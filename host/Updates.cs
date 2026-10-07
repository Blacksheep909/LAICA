using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Laica
{
    /// <summary>
    /// Keeps LAICA and its MCP servers current.
    /// LAICA: asks GitHub for the latest release now and every few hours, and (when you click Update) downloads the installer, checks its SHA-256 against the release's sums file
    /// and runs it over the current install. Nothing is installed without a click.
    /// MCP servers: servers launched with npx or uvx are rewritten to ask for the package's latest version on every launch ("pkg@latest"), so they never go stale.
    /// Servers pinned to an explicit version on purpose are left alone.
    /// </summary>
    public sealed partial class HarnessManager
    {
        const string UpdateRepo = "Blacksheep909/LAICA";
        Dictionary<string, object> upd; Timer updTimer; bool updBusy;
        /// <summary>Test hook: replaces the HTTPS GET used for the release lookup.</summary>
        internal Func<string, string> UpdateHttp { get; set; }

        Dictionary<string, object> UpdateState()
        {
            lock (gate)
            {
                if (upd != null) return upd;
                var l = LoadList("updates.json"); var c = l.Count > 0 ? l[0] : new Dictionary<string, object>();
                if (!c.ContainsKey("Auto")) c["Auto"] = true;
                return upd = c;
            }
        }
        void SaveUpdateState() { lock (gate) SaveList("updates.json", new List<Dictionary<string, object>> { UpdateState() }); }

        /// <summary>Starts the background checks. Called by the desktop app only (never by tests).</summary>
        public void StartUpdateChecks()
        {
            if (updTimer != null) return;
            updTimer = new Timer(_ => { try { if (Convert.ToBoolean(UpdateState()["Auto"])) CheckUpdates(false); } catch (Exception) { } }, null, 25000, 6 * 3600 * 1000);
        }

        static string HttpGetText(string url)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var rq = (HttpWebRequest)WebRequest.Create(url); rq.UserAgent = "LAICA-updater"; rq.Accept = "application/vnd.github+json"; rq.Timeout = 15000; rq.ReadWriteTimeout = 15000;
            using (var rs = (HttpWebResponse)rq.GetResponse()) using (var sr = new StreamReader(rs.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
        }

        static bool NewerThan(string latest, string current)
        {
            Version a, b; if (!Version.TryParse(latest, out a) || !Version.TryParse(current, out b)) return false; return a > b;
        }

        /// <summary>Looks up the newest GitHub release. Returns true when it is newer than this build.</summary>
        bool CheckLaica()
        {
            var st = UpdateState(); string err = "";
            try
            {
                string body = (UpdateHttp ?? HttpGetText)("https://api.github.com/repos/" + UpdateRepo + "/releases/latest");
                var r = json.Deserialize<Dictionary<string, object>>(body); string tag = Str(r, "tag_name").TrimStart('v', 'V');
                string setup = "", sums = "", name = ""; var assets = Arr(r, "assets");
                if (assets != null) foreach (object o in assets)
                    {
                        var a = o as Dictionary<string, object>; if (a == null) continue; string n = Str(a, "name"), u = Str(a, "browser_download_url");
                        if (!u.StartsWith("https://github.com/" + UpdateRepo + "/releases/download/", StringComparison.Ordinal)) continue;
                        if (Regex.IsMatch(n, @"^LAICA-Setup-[0-9.]+\.exe$")) { setup = u; name = n; } else if (Regex.IsMatch(n, @"^SHA256SUMS-[0-9.]+\.txt$")) sums = u;
                    }
                lock (gate) { st["Latest"] = tag; st["SetupUrl"] = setup; st["SetupName"] = name; st["SumsUrl"] = sums; st["Notes"] = Str(r, "body"); st["Page"] = Str(r, "html_url"); }
            }
            catch (Exception ex) { err = ex.Message; }
            lock (gate) { st["LaicaCheckedUtc"] = DateTime.UtcNow.ToString("o"); st["LaicaError"] = err; }
            SaveUpdateState();
            return err == "" && NewerThan(Str(st, "Latest"), AppVersion.Value);
        }

        /// <summary>Checks LAICA and refreshes the MCP servers. <paramref name="force"/> also repeats the MCP pass inside its once-a-day window.</summary>
        public object CheckUpdates(bool force)
        {
            lock (gate) { if (updBusy) return UpdatesGet(); updBusy = true; }
            try
            {
                CheckLaica();
                var st = UpdateState(); DateTime last;
                bool due = force || !DateTime.TryParse(Str(st, "McpCheckedUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out last) || (DateTime.UtcNow - last).TotalHours >= 20;
                if (due) { try { McpFreshen(); } catch (Exception) { } }
            }
            finally { lock (gate) updBusy = false; }
            Raise(); return UpdatesGet();
        }

        public object UpdatesGet()
        {
            var st = UpdateState(); string cur = AppVersion.Value, latest = Str(st, "Latest");
            lock (gate)
                return new Dictionary<string, object> {
                    { "Auto", Convert.ToBoolean(st["Auto"]) }, { "Current", cur }, { "Latest", latest }, { "Available", NewerThan(latest, cur) && Str(st, "SetupUrl") != "" }, { "Notes", Str(st, "Notes") }, { "Page", Str(st, "Page") },
                    { "CheckedUtc", Str(st, "LaicaCheckedUtc") }, { "Error", Str(st, "LaicaError") }, { "CanInstall", CanSelfUpdate() },
                    { "McpCheckedUtc", Str(st, "McpCheckedUtc") }, { "McpUpdated", st.ContainsKey("McpUpdated") && st["McpUpdated"] is System.Collections.IEnumerable && !(st["McpUpdated"] is string) ? ((System.Collections.IEnumerable)st["McpUpdated"]).Cast<object>().Select(Convert.ToString).ToArray() : new string[0] } };
        }

        public object UpdatesSet(Dictionary<string, object> d)
        {
            var st = UpdateState(); if (d != null && d.ContainsKey("Auto")) lock (gate) st["Auto"] = Convert.ToBoolean(d["Auto"]); SaveUpdateState(); return UpdatesGet();
        }

        static bool CanSelfUpdate()
        {
            try { var asm = System.Reflection.Assembly.GetEntryAssembly(); return asm != null && String.Equals(Path.GetFileName(asm.Location), "LAICA.exe", StringComparison.OrdinalIgnoreCase); } catch (Exception) { return false; }
        }

        /// <summary>Downloads the newest installer, verifies it and runs it over this install, then restarts LAICA.</summary>
        public object UpdateInstall()
        {
            if (!CanSelfUpdate()) throw new InvalidOperationException("Updating works from the installed LAICA app.");
            if (!Convert.ToBoolean(((Dictionary<string, object>)UpdatesGet())["Available"])) throw new InvalidOperationException("LAICA is already up to date.");
            var st = UpdateState(); string url = Str(st, "SetupUrl"), sumsUrl = Str(st, "SumsUrl"), name = Str(st, "SetupName");
            if (!url.StartsWith("https://github.com/" + UpdateRepo + "/releases/download/", StringComparison.Ordinal) || !Regex.IsMatch(name, @"^LAICA-Setup-[0-9.]+\.exe$")) throw new InvalidOperationException("The update address isn't trusted.");
            string dirTmp = Path.Combine(Path.GetTempPath(), "laica-update"); Directory.CreateDirectory(dirTmp); string setup = Path.Combine(dirTmp, name);
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            using (var wc = new WebClient()) { wc.Headers["User-Agent"] = "LAICA-updater"; wc.DownloadFile(url, setup); }
            if (sumsUrl != "")
            {
                string sums = (UpdateHttp ?? HttpGetText)(sumsUrl); string expected = null;
                foreach (string line in sums.Split('\n')) { var m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$"); if (m.Success && m.Groups[2].Value.Trim() == name) expected = m.Groups[1].Value.ToLowerInvariant(); }
                string actual; using (var sha = SHA256.Create()) using (var fs = File.OpenRead(setup)) actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                if (expected == null || expected != actual) { try { File.Delete(setup); } catch (Exception) { } throw new InvalidOperationException("The downloaded installer failed its checksum, so it was not run."); }
            }
            string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); string exe = Path.Combine(appDir, "LAICA.exe");
            string ps = "Start-Sleep -Seconds 2; Start-Process -FilePath '" + setup.Replace("'", "''") + "' -ArgumentList '/S','\"/D=" + appDir.Replace("'", "''") + "\"' -Wait; Start-Process -FilePath '" + exe.Replace("'", "''") + "'";
            string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand " + enc) { CreateNoWindow = true, UseShellExecute = false });
            new Thread(() => { Thread.Sleep(900); Environment.Exit(0); }) { IsBackground = true }.Start();
            return true;
        }

        // ---------- MCP servers ----------
        static readonly Regex UnversionedPkg = new Regex(@"^(@[a-z0-9~_.-]+/)?[a-z0-9~_.-]+$", RegexOptions.IgnoreCase);

        /// <summary>For one MCP server definition (command plus args) returns the unversioned npx/uvx package argument that should become "pkg@latest", or null.</summary>
        internal static string StalePackage(string command, IList<string> args)
        {
            string c = Path.GetFileNameWithoutExtension((command ?? "").Replace('"', ' ').Trim()).ToLowerInvariant(); int start = -1; string runner = "";
            if (c == "npx" || c == "uvx") { start = 0; runner = c; }
            else if (c == "cmd" || c == "powershell" || c == "pwsh")
                for (int i = 0; i < args.Count; i++) { string a = Path.GetFileNameWithoutExtension(args[i] ?? "").ToLowerInvariant(); if (a == "npx" || a == "uvx") { start = i + 1; runner = a; break; } }
            if (start < 0) return null;
            for (int i = start; i < args.Count; i++)
            {
                string a = args[i] ?? ""; if (a == "") return null;
                if (a.StartsWith("-"))
                {
                    if (a == "-p" || a == "--package" || a == "--from" || a == "--with" || a == "--python" || a == "-w") return null;   // custom package wiring: leave it alone
                    continue;
                }
                return UnversionedPkg.IsMatch(a) ? a : null;
            }
            return null;
        }

        /// <summary>Makes every installed npx/uvx MCP server (Claude Code and Codex) fetch its latest version at launch. Returns the names that changed.</summary>
        public object McpFreshen()
        {
            var changed = new List<string>();
            try { changed.AddRange(FreshenClaude()); } catch (Exception) { }
            try { changed.AddRange(FreshenCodex()); } catch (Exception) { }
            var st = UpdateState(); lock (gate) { st["McpCheckedUtc"] = DateTime.UtcNow.ToString("o"); if (changed.Count > 0) st["McpUpdated"] = changed.Distinct().ToArray(); }
            SaveUpdateState(); if (changed.Count > 0) Raise();
            return changed.ToArray();
        }

        static void BackupOnce(string path) { try { string b = path + ".laica-backup"; if (!File.Exists(b)) File.Copy(path, b); } catch (Exception) { } }

        List<string> FreshenClaude()
        {
            var done = new List<string>(); string cj = ClaudeJson(); if (!File.Exists(cj)) return done;
            string text = File.ReadAllText(cj, new UTF8Encoding(false)); var d = json.Deserialize<Dictionary<string, object>>(text); var m = Obj(d, "mcpServers"); if (m == null) return done;
            foreach (var k in m)
            {
                var v = k.Value as Dictionary<string, object>; if (v == null) continue; var al = v.ContainsKey("args") ? v["args"] as System.Collections.IList : null; if (al == null) continue;
                var args = al.Cast<object>().Select(Convert.ToString).ToList(); string pkg = StalePackage(Str(v, "command"), args); if (pkg == null) continue;
                int at = text.IndexOf("\"" + k.Key + "\"", StringComparison.Ordinal); if (at < 0) continue; int ai = text.IndexOf("\"args\"", at, StringComparison.Ordinal); if (ai < 0) continue; int open = text.IndexOf('[', ai), close = open < 0 ? -1 : text.IndexOf(']', open); if (close < 0) continue;
                string block = text.Substring(open, close - open + 1), lit = "\"" + pkg + "\""; if (!block.Contains(lit)) continue;
                text = text.Substring(0, open) + block.Replace(lit, "\"" + pkg + "@latest\"") + text.Substring(close + 1); done.Add(k.Key);
            }
            if (done.Count > 0) { BackupOnce(cj); string tmp = cj + ".laica-tmp"; File.WriteAllText(tmp, text, new UTF8Encoding(false)); json.Deserialize<Dictionary<string, object>>(File.ReadAllText(tmp)); File.Delete(cj); File.Move(tmp, cj); }
            return done;
        }

        List<string> FreshenCodex()
        {
            var done = new List<string>(); string ct = Path.Combine(CodexHomeDir(), "config.toml"); if (!File.Exists(ct)) return done;
            string text = File.ReadAllText(ct, new UTF8Encoding(false)); string result = text;
            foreach (Match sec in Regex.Matches(text, @"(?ms)^\[mcp_servers\.(""[^""]+""|[^\].]+)\]\s*\r?\n(.*?)(?=^\[|\z)"))
            {
                string body = sec.Groups[2].Value; var cm = Regex.Match(body, @"(?m)^command\s*=\s*""([^""]*)"""); var am = Regex.Match(body, @"(?m)^args\s*=\s*\[([^\]]*)\]");
                if (!cm.Success || !am.Success) continue;
                var args = Regex.Matches(am.Groups[1].Value, @"""((?:[^""\\]|\\.)*)""").Cast<Match>().Select(x => x.Groups[1].Value.Replace("\\\\", "\\")).ToList(); string pkg = StalePackage(cm.Groups[1].Value.Replace("\\\\", "\\"), args); if (pkg == null) continue;
                string lit = "\"" + pkg + "\""; if (!am.Value.Contains(lit)) continue;
                string newBody = body.Replace(am.Value, am.Value.Replace(lit, "\"" + pkg + "@latest\"")); result = result.Replace(body, newBody); done.Add(sec.Groups[1].Value.Trim('"'));
            }
            if (done.Count > 0 && result != text) { BackupOnce(ct); string tmp = ct + ".laica-tmp"; File.WriteAllText(tmp, result, new UTF8Encoding(false)); File.Delete(ct); File.Move(tmp, ct); }
            return done;
        }

        /// <summary>Catalogue and "add MCP" commands: unversioned npx/uvx packages get @latest.</summary>
        internal static string LatestCommand(string command)
        {
            var parts = Tokenize(command); if (parts.Length == 0) return command;
            string pkg = StalePackage(parts[0], parts.Skip(1).ToList()); if (pkg == null) return command;
            int idx = Array.IndexOf(parts, pkg, 1); if (idx < 0) return command; parts[idx] = pkg + "@latest";
            return String.Join(" ", parts.Select(WinQuote));
        }
    }
}
