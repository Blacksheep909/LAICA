using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Laica
{
    /// <summary>Thin, safe wrapper over the git CLI plus a cheap folder snapshot used to notice changes made while a chat was away.</summary>
    public static class GitTools
    {
        static string exe; static bool searched;
        public static string Exe()
        {
            if (searched) return exe; searched = true;
            var candidates = new List<string>();
            foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) { try { candidates.Add(Path.Combine(d.Trim(), "git.exe")); } catch (Exception) { } }
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(pf, "Git", "cmd", "git.exe")); candidates.Add(Path.Combine(local, "Programs", "Git", "cmd", "git.exe"));
            try { string rt = Path.Combine(home, ".cache", "codex-runtimes"); if (Directory.Exists(rt)) candidates.AddRange(Directory.GetFiles(rt, "git.exe", SearchOption.AllDirectories).Where(p => p.EndsWith(Path.Combine("cmd", "git.exe"), StringComparison.OrdinalIgnoreCase))); } catch (Exception) { }
            exe = candidates.FirstOrDefault(File.Exists); return exe;
        }
        public static bool Available { get { return Exe() != null; } }

        /// <summary>Runs git and returns stdout. Throws with git's own message when the exit code is non-zero and <paramref name="check"/> is set.</summary>
        public static string Run(string cwd, string args, bool check = true, int timeoutMs = 60000)
        {
            if (Exe() == null) throw new InvalidOperationException("Git isn't installed on this computer.");
            var psi = new ProcessStartInfo { FileName = Exe(), Arguments = "-c core.quotepath=false -c core.autocrlf=false " + args, WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0"; psi.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
            using (var p = Process.Start(psi))
            {
                var err = new StringBuilder(); p.ErrorDataReceived += (o, e) => { if (e.Data != null) err.AppendLine(e.Data); }; p.BeginErrorReadLine();
                var output = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } throw new TimeoutException("git " + args.Split(' ')[0] + " timed out."); }
                p.WaitForExit(); string text = output.Result;
                if (check && p.ExitCode != 0) throw new InvalidOperationException((err.ToString().Trim() != "" ? err.ToString().Trim() : text.Trim()) + "");
                return text;
            }
        }
        static string Q(string v) { return "\"" + v.Replace("\"", "\\\"") + "\""; }

        public static string Root(string dir)
        {
            try { if (!Available || !Directory.Exists(dir)) return null; string r = Run(dir, "rev-parse --show-toplevel", false).Trim(); return r == "" ? null : Path.GetFullPath(r); } catch (Exception) { return null; }
        }
        public static string Branch(string dir) { try { return Run(dir, "rev-parse --abbrev-ref HEAD", false).Trim(); } catch (Exception) { return ""; } }
        public static string Head(string dir) { try { return Run(dir, "rev-parse HEAD", false).Trim(); } catch (Exception) { return ""; } }

        public static List<Dictionary<string, object>> Status(string dir)
        {
            var list = new List<Dictionary<string, object>>();
            foreach (string line in Run(dir, "status --porcelain=v1 --untracked-files=all").Split('\n'))
            {
                if (line.Length < 4) continue;
                string code = line.Substring(0, 2).Trim(), path = line.Substring(3).Trim();
                int arrow = path.IndexOf(" -> "); if (arrow >= 0) path = path.Substring(arrow + 4);
                list.Add(new Dictionary<string, object> { { "Status", code == "??" ? "A" : code.Substring(0, 1) }, { "Untracked", code == "??" }, { "Path", path.Trim('"') } });
            }
            return list;
        }
        public static string Diff(string dir, string path)
        {
            string full = Path.GetFullPath(Path.Combine(dir, path));
            string tracked = Run(dir, "diff HEAD --no-color -- " + Q(path), false);
            if (tracked.Trim() != "") return Cap(tracked);
            if (File.Exists(full)) { var lines = File.ReadAllText(full).Split('\n'); return Cap("--- /dev/null\n+++ b/" + path + "\n" + String.Join("\n", lines.Select(l => "+" + l.TrimEnd('\r')))); }
            return "";
        }
        static string Cap(string s) { return s.Length > 400000 ? s.Substring(0, 400000) + "\n… (truncated)" : s; }

        // ---------- folder snapshot ("what changed while I was away") ----------
        static readonly string[] SkipDirs = { ".git", "node_modules", ".venv", "__pycache__", "dist", "build", "obj", "bin", "target", ".next", ".laica" };
        public static Dictionary<string, string> Snapshot(string root, int limit = 8000)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>(); stack.Push(root);
            while (stack.Count > 0 && map.Count < limit)
            {
                string d = stack.Pop();
                try
                {
                    foreach (string sub in Directory.GetDirectories(d)) { string n = Path.GetFileName(sub); if (SkipDirs.Contains(n, StringComparer.OrdinalIgnoreCase)) continue; if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue; stack.Push(sub); }
                    foreach (string f in Directory.GetFiles(d)) { if (map.Count >= limit) break; var fi = new FileInfo(f); map[f.Substring(root.Length).TrimStart('\\', '/')] = fi.Length + ":" + fi.LastWriteTimeUtc.Ticks; }
                }
                catch (Exception) { }
            }
            return map;
        }
        /// <summary>Plain-language list of changes between two snapshots, or null when nothing changed.</summary>
        public static string DescribeDrift(Dictionary<string, string> before, Dictionary<string, string> after, string headBefore, string headAfter, string dir)
        {
            if (before == null) return null;
            var added = after.Keys.Where(k => !before.ContainsKey(k)).OrderBy(k => k).ToList();
            var removed = before.Keys.Where(k => !after.ContainsKey(k)).OrderBy(k => k).ToList();
            var changed = after.Where(kv => before.ContainsKey(kv.Key) && before[kv.Key] != kv.Value).Select(kv => kv.Key).OrderBy(k => k).ToList();
            string commits = "";
            if (!String.IsNullOrEmpty(headBefore) && !String.IsNullOrEmpty(headAfter) && headBefore != headAfter) { try { commits = Run(dir, "log --oneline -n 10 " + headBefore + ".." + headAfter, false).Trim(); } catch (Exception) { } }
            if (added.Count + removed.Count + changed.Count == 0 && commits == "") return null;
            var sb = new StringBuilder("Since your last turn in this folder, changes were made outside this conversation:\n");
            Action<string, List<string>> add = (label, items) => { if (items.Count == 0) return; sb.Append("- " + label + " (" + items.Count + "): " + String.Join(", ", items.Take(25)) + (items.Count > 25 ? ", …" : "") + "\n"); };
            add("modified", changed); add("new", added); add("deleted", removed);
            if (commits != "") sb.Append("- new commits:\n  " + commits.Replace("\n", "\n  ") + "\n");
            sb.Append("Re-read any file you rely on before editing it.");
            return sb.ToString();
        }
    }
}
