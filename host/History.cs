using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Laica
{
    /// <summary>Lists the conversations Codex and Claude Code already have on this computer and opens them as LAICA chats that can be continued with each tool's own resume feature.</summary>
    public sealed partial class HarnessManager
    {
        sealed class HistEntry { public string Source, ExternalId, Title, Project, Path, ProjectName = "", ProjectId = ""; public DateTime Updated; }
        public sealed class CodexProject { public string Id, Name; public List<string> Roots = new List<string>(); }
        sealed class CodexMeta { public Dictionary<string, string> Names = new Dictionary<string, string>(); public List<CodexProject> Projects = new List<CodexProject>(); public Dictionary<string, string> Assign = new Dictionary<string, string>(); }
        readonly object histGate = new object();
        List<HistEntry> histCache; DateTime histAt = DateTime.MinValue;
        const int MaxHistory = 400, MaxTranscriptEvents = 3000;

        static string CleanPath(string p) { if (String.IsNullOrEmpty(p)) return ""; if (p.StartsWith("\\\\?\\")) p = p.Substring(4); return p.TrimEnd('\\', '/'); }
        static IEnumerable<string> SharedLines(string path, int maxLines, long maxChars)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true, 65536))
            {
                string l; int n = 0; long chars = 0;
                while ((l = sr.ReadLine()) != null) { n++; chars += l.Length; if (n > maxLines || chars > maxChars) yield break; yield return l; }
            }
        }
        static bool Boilerplate(string t) { t = t.TrimStart(); return t == "" || t.StartsWith("<") || t.StartsWith("# AGENTS") || t.StartsWith("Caveat:") || t.StartsWith("[Request interrupted"); }
        static string TextOf(object content)
        {
            if (content is string) return (string)content;
            var sb = new StringBuilder(); var list = content as System.Collections.IEnumerable; if (list == null) return "";
            foreach (object o in list) { var d = o as Dictionary<string, object>; if (d == null) continue; string ty = Str(d, "type"); if (ty == "text" || ty == "input_text" || ty == "output_text") { string t = Str(d, "text"); if (t != "") { if (sb.Length > 0) sb.Append("\n"); sb.Append(t); } } }
            return sb.ToString();
        }
        static string UserTextOf(object content)
        {
            if (content is string) return Boilerplate((string)content) ? "" : (string)content;
            var parts = new List<string>(); var list = content as System.Collections.IEnumerable; if (list == null) return "";
            foreach (object o in list) { var d = o as Dictionary<string, object>; if (d == null) continue; string ty = Str(d, "type"); if (ty == "text" || ty == "input_text") { string t = Str(d, "text"); if (!Boilerplate(t)) parts.Add(t); } }
            return String.Join("\n", parts);
        }
        static string ShortTitle(string t) { t = Regex.Replace((t ?? "").Trim(), @"\s+", " "); return t.Length > 60 ? t.Substring(0, 60).TrimEnd() + "…" : t; }

        HistEntry ReadClaudeHead(string file)
        {
            string id = System.IO.Path.GetFileNameWithoutExtension(file), cwd = "", title = "";
            foreach (string line in SharedLines(file, 400, 3000000))
            {
                if (line.Length > 1000000) continue; if (cwd != "" && title != "") break;
                if (!line.Contains("\"cwd\"") && !line.Contains("\"type\":\"user\"")) continue;
                Dictionary<string, object> o; try { o = json.Deserialize<Dictionary<string, object>>(line); } catch (Exception) { continue; }
                if (cwd == "" && Str(o, "cwd") != "") cwd = CleanPath(Str(o, "cwd"));
                if (title == "" && Str(o, "type") == "user" && Str(o, "isSidechain") != "True") { var m = Obj(o, "message"); if (m != null) title = ShortTitle(UserTextOf(m.ContainsKey("content") ? m["content"] : null)); }
            }
            if (cwd == "") return null;
            return new HistEntry { Source = "claude", ExternalId = id, Title = title, Project = cwd, Path = file, Updated = File.GetLastWriteTimeUtc(file) };   // an empty title means "no real prompt": dropped below
        }
        HistEntry ReadCodexHead(string file)
        {
            string id = "", cwd = "", title = ""; bool sub = false;
            foreach (string line in SharedLines(file, 300, 4000000))
            {
                if (line.Length > 1000000) continue; if (id != "" && title != "") break;
                bool meta = id == "" && line.Contains("\"session_meta\""), user = title == "" && line.Contains("\"role\":\"user\"");
                if (!meta && !user) continue;
                Dictionary<string, object> o; try { o = json.Deserialize<Dictionary<string, object>>(line); } catch (Exception) { continue; }
                var p = Obj(o, "payload"); if (p == null) continue;
                if (Str(o, "type") == "session_meta") { id = Str(p, "id"); cwd = CleanPath(Str(p, "cwd")); sub = Str(p, "parent_thread_id") != ""; if (sub) break; }
                else if (Str(p, "type") == "message" && Str(p, "role") == "user") title = ShortTitle(UserTextOf(p.ContainsKey("content") ? p["content"] : null));
            }
            if (id == "" || cwd == "" || sub) return null;
            return new HistEntry { Source = "codex", ExternalId = id, Title = title == "" ? "Codex session" : title, Project = cwd, Path = file, Updated = File.GetLastWriteTimeUtc(file) };
        }

        CodexMeta LoadCodexMeta()
        {
            var meta = new CodexMeta(); string home = CodexHomeDir();
            try
            {
                string idx = System.IO.Path.Combine(home, "session_index.jsonl");
                if (File.Exists(idx)) foreach (string line in SharedLines(idx, 100000, 40000000)) { try { var d = json.Deserialize<Dictionary<string, object>>(line); string id = Str(d, "id"), name = Str(d, "thread_name"); if (id != "" && name != "") meta.Names[id] = name; } catch (Exception) { } }
            }
            catch (Exception) { }
            try
            {
                string gs = System.IO.Path.Combine(home, ".codex-global-state.json");
                if (File.Exists(gs))
                {
                    string text; using (var fs = new FileStream(gs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();
                    var root = json.Deserialize<Dictionary<string, object>>(text); var lp = Obj(root, "local-projects"); var byId = new Dictionary<string, CodexProject>();
                    if (lp != null) foreach (var kv in lp) { var p = kv.Value as Dictionary<string, object>; if (p == null) continue; var cp = new CodexProject { Id = Str(p, "id") != "" ? Str(p, "id") : kv.Key, Name = Str(p, "name") }; var roots = Arr(p, "rootPaths"); if (roots != null) foreach (object r in roots) cp.Roots.Add(CleanPath(Convert.ToString(r))); if (cp.Name != "") byId[cp.Id] = cp; }
                    var order = Arr(root, "project-order"); var placed = new HashSet<string>();
                    if (order != null) foreach (object o in order) { CodexProject cp; string id = Convert.ToString(o); if (byId.TryGetValue(id, out cp) && placed.Add(id)) meta.Projects.Add(cp); }
                    foreach (var cp in byId.Values) if (placed.Add(cp.Id)) meta.Projects.Add(cp);
                    var asg = Obj(root, "thread-project-assignments"); if (asg != null) foreach (var kv in asg) { var a = kv.Value as Dictionary<string, object>; if (a != null && Str(a, "projectId") != "") meta.Assign[kv.Key] = Str(a, "projectId"); }
                }
            }
            catch (Exception) { }
            return meta;
        }
        static bool Under(string path, string root) { if (String.IsNullOrEmpty(path) || String.IsNullOrEmpty(root)) return false; path = path.TrimEnd('\\', '/'); root = root.TrimEnd('\\', '/'); return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase); }
        static string Squash(string s) { return new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant(); }
        CodexProject MatchCodexProject(CodexMeta meta, string threadId, string cwd)
        {
            string pid; if (meta.Assign.TryGetValue(threadId, out pid)) { var assigned = meta.Projects.FirstOrDefault(p => p.Id == pid); if (assigned != null) return assigned; }
            foreach (var p in meta.Projects) foreach (string r in p.Roots) if (Under(cwd, r)) return p;
            var m = Regex.Match(cwd ?? "", @"[\\/]\.codex[\\/]worktrees[\\/][^\\/]+[\\/]([^\\/]+)", RegexOptions.IgnoreCase);
            if (m.Success) { string leaf = Squash(m.Groups[1].Value); foreach (var p in meta.Projects) if (Squash(p.Name) == leaf || p.Roots.Any(r => Squash(System.IO.Path.GetFileName(r)) == leaf)) return p; }
            return null;
        }
        /// <summary>The projects as Codex itself shows them (names and order), with the first root that still exists on disk.</summary>
        public object CodexProjects()
        {
            return LoadCodexMeta().Projects.Select(p => (object)new Dictionary<string, object> { { "Id", p.Id }, { "Name", p.Name }, { "Path", p.Roots.FirstOrDefault(Directory.Exists) ?? p.Roots.FirstOrDefault() ?? "" }, { "Roots", p.Roots.ToArray() } }).ToArray();
        }
        static string ClaudeTitle(string file)
        {
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long take = Math.Min(fs.Length, 393216); fs.Seek(-take, SeekOrigin.End); var buf = new byte[take]; int n = 0; while (n < take) { int r = fs.Read(buf, n, (int)take - n); if (r <= 0) break; n += r; }
                    string tail = Encoding.UTF8.GetString(buf, 0, n); string found = null; foreach (Match m in Regex.Matches(tail, "\"customTitle\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")) found = m.Groups[1].Value;
                    if (found != null) { try { return (string)new JavaScriptSerializer().Deserialize<string>("\"" + found + "\""); } catch (Exception) { return found; } }
                }
            }
            catch (Exception) { }
            return null;
        }

        List<HistEntry> LoadHistory(bool refresh)
        {
            lock (histGate)
            {
                if (!refresh && histCache != null && (DateTime.UtcNow - histAt).TotalSeconds < 90) return histCache;
                var files = new List<KeyValuePair<string, DateTime>>();
                try { string root = System.IO.Path.Combine(ClaudeDir(), "projects"); if (Directory.Exists(root)) foreach (string d in Directory.GetDirectories(root)) foreach (string f in Directory.GetFiles(d, "*.jsonl")) files.Add(new KeyValuePair<string, DateTime>(f, File.GetLastWriteTimeUtc(f))); } catch (Exception) { }
                try { string root = System.IO.Path.Combine(CodexHomeDir(), "sessions"); if (Directory.Exists(root)) foreach (string f in Directory.GetFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories)) files.Add(new KeyValuePair<string, DateTime>(f, File.GetLastWriteTimeUtc(f))); } catch (Exception) { }
                var list = new List<HistEntry>();
                foreach (var f in files.OrderByDescending(x => x.Value).Take(MaxHistory * 2))
                {
                    if (list.Count >= MaxHistory) break;
                    try { var e = f.Key.IndexOf("rollout-", StringComparison.OrdinalIgnoreCase) >= 0 && f.Key.IndexOf(System.IO.Path.DirectorySeparatorChar + "sessions" + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0 ? ReadCodexHead(f.Key) : ReadClaudeHead(f.Key); if (e != null) list.Add(e); } catch (Exception) { }
                }
                var meta = LoadCodexMeta(); var desktop = LoadClaudeDesktopMeta(json);
                foreach (var e in list)
                {
                    if (e.Source == "codex")
                    {
                        string name; if (meta.Names.TryGetValue(e.ExternalId, out name)) e.Title = ShortTitle(name);
                        var p = MatchCodexProject(meta, e.ExternalId, e.Project); if (p != null) { e.ProjectName = p.Name; e.ProjectId = p.Id; if (!Directory.Exists(e.Project)) { string root = p.Roots.FirstOrDefault(Directory.Exists); if (root != null) e.Project = root; } }
                    }
                    else { string title = ClaudeTitle(e.Path); ClaudeDesktopMeta dm; bool known = desktop.TryGetValue(e.ExternalId, out dm);
                        if (!String.IsNullOrWhiteSpace(title)) e.Title = ShortTitle(title); else if (known && dm.Title != "") e.Title = ShortTitle(dm.Title);
                        if (known && dm.Cwd != "" && Directory.Exists(dm.Cwd)) e.Project = dm.Cwd; if (known && dm.Active > e.Updated) e.Updated = dm.Active;
                        if (IsScratchFolder(e.Project)) { e.ProjectName = ""; continue; }
                        e.ProjectName = Regex.Replace(System.IO.Path.GetFileName(e.Project.TrimEnd('\\', '/')), @"^$", "Claude Code"); }
                }
                list.RemoveAll(e => e.Source == "claude" && String.IsNullOrWhiteSpace(e.Title));   // sessions with nothing in them (a command run on its own, a probe) are not chats
                histCache = list; histAt = DateTime.UtcNow; return list;
            }
        }

        sealed class ClaudeDesktopMeta { public string Cwd = "", Title = ""; public DateTime Active; public bool Archived; }

        /// <summary>The Claude desktop app keeps each chat's real name and project folder in its own metadata; the transcript only knows the scratch folder it started in.</summary>
        static Dictionary<string, ClaudeDesktopMeta> LoadClaudeDesktopMeta(JavaScriptSerializer ser)
        {
            var map = new Dictionary<string, ClaudeDesktopMeta>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string over = Environment.GetEnvironmentVariable("LAICA_CLAUDE_DESKTOP_DIR"); var roots = new List<string>(); if (!String.IsNullOrWhiteSpace(over)) roots.Add(over); else foreach (string cr in HarnessManager.ClaudeRoamingRoots()) roots.Add(System.IO.Path.Combine(cr, "claude-code-sessions"));
                foreach (string f in roots.Where(Directory.Exists).SelectMany(r => Directory.GetFiles(r, "local_*.json", SearchOption.AllDirectories)))
                {
                    try
                    {
                        if (new FileInfo(f).Length > 8 * 1024 * 1024) continue;
                        string text; using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();
                        var d = ser.Deserialize<Dictionary<string, object>>(text); string id = Str(d, "cliSessionId"); if (id == "") continue;
                        var m = new ClaudeDesktopMeta { Cwd = CleanPath(Str(d, "cwd") != "" ? Str(d, "cwd") : Str(d, "originCwd")), Title = Str(d, "title").Trim(), Archived = Str(d, "isArchived") == "True" };
                        long ms; if (Int64.TryParse(Str(d, "lastActivityAt"), out ms) && ms > 0) m.Active = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
                        map[id] = m;
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return map;
        }
        static bool IsScratchFolder(string path) { return !String.IsNullOrEmpty(path) && (path.IndexOf("\\scratch-workspaces\\", StringComparison.OrdinalIgnoreCase) >= 0 || Regex.IsMatch(System.IO.Path.GetFileName(path.TrimEnd('\\', '/')), @"^scratch-\d{4}-\d{2}-\d{2}")); }

        public object ImportList(bool refresh)
        {
            var list = LoadHistory(refresh); var open = new Dictionary<string, string>();
            lock (gate) foreach (var s in sessions.Values) if (!String.IsNullOrEmpty(s.ExternalId)) open[s.Harness + "|" + s.ExternalId] = (s.Parent ?? s).Id;
            return list.Select(h => (object)new Dictionary<string, object> { { "Source", h.Source }, { "ExternalId", h.ExternalId }, { "Title", h.Title }, { "Project", h.Project }, { "ProjectName", h.ProjectName }, { "ProjectId", h.ProjectId }, { "UpdatedUtc", h.Updated.ToString("o") }, { "SessionId", open.ContainsKey(h.Source + "|" + h.ExternalId) ? open[h.Source + "|" + h.ExternalId] : "" } }).ToArray();
        }

        public object ImportOpen(string source, string externalId, string fallbackCwd)
        {
            if (source != "codex" && source != "claude") throw new ArgumentException("Unknown source.");
            Session exist; lock (gate) exist = sessions.Values.FirstOrDefault(x => x.Harness == source && x.ExternalId == externalId);
            if (exist != null) { try { UpgradeImport(exist); } catch (Exception) { } return Dto(exist.Parent ?? exist); }
            var entry = LoadHistory(false).FirstOrDefault(h => h.Source == source && h.ExternalId == externalId) ?? LoadHistory(true).FirstOrDefault(h => h.Source == source && h.ExternalId == externalId);
            if (entry == null) throw new ArgumentException("That conversation can't be found any more.");
            var info = Harnesses().FirstOrDefault(h => h.Id == source);
            string cwd = Directory.Exists(entry.Project) ? entry.Project : (!String.IsNullOrWhiteSpace(fallbackCwd) && Directory.Exists(fallbackCwd) ? fallbackCwd : Environment.CurrentDirectory);
            var s = new Session { Id = Guid.NewGuid().ToString("N"), Harness = source, Cwd = System.IO.Path.GetFullPath(cwd), Mode = info != null ? info.DefaultMode : "default", ExternalId = externalId, Title = entry.Title, HasSentRules = true };
            var events = source == "claude" ? TranscriptClaude(s, entry.Path) : TranscriptCodex(s, entry.Path);
            string stamp = DateTime.UtcNow.ToString("o");
            var head = new Dictionary<string, object> { { "Kind", "log" }, { "Text", "Imported from " + (source == "claude" ? "Claude Code" : "Codex") + " history" + (Directory.Exists(entry.Project) ? "" : " — the original folder no longer exists, so new messages use " + s.Cwd) + ". Send a message to continue this conversation." }, { "Detail", "v2" }, { "TimeUtc", stamp } };
            lock (gate) { s.Events.Add(head); s.Events.AddRange(events); foreach (var e in s.Events) { e["SessionId"] = s.Id; e["N"] = ++s.Seq; } sessions[s.Id] = s; }
            SaveSession(s); Raise(); return Dto(s);
        }

        /// <summary>Chats imported by an older LAICA only had text. Re-read the vendor's transcript for them (pictures, thinking, commands, turn ends) and keep anything done in LAICA since.</summary>
        void UpgradeImport(Session s)
        {
            Dictionary<string, object> head; DateTime stamp;
            lock (gate)
            {
                if (s.Busy || s.Events.Count == 0) return; head = s.Events[0];
                if (Str(head, "Kind") != "log" || !Str(head, "Text").StartsWith("Imported from") || Str(head, "Detail") == "v2") return;
                if (!DateTime.TryParse(Str(head, "TimeUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out stamp)) return;
            }
            var entry = LoadHistory(false).FirstOrDefault(h => h.Source == s.Harness && h.ExternalId == s.ExternalId); if (entry == null) return;
            var fresh = s.Harness == "claude" ? TranscriptClaude(s, entry.Path) : TranscriptCodex(s, entry.Path);
            lock (gate)
            {
                var mine = s.Events.Skip(1).Where(e => { DateTime t; return DateTime.TryParse(Str(e, "TimeUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out t) && t > stamp.AddSeconds(2); }).ToList();
                head["Detail"] = "v2"; var all = new List<Dictionary<string, object>> { head }; all.AddRange(fresh); all.AddRange(mine);
                long n = 0; foreach (var e in all) { e["SessionId"] = s.Id; e["N"] = ++n; } s.Events.Clear(); s.Events.AddRange(all); s.Seq = n;
            }
            SaveSession(s); Raise();
        }
        static Dictionary<string, object> Ev(string kind, string text, string detail, string time) { return new Dictionary<string, object> { { "SessionId", "" }, { "Kind", kind }, { "Text", text }, { "Detail", detail }, { "TimeUtc", String.IsNullOrEmpty(time) ? DateTime.UtcNow.ToString("o") : time } }; }
        static void Add(List<Dictionary<string, object>> list, Dictionary<string, object> e) { list.Add(e); if (list.Count > MaxTranscriptEvents + 300) list.RemoveRange(0, list.Count - MaxTranscriptEvents); }
        static string Cap(string s, int n) { return s != null && s.Length > n ? s.Substring(0, n) + "\n… (truncated)" : (s ?? ""); }

        static void EndTurn(List<Dictionary<string, object>> list, ref bool open, string time) { if (open) { Add(list, Ev("done", "", null, time)); open = false; } }
        static Dictionary<string, object> WithImages(Dictionary<string, object> e, List<string> images) { if (images != null && images.Count > 0) e["Images"] = images.ToArray(); return e; }

        /// <summary>A Claude Code transcript as chat events: your messages, replies, thinking, tool calls and results, pictures (yours and ones a tool returned) and turn ends.</summary>
        List<Dictionary<string, object>> TranscriptClaude(Session s, string file)
        {
            var list = new List<Dictionary<string, object>>(); bool open = false;
            foreach (string line in SharedLines(file, Int32.MaxValue, Int64.MaxValue))
            {
                if (line.Length > 12000000 || (!line.Contains("\"type\":\"user\"") && !line.Contains("\"type\":\"assistant\""))) continue;
                Dictionary<string, object> o; try { o = json.Deserialize<Dictionary<string, object>>(line); } catch (Exception) { continue; }
                if (Str(o, "isSidechain") == "True" || Str(o, "isMeta") == "True") continue;
                var m = Obj(o, "message"); if (m == null) continue; string time = Str(o, "timestamp"), type = Str(o, "type"); object content = m.ContainsKey("content") ? m["content"] : null;
                if (type == "user")
                {
                    string text = UserTextOf(content); if (text.Trim() != "") { EndTurn(list, ref open, time); Add(list, Ev("user", Cap(text, 6000), null, time)); open = true; }
                    var blocks = content as System.Collections.IEnumerable; if (blocks != null && !(content is string)) foreach (object b in blocks)
                        {
                            var d = b as Dictionary<string, object>; if (d == null) continue; string ty = Str(d, "type");
                            if (ty == "tool_result") { object inner = d.ContainsKey("content") ? d["content"] : null; Add(list, WithImages(Ev("tool_result", Cap(Flatten(inner), 4000), Str(d, "is_error") == "True" ? "error" : null, time), SaveImages(s, inner))); }
                            else if (ty == "image") { var im = SaveImages(s, new object[] { d }); if (im.Count > 0) Add(list, WithImages(Ev("image", "", "attached", time), im)); }
                        }
                }
                else
                {
                    var blocks = content as System.Collections.IEnumerable; if (blocks == null || content is string) { if (content is string) { Add(list, Ev("assistant", Cap((string)content, 8000), null, time)); open = true; } continue; }
                    foreach (object b in blocks)
                    {
                        var d = b as Dictionary<string, object>; if (d == null) continue; string ty = Str(d, "type"); open = true;
                        if (ty == "text" && Str(d, "text").Trim() != "") Add(list, Ev("assistant", Cap(Str(d, "text"), 8000), null, time));
                        else if (ty == "thinking" && Str(d, "thinking").Trim() != "") Add(list, Ev("thinking", Cap(Str(d, "thinking"), 4000), null, time));
                        else if (ty == "tool_use") Add(list, Ev("tool", Str(d, "name"), Cap(json.Serialize(d.ContainsKey("input") ? d["input"] : null), 2000), time));
                        else if (ty == "image") { var im = SaveImages(s, new object[] { d }); if (im.Count > 0) Add(list, WithImages(Ev("image", "", null, time), im)); }
                    }
                }
            }
            EndTurn(list, ref open, DateTime.UtcNow.ToString("o"));
            return list.Count > MaxTranscriptEvents ? list.Skip(list.Count - MaxTranscriptEvents).ToList() : list;
        }

        static string CodexCommand(JavaScriptSerializer js, string arguments)
        {
            try
            {
                var d = js.Deserialize<Dictionary<string, object>>(arguments); object c = d.ContainsKey("command") ? d["command"] : d.ContainsKey("cmd") ? d["cmd"] : null;
                var arr = c as System.Collections.IEnumerable; if (arr != null && !(c is string)) return String.Join(" ", arr.Cast<object>().Select(x => Convert.ToString(x)));
                return Convert.ToString(c) ?? "";
            }
            catch (Exception) { return ""; }
        }

        /// <summary>A Codex rollout as chat events: messages, reasoning summaries, commands and their output, file edits, searches, pictures (yours, viewed or generated) and turn ends.</summary>
        List<Dictionary<string, object>> TranscriptCodex(Session s, string file)
        {
            var list = new List<Dictionary<string, object>>(); bool open = false;
            foreach (string line in SharedLines(file, Int32.MaxValue, Int64.MaxValue))
            {
                if (line.Length > 12000000 || !line.Contains("\"response_item\"")) continue;
                Dictionary<string, object> o; try { o = json.Deserialize<Dictionary<string, object>>(line); } catch (Exception) { continue; }
                var p = Obj(o, "payload"); if (p == null) continue; string time = Str(o, "timestamp"), ty = Str(p, "type");
                if (ty == "message")
                {
                    string role = Str(p, "role"); object content = p.ContainsKey("content") ? p["content"] : null;
                    if (role == "user")
                    {
                        string t = UserTextOf(content); if (t.Trim() != "") { EndTurn(list, ref open, time); Add(list, Ev("user", Cap(t, 6000), null, time)); open = true; }
                        var im = SaveImages(s, content); if (im.Count > 0) Add(list, WithImages(Ev("image", "", "attached", time), im));
                    }
                    else if (role == "assistant") { string t = TextOf(content); if (t.Trim() != "") { Add(list, Ev("assistant", Cap(t, 8000), null, time)); open = true; } }
                }
                else if (ty == "reasoning") { string r = Flatten(p.ContainsKey("summary") ? p["summary"] : null); if (r.Trim() != "") { Add(list, Ev("thinking", Cap(r, 4000), null, time)); open = true; } }
                else if (ty == "function_call" || ty == "custom_tool_call")
                {
                    string name = Str(p, "name") == "" ? "tool" : Str(p, "name"), args = ty == "function_call" ? Str(p, "arguments") : Str(p, "input");
                    if (name == "shell" || name == "shell_command" || name == "exec_command" || name == "container.exec") { string c = CodexCommand(json, args); if (c != "") { Add(list, Ev("tool", "shell", Cap(c, 2000), time)); open = true; continue; } }
                    Add(list, Ev("tool", name == "apply_patch" ? "file change" : name, Cap(args, 2000), time)); open = true;
                }
                else if (ty == "local_shell_call") { var act = Obj(p, "action"); string c = act != null && act.ContainsKey("command") ? Flatten(act["command"]) : ""; Add(list, Ev("tool", "shell", Cap(c.Replace("\n", " "), 2000), time)); open = true; }
                else if (ty == "web_search_call") { var act = Obj(p, "action"); Add(list, Ev("tool", "web search", Str(act, "query"), time)); open = true; }
                else if (ty == "function_call_output" || ty == "custom_tool_call_output")
                {
                    object output = p.ContainsKey("output") ? p["output"] : null; string text = output is string ? (string)output : Flatten(output) != "" ? Flatten(output) : json.Serialize(output);
                    try { if (output is string && ((string)output).StartsWith("{")) { var od = json.Deserialize<Dictionary<string, object>>((string)output); if (od.ContainsKey("output")) text = Convert.ToString(od["output"]); } } catch (Exception) { }
                    Add(list, WithImages(Ev("tool_result", Cap(text, 4000), null, time), SaveImages(s, output))); open = true;
                }
                else if (ty.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0) { var im = ImagesFromItem(s, p); if (im.Count > 0) { Add(list, WithImages(Ev("image", ty, Str(p, "path"), time), im)); open = true; } }
            }
            EndTurn(list, ref open, DateTime.UtcNow.ToString("o"));
            return list.Count > MaxTranscriptEvents ? list.Skip(list.Count - MaxTranscriptEvents).ToList() : list;
        }    }
}
