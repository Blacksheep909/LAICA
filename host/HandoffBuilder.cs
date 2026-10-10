using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Laica
{
    /// <summary>What a fresh agent needs to carry on someone else's work. Everything here is built by LAICA itself from the chat's event log and the project folder:
    /// no model is called, so it works even when the agent that was working has no usage left.</summary>
    public sealed class HandoffInfo
    {
        public string Goal = "", Latest = "", Stage = "", UpdatedBy = "", UpdatedUtc = "", Constraints = "", NextSteps = "";
        public bool Unfinished; public string LastReply = "";
        public List<string> Requests = new List<string>(), Recent = new List<string>(), Problems = new List<string>();
        public List<string> Done = new List<string>(), InProgress = new List<string>(), Files = new List<string>(), VerifyFirst = new List<string>(), Commands = new List<string>(), FolderOnly = new List<string>();
    }

    public static class HandoffBuilder
    {
        public const string StartMark = "<!-- laica:handoff:start -->", EndMark = "<!-- laica:handoff:end -->";
        public const string PinnedStub = "## Pinned\r\n_Yours. LAICA never changes anything from here down._\r\n";
        static readonly string[] SkipDirs = { ".git", "node_modules", ".venv", "__pycache__", "dist", "build", "obj", "bin", "target", ".next", ".laica", ".vs", ".idea" };
        static readonly string[] EditTools = { "Edit", "MultiEdit", "Write", "NotebookEdit", "str_replace_editor", "apply_patch" };

        // ---------- secrets ----------
        static readonly Regex[] Secrets = {
            new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)", RegexOptions.Compiled),
            new Regex(@"\b(sk|pk|rk)-[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled),
            new Regex(@"\bgh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.Compiled),
            new Regex(@"\bgithub_pat_[A-Za-z0-9_]{20,}", RegexOptions.Compiled),
            new Regex(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}", RegexOptions.Compiled),
            new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled),
            new Regex(@"\bAIza[0-9A-Za-z_\-]{30,}", RegexOptions.Compiled),
            new Regex(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", RegexOptions.Compiled),
            new Regex(@"\bBearer\s+[A-Za-z0-9._\-]{16,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)
        };
        static readonly Regex Assign = new Regex(@"\b([A-Za-z0-9_]*(?:api[_-]?key|secret|token|password|passwd|pwd|credential)[A-Za-z0-9_]*)\s*[:=]\s*[""']?[^\s""',;]{6,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Removes API keys, tokens, passwords and private keys from text before it is written to HANDOFF.md or handed to another agent.</summary>
        public static string Redact(string text)
        {
            if (String.IsNullOrEmpty(text)) return text ?? "";
            foreach (var rx in Secrets) text = rx.Replace(text, "[redacted]");
            return Assign.Replace(text, "$1=[redacted]");
        }

        static string Str(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : ""; }
        static long Num(Dictionary<string, object> d, string k) { object v; if (d == null || !d.TryGetValue(k, out v) || v == null) return 0; try { return Convert.ToInt64(v); } catch (Exception) { return 0; } }
        public static string Clip(string text, int max) { text = (text ?? "").Trim(); return text.Length <= max ? text : text.Substring(0, max) + "..."; }
        static string OneLine(string text, int max) { return Clip(Regex.Replace(text ?? "", @"\s+", " "), max); }

        static string Rel(string cwd, string path)
        {
            if (String.IsNullOrEmpty(path)) return "";
            try
            {
                if (!String.IsNullOrEmpty(cwd) && Path.IsPathRooted(path))
                {
                    string full = Path.GetFullPath(path), root = Path.GetFullPath(cwd).TrimEnd('\\', '/') + "\\";
                    if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return full.Substring(root.Length).Replace('\\', '/');
                }
            }
            catch (Exception) { }
            return path.Replace('\\', '/');
        }

        sealed class Pending { public string Kind, Name, Cmd; public int Index; }

        /// <summary>Reads the event log once, in order, and works out the goal, what is done, what was in flight, which files were touched and which were half-written.</summary>
        public static HandoffInfo Build(IList<Dictionary<string, object>> events, string cwd, string rules, string updatedBy, DateTime nowUtc)
        {
            var info = new HandoffInfo { UpdatedBy = updatedBy ?? "", UpdatedUtc = nowUtc.ToString("o"), Constraints = Redact(Clip(rules, 700)) };
            var js = new JavaScriptSerializer();
            var files = new List<KeyValuePair<string, string>>(); var cmds = new List<string>(); var pending = new List<Pending>(); var done = new List<string>();
            string firstUser = "", lastUser = "", turnFinal = "", turnRequest = ""; bool turnOpen = false, turnFailed = false, lastClean = true;
            DateTime stopAt = DateTime.MinValue; int count = 0;
            var recentEdits = new List<KeyValuePair<string, DateTime>>();
            var requests = new List<string>(); var turns = new List<string>(); var problems = new List<string>(); var turnFiles = new List<string>();
            Action closeTurn = () =>
            {
                if (turnRequest == "") return;
                var sbT = new StringBuilder(); sbT.Append("**You:** ").Append(Clip(turnRequest, 1000)).Append("\r\n\r\n").Append("**Agent:** ").Append(turnFinal == "" ? (turnFailed ? "(stopped before replying)" : "(no reply)") : Clip(turnFinal, 1800));
                if (turnFiles.Count > 0) sbT.Append("\r\n\r\n_Changed: ").Append(String.Join(", ", turnFiles.Distinct().Take(10))).Append("_");
                turns.Add(sbT.ToString()); turnFiles.Clear();
            };
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i]; string kind = Str(e, "Kind"), text = Str(e, "Text"), detail = Str(e, "Detail"); DateTime t = DateTime.MinValue;
                DateTime.TryParse(Str(e, "TimeUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t);
                if (kind == "user")
                {
                    if (turnOpen) { done.Add(Turn(turnRequest, turnFinal, turnFailed)); closeTurn(); }
                    requests.Add(text);
                    if (firstUser == "") firstUser = text; lastUser = text; turnRequest = text; turnFinal = ""; turnOpen = true; turnFailed = false; lastClean = false; stopAt = DateTime.MinValue; count++;
                }
                else if (kind == "assistant") turnFinal = (detail == "append" && turnFinal != "") ? turnFinal + "\n" + text : text;
                else if (kind == "tool")
                {
                    string name = text;
                    if (name == "shell" || name == "Bash" || name == "PowerShell" || name == "run_command") { string c = name == "shell" ? detail : CommandOf(js, detail); pending.Add(new Pending { Kind = "cmd", Name = name, Cmd = c, Index = i }); cmds.Add(c); }
                    else if (name == "file change")
                    {
                        foreach (var ch in ChangesOf(js, detail)) { files.Add(new KeyValuePair<string, string>(Rel(cwd, ch.Key), ch.Value)); turnFiles.Add(Rel(cwd, ch.Key)); if (t != DateTime.MinValue) recentEdits.Add(new KeyValuePair<string, DateTime>(Rel(cwd, ch.Key), t)); }
                    }
                    else if (EditTools.Contains(name))
                    {
                        string p = PathOf(js, detail); if (p != "") { p = Rel(cwd, p); pending.Add(new Pending { Kind = "edit", Name = name, Cmd = p, Index = i }); files.Add(new KeyValuePair<string, string>(p, name == "Write" ? "written" : "edited")); turnFiles.Add(p); if (t != DateTime.MinValue) recentEdits.Add(new KeyValuePair<string, DateTime>(p, t)); }
                    }
                }
                else if (kind == "tool_result")
                {
                    int at = -1;
                    if (detail != "" && detail != "error") at = pending.FindIndex(x => x.Kind == "cmd" && x.Cmd == detail);
                    if (at < 0 && pending.Count > 0) at = 0;
                    if (at >= 0) { var p = pending[at]; pending.RemoveAt(at); if (p.Kind == "cmd") { int ci = cmds.LastIndexOf(p.Cmd); if (ci >= 0 && detail == "error") cmds[ci] = p.Cmd + "  [failed: " + OneLine(text, 200) + "]"; } }
                }
                else if (kind == "error" || kind == "limit") { turnFailed = true; if (stopAt == DateTime.MinValue || t > stopAt) stopAt = t; if (kind == "error") problems.Add(OneLine(text, 260)); }
                else if (kind == "done") { if (turnOpen) { done.Add(Turn(turnRequest, turnFinal, turnFailed)); closeTurn(); lastClean = !turnFailed && turnFinal != ""; turnOpen = false; } }
            }
            if (turnOpen) { lastClean = false; closeTurn(); }
            info.Goal = Redact(Clip(firstUser, 800)); info.Latest = Redact(Clip(lastUser, 4000));
            info.Unfinished = lastUser != "" && !lastClean; info.LastReply = Redact(Clip(turnFinal, 600));
            for (int i = Math.Max(0, requests.Count - 30), n = i + 1; i < requests.Count; i++, n++) info.Requests.Add(n + ". " + Redact(OneLine(requests[i], 260)));
            foreach (string d in turns.Skip(Math.Max(0, turns.Count - 6))) info.Recent.Add(Redact(d));
            foreach (string pr in problems.Skip(Math.Max(0, problems.Count - 5))) info.Problems.Add(Redact(pr));
            foreach (string d in done.Skip(Math.Max(0, done.Count - 30))) info.Done.Add(Redact(d));
            if (info.Unfinished) { info.InProgress.Add("Unfinished request: " + Redact(OneLine(lastUser, 400))); if (turnFinal != "") info.InProgress.Add("Last thing the agent said: " + Redact(OneLine(turnFinal, 400))); }
            // newest first, one line per file
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = files.Count - 1; i >= 0 && info.Files.Count < 80; i--) if (files[i].Key != "" && seen.Add(files[i].Key)) info.Files.Add(files[i].Key + " (" + files[i].Value + ")");
            for (int i = cmds.Count - 1; i >= 0 && info.Commands.Count < 20; i--) if (!String.IsNullOrWhiteSpace(cmds[i])) info.Commands.Add(Redact(OneLine(cmds[i], 360)));
            // half-written files: an edit that never got a result, or one touched in the last moments before the agent stopped
            var verify = new List<string>();
            if (info.Unfinished) foreach (var p in pending) if (p.Kind == "edit" && !verify.Contains(p.Cmd)) verify.Add(p.Cmd);
            if (info.Unfinished && stopAt != DateTime.MinValue && !String.IsNullOrEmpty(cwd)) foreach (string f in RecentlyWritten(cwd, stopAt, 20, 12)) if (!verify.Contains(f)) verify.Add(f);
            info.VerifyFirst = verify.Take(15).ToList();
            // files that changed in the folder that no event explains (shell commands, formatters, the user)
            try { var known = new HashSet<string>(info.Files.Select(x => x.Substring(0, x.LastIndexOf(" ("))), StringComparer.OrdinalIgnoreCase); DateTime first = FirstTime(events); foreach (string f in FolderChanges(cwd, first, 30)) if (!known.Contains(f)) info.FolderOnly.Add(f); } catch (Exception) { }
            info.Stage = count == 0 ? "No requests yet." : (info.Unfinished ? "Interrupted part-way through request " + count + "." : "Idle after request " + count + " (finished).");
            return info;
        }

        static string Turn(string request, string final, bool failed) { return OneLine(request, 90) + "  ->  " + (final == "" ? (failed ? "stopped before finishing" : "no reply") : OneLine(FirstSentence(final), 160)) + (failed && final != "" ? "  [interrupted]" : ""); }
        static string FirstSentence(string t) { t = (t ?? "").Trim(); int i = t.IndexOfAny(new[] { '\n' }); if (i > 0) t = t.Substring(0, i); return t; }
        static DateTime FirstTime(IList<Dictionary<string, object>> ev) { foreach (var e in ev) { DateTime t; if (DateTime.TryParse(Str(e, "TimeUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)) return t; } return DateTime.UtcNow.AddDays(-1); }

        static string CommandOf(JavaScriptSerializer js, string detail) { try { var d = js.Deserialize<Dictionary<string, object>>(detail); return Str(d, "command"); } catch (Exception) { return detail ?? ""; } }
        static string PathOf(JavaScriptSerializer js, string detail)
        {
            try { var d = js.Deserialize<Dictionary<string, object>>(detail); string p = Str(d, "file_path"); if (p == "") p = Str(d, "notebook_path"); if (p == "") p = Str(d, "path"); return p; } catch (Exception) { return ""; }
        }
        static List<KeyValuePair<string, string>> ChangesOf(JavaScriptSerializer js, string detail)
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                var arr = js.DeserializeObject(detail) as System.Collections.IEnumerable; if (arr == null) return list;
                foreach (object o in arr) { var d = o as Dictionary<string, object>; if (d == null) continue; string p = Str(d, "path"), k = Str(d, "kind"); if (k.Length > 0 && k[0] == '{') k = ""; if (p != "") list.Add(new KeyValuePair<string, string>(p, k == "add" ? "created" : k == "delete" ? "deleted" : "edited")); }
            }
            catch (Exception) { }
            return list;
        }

        // ---------- the folder ----------
        static bool Skipped(string rel)
        {
            string[] parts = rel.Replace('\\', '/').Split('/');
            for (int i = 0; i < parts.Length - 1; i++) if (SkipDirs.Contains(parts[i], StringComparer.OrdinalIgnoreCase)) return true;
            string n = parts[parts.Length - 1]; return n.StartsWith("HANDOFF", StringComparison.OrdinalIgnoreCase);
        }
        static IEnumerable<FileInfo> Walk(string root, int limit)
        {
            var stack = new Stack<string>(); stack.Push(root); int n = 0;
            while (stack.Count > 0 && n < limit)
            {
                string d = stack.Pop(); string[] sub, fs;
                try { sub = Directory.GetDirectories(d); fs = Directory.GetFiles(d); } catch (Exception) { continue; }
                foreach (string s in sub) { if (!SkipDirs.Contains(Path.GetFileName(s), StringComparer.OrdinalIgnoreCase)) stack.Push(s); }
                foreach (string f in fs) { if (++n > limit) yield break; FileInfo fi; try { fi = new FileInfo(f); } catch (Exception) { continue; } yield return fi; }
            }
        }
        static string RelTo(string root, FileInfo f) { string r = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\"; return f.FullName.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? f.FullName.Substring(r.Length).Replace('\\', '/') : f.FullName; }

        /// <summary>Files written within a few seconds either side of a moment (the moment an agent ran out of usage), by file time. Works with or without git.</summary>
        public static List<string> RecentlyWritten(string cwd, DateTime moment, int seconds, int max)
        {
            var list = new List<string>();
            try { if (!Directory.Exists(cwd)) return list; foreach (var fi in Walk(cwd, 6000)) { double d = (moment - fi.LastWriteTimeUtc).TotalSeconds; if (d >= -5 && d <= seconds) { string r = RelTo(cwd, fi); if (!Skipped(r)) list.Add(r); } if (list.Count >= max) break; } } catch (Exception) { }
            return list;
        }

        /// <summary>Files in the folder changed since a time: git's own list inside a repository, file times otherwise (LAICA never runs git init for you).</summary>
        public static List<string> FolderChanges(string cwd, DateTime sinceUtc, int max)
        {
            var list = new List<string>();
            try
            {
                if (String.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) return list;
                string root = GitTools.Root(cwd);
                if (root != null) { foreach (var f in GitTools.Status(root)) { string p = Str(f, "Path"); if (p != "" && !Skipped(p)) list.Add(p); if (list.Count >= max) break; } return list; }
                foreach (var fi in Walk(cwd, 6000).OrderByDescending(x => x.LastWriteTimeUtc)) { if (fi.LastWriteTimeUtc < sinceUtc) break; string r = RelTo(cwd, fi); if (!Skipped(r)) list.Add(r); if (list.Count >= max) break; }
            }
            catch (Exception) { }
            return list;
        }

        /// <summary>A cheap fingerprint of the project folder: it changes when any file is added, removed or saved. Used to tell whether the work is really moving forward.</summary>
        public static string FolderStamp(string cwd)
        {
            try
            {
                if (String.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) return "";
                long newest = 0, total = 0; int n = 0;
                foreach (var fi in Walk(cwd, 8000)) { if (Skipped(RelTo(cwd, fi))) continue; n++; total += fi.Length; if (fi.LastWriteTimeUtc.Ticks > newest) newest = fi.LastWriteTimeUtc.Ticks; }
                return n + "|" + total + "|" + newest;
            }
            catch (Exception) { return ""; }
        }

        // ---------- HANDOFF.md ----------
        static void Section(StringBuilder sb, string title, IEnumerable<string> items, string empty)
        {
            sb.Append("### ").Append(title).Append("\r\n"); bool any = false;
            foreach (string s in items) { sb.Append("- ").Append(s).Append("\r\n"); any = true; }
            if (!any) sb.Append("_").Append(empty).Append("_\r\n");
            sb.Append("\r\n");
        }

        /// <summary>"src/a.ts (edited)" -> "`src/a.ts` (edited)": a path in code quotes is shown as a link in LAICA and reads well in an editor.</summary>
        static string TickPath(string s) { int i = s.LastIndexOf(" ("); return i > 0 ? "`" + s.Substring(0, i) + "`" + s.Substring(i) : "`" + s + "`"; }

        public static string RenderManaged(HandoffInfo h)
        {
            var sb = new StringBuilder();
            sb.Append(StartMark).Append("\r\n").Append("# Project handoff\r\n");
            sb.Append("_This block is rewritten by LAICA after every turn so any agent can pick the work up. Put your own notes under Pinned at the bottom: LAICA never touches those._\r\n\r\n");
            sb.Append("Last updated by ").Append(h.UpdatedBy == "" ? "LAICA" : h.UpdatedBy).Append(" at ").Append(h.UpdatedUtc).Append("\r\n\r\n");
            sb.Append("### Goal\r\n").Append(h.Goal == "" ? "_No request yet._" : h.Goal).Append("\r\n\r\n");
            if (h.Requests.Count > 1) Section(sb, "Everything the user has asked, in order", h.Requests, "None.");
            if (h.Latest != "" && h.Latest != h.Goal) sb.Append("### Latest request from the user\r\n").Append(h.Latest).Append("\r\n\r\n");
            sb.Append("### Current stage\r\n").Append(h.Stage == "" ? "Not started." : h.Stage).Append("\r\n\r\n");
            Section(sb, "Done, turn by turn", h.Done, "Nothing finished yet.");
            if (h.Recent.Count > 0) { sb.Append("### Recent conversation (newest last)\r\n"); foreach (string r in h.Recent) sb.Append(r).Append("\r\n\r\n---\r\n\r\n"); }
            var next = new List<string>(h.InProgress); if (h.NextSteps != "") next.Add("Agent note: " + h.NextSteps);
            Section(sb, "In progress and next steps", next, "Nothing waiting.");
            Section(sb, "Verify first (may be half-written)", h.VerifyFirst.Select(TickPath), "None.");
            Section(sb, "Files touched", h.Files.Select(TickPath).Concat(h.FolderOnly.Select(f => TickPath(f + " (changed in the folder)"))), "No files changed yet.");
            Section(sb, "Recent commands", h.Commands, "None.");
            if (h.Problems.Count > 0) Section(sb, "Problems reported", h.Problems, "None.");
            sb.Append("### Active constraints\r\n").Append(h.Constraints == "" ? "_None recorded._" : h.Constraints).Append("\r\n").Append(EndMark);
            return sb.ToString();
        }

        /// <summary>Puts a fresh managed block into existing file text. Everything outside the block (the Pinned section, anything the user wrote) is kept exactly.</summary>
        public static string Merge(string existing, string managed)
        {
            if (String.IsNullOrEmpty(existing)) return managed + "\r\n\r\n" + PinnedStub;
            int a = existing.IndexOf(StartMark, StringComparison.Ordinal), b = existing.IndexOf(EndMark, StringComparison.Ordinal);
            if (a >= 0 && b > a) return existing.Substring(0, a) + managed + existing.Substring(b + EndMark.Length);
            return managed + "\r\n\r\n" + PinnedStub + "\r\n" + existing;
        }

        public static string ManagedOf(string text) { if (text == null) return ""; int a = text.IndexOf(StartMark, StringComparison.Ordinal), b = text.IndexOf(EndMark, StringComparison.Ordinal); return a >= 0 && b > a ? text.Substring(a, b + EndMark.Length - a) : ""; }

        /// <summary>The user-owned part: everything after the managed block, without the stub heading LAICA adds.</summary>
        public static string PinnedOf(string text)
        {
            if (String.IsNullOrEmpty(text)) return "";
            int b = text.IndexOf(EndMark, StringComparison.Ordinal); string rest = b >= 0 ? text.Substring(b + EndMark.Length) : text;
            rest = rest.Trim('\r', '\n', ' ');
            string stub = PinnedStub.Trim();
            if (rest.StartsWith(stub, StringComparison.Ordinal)) rest = rest.Substring(stub.Length).Trim('\r', '\n', ' ');
            return rest;
        }

        public static string WithPinned(string existing, string pinned)
        {
            string managed = ManagedOf(existing); if (managed == "") managed = StartMark + "\r\n# Project handoff\r\n_Nothing recorded yet. LAICA fills this in after the first turn._\r\n" + EndMark;
            return managed + "\r\n\r\n" + PinnedStub + (String.IsNullOrWhiteSpace(pinned) ? "" : "\r\n" + pinned.Trim() + "\r\n");
        }

        static readonly Regex UpdatedRx = new Regex(@"Last updated by (.*?) at (\S+)", RegexOptions.Compiled);
        public static bool ParseUpdated(string text, out string by, out string utc) { var m = UpdatedRx.Match(text ?? ""); by = m.Success ? m.Groups[1].Value : ""; utc = m.Success ? m.Groups[2].Value : ""; return m.Success; }

        // ---------- what the next agent is told ----------
        /// <summary>Only what happened after sequence number afterSeq: never the whole conversation. The reader is pointed at HANDOFF.md for anything else.</summary>
        public static string Delta(IList<Dictionary<string, object>> events, long afterSeq, string fromName, string cwd, string handoffName, int maxChars)
        {
            var sb = new StringBuilder(); var js = new JavaScriptSerializer();
            var fresh = events.Where(e => Num(e, "N") > afterSeq).ToList();
            string lastUser = "", lastSaid = ""; var files = new List<string>(); var cmds = new List<string>(); var errors = new List<string>(); var pending = new List<string>();
            var talk = new List<KeyValuePair<string, string>>();   // who said what, in order
            foreach (var e in fresh)
            {
                string kind = Str(e, "Kind"), text = Str(e, "Text"), detail = Str(e, "Detail");
                if (kind == "user") { lastUser = text; talk.Add(new KeyValuePair<string, string>("user", text)); }
                else if (kind == "assistant")
                {
                    lastSaid = (Str(e, "Detail") == "append" && lastSaid != "") ? lastSaid + "\n" + text : text;
                    if (Str(e, "Detail") == "append" && talk.Count > 0 && talk[talk.Count - 1].Key == "agent") talk[talk.Count - 1] = new KeyValuePair<string, string>("agent", talk[talk.Count - 1].Value + "\n" + text); else talk.Add(new KeyValuePair<string, string>("agent", text));
                }
                else if (kind == "tool")
                {
                    if (text == "shell" || text == "Bash" || text == "PowerShell") cmds.Add(text == "shell" ? detail : CommandOf(js, detail));
                    else if (text == "file change") foreach (var c in ChangesOf(js, detail)) files.Add(Rel(cwd, c.Key) + " (" + c.Value + ")");
                    else if (EditTools.Contains(text)) { string p = PathOf(js, detail); if (p != "") { files.Add(Rel(cwd, p) + " (" + (text == "Write" ? "written" : "edited") + ")"); pending.Add(Rel(cwd, p)); } }
                }
                else if (kind == "tool_result") { if (pending.Count > 0) pending.RemoveAt(0); if (detail == "error" && cmds.Count > 0) cmds[cmds.Count - 1] = cmds[cmds.Count - 1] + "  [failed: " + OneLine(text, 200) + "]"; }
                else if (kind == "error" || kind == "limit") errors.Add(OneLine(text, 260));
            }
            sb.Append("WHAT HAPPENED SINCE YOU LAST WORKED ON THIS (").Append(fresh.Count).Append(" new events; this is only the new part, not the whole conversation)\n");
            if (fresh.Count == 0) sb.Append("- Nothing new in the chat. Check the files before editing.\n");
            if (talk.Count > 0)
            {
                sb.Append("\nTHE CONVERSATION SINCE (oldest first):\n");
                foreach (var tk in talk.Skip(Math.Max(0, talk.Count - 14)))
                    sb.Append(tk.Key == "user" ? "- The user wrote: " : "- " + fromName + " replied: ").Append(Clip(tk.Value, tk.Key == "user" ? 2000 : 2200)).Append("\n");
                sb.Append("\n");
            }
            else if (lastUser != "") sb.Append("- Latest request from the user: ").Append(Clip(lastUser, 3000)).Append("\n");
            var distinct = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); for (int i = files.Count - 1; i >= 0; i--) if (seen.Add(files[i])) distinct.Add(files[i]);
            if (distinct.Count > 0) sb.Append("- Files ").Append(fromName).Append(" changed: ").Append(String.Join(", ", distinct.Take(60))).Append("\n");
            if (cmds.Count > 0) sb.Append("- Commands run: ").Append(String.Join(" | ", cmds.Skip(Math.Max(0, cmds.Count - 15)).Select(c => OneLine(c, 320)))).Append("\n");
            if (errors.Count > 0) sb.Append("- Problems reported: ").Append(String.Join(" | ", errors.Skip(Math.Max(0, errors.Count - 5)))).Append("\n");
            if (pending.Count > 0) sb.Append("- VERIFY FIRST (edits with no result, may be half-written): ").Append(String.Join(", ", pending.Distinct().Take(15))).Append("\n");
            sb.Append("- Full project state, decisions and notes are in ").Append(handoffName).Append(" in the working folder. Read it only if you need more than this.\n");
            return Redact(Clip(sb.ToString(), maxChars));
        }
    }
}
