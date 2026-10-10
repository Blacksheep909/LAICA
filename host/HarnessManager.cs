using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Laica
{
    /// <summary>Runs external AI coding harnesses (Codex, Claude Code, Gemini, custom CLIs) as child processes, persists chats, schedules tasks and serves workspace files.</summary>
    public sealed partial class HarnessManager : IDisposable
    {
        public sealed class HarnessInfo { public string Id, Name, Path, Parser; public bool Available, Custom; public string[] Modes; public string DefaultMode; public string Args; }
        sealed class Session
        {
            public string Id, Harness, Cwd, Mode, ExternalId, Title = "New chat", Rules, AssistantId; public Process Proc; public bool Busy, HasSentRules, Paused, HandoffDone; public int HandoffDepth; public readonly System.Threading.ManualResetEventSlim Go = new System.Threading.ManualResetEventSlim(true);
            public string ServiceId, Model, ImageModel, Effort, TeamId; public List<Dictionary<string, object>> Messages = new List<Dictionary<string, object>>(); public System.Threading.CancellationTokenSource Cts;
            public bool Notify, Interactive, Ended; public string TurnPrompt; public readonly object InLock = new object(); public readonly Dictionary<string, Dictionary<string, object>> Pending = new Dictionary<string, Dictionary<string, object>>(); public readonly HashSet<string> AlwaysAllow = new HashSet<string>();
            public PairState Pair = new PairState(); public Session Parent, Child; public string ParentId; public long Seq; public bool Probe;
            public long TIn, TOut, TCr, TCw; public bool Isolated; public string RepoRoot, Branch, BaseBranch, BaseCommit, WorktreePath, SnapHead; public Dictionary<string, string> Snapshot;
            public readonly List<Dictionary<string, object>> Events = new List<Dictionary<string, object>>();
        }
        readonly object gate = new object();
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 60 };
        readonly string dir;
        List<Dictionary<string, object>> tasks = new List<Dictionary<string, object>>();
        List<Dictionary<string, object>> agents = new List<Dictionary<string, object>>();
        Timer scheduler;
        public event Action<object> Event;
        public event Action Changed;
        /// <summary>Raised when a scheduled task or a team finishes: (title, summary).</summary>
        public event Action<string, string> Completed;

        readonly Func<ServiceConnection[]> services;
        public HarnessManager(string dataDir) : this(dataDir, null) { }
        public HarnessManager(string dataDir, Func<ServiceConnection[]> serviceList)
        {
            services = serviceList ?? (() => new ServiceConnection[0]); coEnv = CoAuthorEnv;
            dir = Path.Combine(dataDir ?? Path.GetTempPath(), "harness");
            try { Directory.CreateDirectory(Path.Combine(dir, "sessions")); } catch (Exception) { }
            tasks = LoadList("tasks.json"); agents = LoadList("agents.json"); teams = LoadList("teams.json");
            LoadSessions(); LoadUsage(); LoadHandoff(); LinkPairs(); PurgeTrash();
            scheduler = new Timer(_ => Tick(), null, 15000, 15000);
        }

        // ---------- discovery ----------
        public static string FindClaude()
        {
            foreach (string claudeRoot in ClaudeRoamingRoots())
            {
                string root = Path.Combine(claudeRoot, "claude-code");
                try { if (Directory.Exists(root)) { string f = Directory.GetDirectories(root).SelectMany(d => Directory.GetDirectories(d).Concat(new[] { d })).SelectMany(d => Directory.GetFiles(d, "claude.exe")).OrderByDescending(p => File.GetLastWriteTimeUtc(p)).FirstOrDefault(); if (f != null) return f; } } catch (Exception) { }
            }
            return FindOnPath("claude.exe") ?? FindOnPath("claude.cmd");
        }
        /// <summary>
        /// Where the Claude desktop app keeps its data. The Store (MSIX) build of Claude redirects %APPDATA%\Claude for itself, so programs outside it
        /// (including LAICA) only see the real files under %LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude.
        /// </summary>
        public static string[] ClaudeRoamingRoots()
        {
            var list = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude") };
            try
            {
                string pk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                if (Directory.Exists(pk)) foreach (string d in Directory.GetDirectories(pk, "Claude_*")) list.Add(Path.Combine(d, "LocalCache", "Roaming", "Claude"));
            }
            catch (Exception) { }
            return list.ToArray();
        }
        static string FindOnPath(string file)
        {
            foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) { try { string p = Path.Combine(d.Trim(), file); if (File.Exists(p)) return p; } catch (Exception) { } }
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (string d in new[] { Path.Combine(roam, "npm"), Path.Combine(home, ".bun", "bin"), Path.Combine(home, ".local", "bin"), Path.Combine(home, "scoop", "shims"), Path.Combine(local, "Volta", "bin"), Path.Combine(local, "pnpm"), Path.Combine(local, "Programs", "gemini-cli") }) { try { string p = Path.Combine(d, file); if (File.Exists(p)) return p; } catch (Exception) { } }
            return null;
        }
        static string FindAny(params string[] names) { foreach (string n in names) { string p = FindOnPath(n); if (p != null) return p; } return null; }

        public HarnessInfo[] Harnesses()
        {
            string codex = ModelCatalog.FindCodex(), claude = FindClaude();
            var list = new List<HarnessInfo> {
                new HarnessInfo { Id = "codex", Name = "Codex", Parser = "codex", Path = codex, Available = codex != null, Modes = new[] { "ask-first", "approve-for-me", "read-only", "workspace-write", "danger-full-access" }, DefaultMode = "workspace-write" },
                new HarnessInfo { Id = "claude", Name = "Claude Code", Parser = "claude", Path = claude, Available = claude != null, Modes = new[] { "plan", "default", "auto", "acceptEdits", "bypassPermissions" }, DefaultMode = "acceptEdits" }
            };
            var text = new[] { new[] { "gemini", "Gemini CLI", "gemini.cmd", "gemini.exe" }, new[] { "qwen", "Qwen Code", "qwen.cmd", "qwen.exe" }, new[] { "opencode", "OpenCode", "opencode.cmd", "opencode.exe" }, new[] { "goose", "Goose", "goose.exe" }, new[] { "kimi", "Kimi CLI", "kimi.exe", "kimi.cmd" }, new[] { "aider", "Aider", "aider.exe" }, new[] { "copilot", "GitHub Copilot CLI", "copilot.cmd", "copilot.exe" } };
            foreach (var t in text) { string p = FindAny(t.Skip(2).ToArray()); list.Add(new HarnessInfo { Id = t[0], Name = t[1], Parser = "text", Path = p, Available = p != null, Modes = new[] { "default", "yolo" }, DefaultMode = "default" }); }
            list.Insert(0, new HarnessInfo { Id = "workflow", Name = "Workflow team", Parser = "builtin", Path = "(built in)", Available = WorkflowRunner != null, Modes = new[] { "read-only" }, DefaultMode = "read-only" });
            list.Insert(0, new HarnessInfo { Id = "laica", Name = "LAICA Agent", Parser = "builtin", Path = "(built in)", Available = services().Length > 0, Modes = new[] { "ask-first", "accept-edits", "yolo" }, DefaultMode = "ask-first" });
            lock (gate) foreach (var a in agents) list.Add(new HarnessInfo { Id = Str(a, "Id"), Name = Str(a, "Name"), Parser = Str(a, "Parser") == "" ? "text" : Str(a, "Parser"), Path = Str(a, "Command"), Args = Str(a, "Args"), Custom = true, Available = File.Exists(Str(a, "Command")) || FindOnPath(Str(a, "Command")) != null, Modes = new[] { "default" }, DefaultMode = "default" });
            return list.ToArray();
        }
        static string ResolveExe(string p) { return File.Exists(p) ? p : (FindOnPath(p) ?? p); }

        // ---------- sessions ----------
        public object List()
        {
            lock (gate) return sessions.Values.Where(x => x.Parent == null).Select(Dto).ToArray();
        }
        static string LastEventTime(Session s) { try { for (int i = s.Events.Count - 1; i >= 0; i--) { string t = Str(s.Events[i], "TimeUtc"); if (t != "") return t; } } catch (Exception) { } return ""; }
        static string LastUserTime(Session s) { try { for (int i = s.Events.Count - 1; i >= 0; i--) if (Str(s.Events[i], "Kind") == "user") return Str(s.Events[i], "TimeUtc"); } catch (Exception) { } return ""; }
        static Dictionary<string, object> Dto(Session s) { return new Dictionary<string, object> { { "Id", s.Id }, { "Harness", s.Harness }, { "Title", s.Title }, { "Cwd", s.Cwd }, { "Mode", s.Mode }, { "Busy", IsBusy(s) }, { "BusySince", IsBusy(s) ? LastUserTime(s) : "" }, { "UpdatedUtc", LastEventTime(s) }, { "Paused", s.Paused || (s.Child != null && s.Child.Paused) }, { "Continuity", s.Pair.Mode }, { "TagTeam", s.Pair.Active == "partner" ? "partner" : (s.Pair.Mode == "automatic" || s.Child != null ? "primary" : "") }, { "Waiting", s.Pair.WaitUntil != DateTime.MinValue }, { "PartnerHarness", PartnerShown(s) }, { "AssistantId", s.AssistantId }, { "Isolated", s.Isolated }, { "Branch", s.Branch }, { "ServiceId", s.ServiceId }, { "Model", s.Model }, { "Effort", s.Effort }, { "TeamId", s.TeamId }, { "Project", s.Isolated && !String.IsNullOrEmpty(s.RepoRoot) ? s.RepoRoot : s.Cwd } }; }
        public object Create(string harness, string cwd, string mode, string rules, string assistantId, string title) { return Create(harness, cwd, mode, rules, assistantId, title, false); }
        public object Create(string harness, string cwd, string mode, string rules, string assistantId, string title, bool isolate) { return Create(harness, cwd, mode, rules, assistantId, title, isolate, null, null); }
        public object Create(string harness, string cwd, string mode, string rules, string assistantId, string title, bool isolate, string serviceId, string model) { return Create(harness, cwd, mode, rules, assistantId, title, isolate, serviceId, model, null); }
        public object Create(string harness, string cwd, string mode, string rules, string assistantId, string title, bool isolate, string serviceId, string model, string effort)
        {
            var info = Harnesses().FirstOrDefault(h => h.Id == harness);
            if (info == null) throw new ArgumentException("Unknown harness.");
            if (String.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd)) throw new ArgumentException("Choose an existing working folder.");
            var s = new Session { Id = Guid.NewGuid().ToString("N"), Harness = harness, Cwd = Path.GetFullPath(cwd), Mode = String.IsNullOrEmpty(mode) ? info.DefaultMode : mode, Title = String.IsNullOrEmpty(title) ? info.Name + " chat" : title, Rules = rules, AssistantId = assistantId, ServiceId = serviceId, Model = model, Effort = ValidEffort(effort) };
            if (harness == "workflow" && (String.IsNullOrWhiteSpace(serviceId) || WorkflowRunner == null)) throw new ArgumentException("Choose a workflow team.");
            if (harness == "laica") { if (String.IsNullOrWhiteSpace(serviceId) || services().All(x => x.Id != serviceId)) throw new ArgumentException("Choose a service for the LAICA Agent."); if (String.IsNullOrWhiteSpace(model)) throw new ArgumentException("Choose a model for the LAICA Agent."); }
            if (isolate) CreateWorktree(s);
            lock (gate) sessions[s.Id] = s;
            SaveSession(s); Raise(); return Dto(s);
        }

        // ---------- git worktrees ----------
        public object GitInfo(string cwd)
        {
            string root = GitTools.Root(cwd);
            bool exists = !String.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd);
            if (root == null) return new Dictionary<string, object> { { "IsRepo", false }, { "Exists", exists }, { "GitAvailable", GitTools.Available } };
            int dirty = 0; try { dirty = GitTools.Status(root).Count; } catch (Exception) { }
            return new Dictionary<string, object> { { "IsRepo", true }, { "Exists", true }, { "GitAvailable", true }, { "Root", root }, { "Branch", GitTools.Branch(root) }, { "Dirty", dirty } };
        }
        static string Slug(string v) { var sb = new StringBuilder(); foreach (char c in (v ?? "").ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : '-'); string r = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-+", "-").Trim('-'); return r == "" ? "chat" : (r.Length > 24 ? r.Substring(0, 24).Trim('-') : r); }
        void CreateWorktree(Session s)
        {
            string root = GitTools.Root(s.Cwd);
            if (root == null) throw new InvalidOperationException(GitTools.Available ? "That folder isn't a git repository, so it can't use a worktree." : "Git isn't installed, so worktrees aren't available.");
            string rel = s.Cwd.Length > root.Length ? s.Cwd.Substring(root.Length).TrimStart('\\', '/') : "";
            string head = GitTools.Head(root); if (head == "") throw new InvalidOperationException("This repository has no commits yet. Make a first commit, then try again.");
            string baseBranch = GitTools.Branch(root);
            string branch = "laica/" + Slug(s.Title) + "-" + s.Id.Substring(0, 6);
            string path = Path.Combine(dir, "worktrees", Slug(Path.GetFileName(root)), s.Id.Substring(0, 8));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            GitTools.Run(root, "worktree add -b " + branch + " \"" + path + "\" HEAD");
            s.Isolated = true; s.RepoRoot = root; s.Branch = branch; s.BaseBranch = baseBranch; s.BaseCommit = head; s.WorktreePath = path;
            s.Cwd = rel != "" && Directory.Exists(Path.Combine(path, rel)) ? Path.Combine(path, rel) : path;
        }
        string WorkRoot(Session s) { return s.Isolated ? s.WorktreePath : (GitTools.Root(s.Cwd) ?? s.Cwd); }
        public object Changes(string id)
        {
            Session s; lock (gate) s = Get(id);
            string wr = WorkRoot(s); string root = GitTools.Root(wr);
            if (root == null) return new Dictionary<string, object> { { "IsRepo", false }, { "Files", new object[0] } };
            var files = GitTools.Status(root); int ahead = 0, baseMoved = 0;
            if (s.Isolated)
            {
                try { ahead = Convert.ToInt32(GitTools.Run(root, "rev-list --count " + s.BaseCommit + "..HEAD", false).Trim()); } catch (Exception) { }
                try { baseMoved = Convert.ToInt32(GitTools.Run(s.RepoRoot, "rev-list --count " + s.BaseCommit + ".." + s.BaseBranch, false).Trim()); } catch (Exception) { }
            }
            return new Dictionary<string, object> { { "IsRepo", true }, { "Isolated", s.Isolated }, { "Branch", s.Isolated ? s.Branch : GitTools.Branch(root) }, { "BaseBranch", s.BaseBranch }, { "Files", files.ToArray() }, { "Ahead", ahead }, { "BaseMoved", baseMoved } };
        }
        public object Diff(string id, string path) { Session s; lock (gate) s = Get(id); string root = GitTools.Root(WorkRoot(s)); if (root == null) throw new InvalidOperationException("This folder isn't a git repository."); Inside(root, path); return GitTools.Diff(root, path); }
        public object Commit(string id, string message)
        {
            Session s; lock (gate) s = Get(id); if (s.Busy) throw new InvalidOperationException("Wait for the agent to finish first.");
            string root = GitTools.Root(WorkRoot(s)); if (root == null) throw new InvalidOperationException("This folder isn't a git repository.");
            if (String.IsNullOrWhiteSpace(message)) throw new ArgumentException("Write a commit message.");
            if (GitTools.Status(root).Count == 0) throw new InvalidOperationException("There is nothing to commit.");
            string ident = GitTools.Run(root, "config user.email", false).Trim() == "" ? "-c user.name=LAICA -c user.email=laica@localhost " : "";
            string trailer = CoAuthorTrailer(WorkRoot(s)); GitTools.Run(root, "add -A"); GitTools.Run(root, ident + "commit -q -m \"" + message.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ") + "\"" + (trailer != "" ? " -m \"" + trailer + "\"" : ""));
            Raise(); return Changes(id);
        }
        /// <summary>Merges the chat's branch into the base branch of the original checkout. Refuses anything that could damage uncommitted work.</summary>
        public object Merge(string id)
        {
            Session s; lock (gate) s = Get(id); if (!s.Isolated) throw new InvalidOperationException("Only worktree chats can be applied to the main checkout.");
            if (s.Busy) throw new InvalidOperationException("Wait for the agent to finish first.");
            if (GitTools.Status(s.WorktreePath).Count > 0) throw new InvalidOperationException("Commit the changes in this chat first.");
            if (GitTools.Branch(s.RepoRoot) != s.BaseBranch) throw new InvalidOperationException("The main checkout is on '" + GitTools.Branch(s.RepoRoot) + "', not '" + s.BaseBranch + "'. Switch it back first.");
            if (GitTools.Status(s.RepoRoot).Count > 0) throw new InvalidOperationException("The main checkout has uncommitted changes. Commit or stash them first so nothing is lost.");
            try { GitTools.Run(s.RepoRoot, "-c user.name=LAICA -c user.email=laica@localhost merge --no-ff -m \"Merge " + s.Branch + "\" " + s.Branch); }
            catch (Exception ex) { try { GitTools.Run(s.RepoRoot, "merge --abort", false); } catch (Exception) { } throw new InvalidOperationException("The merge has conflicts and was cancelled; nothing was changed. " + ex.Message); }
            Raise(); return Changes(id);
        }
        public void RemoveWorktree(string id, bool deleteBranch)
        {
            Session s; lock (gate) s = Get(id); if (!s.Isolated) return;
            if (s.Busy) throw new InvalidOperationException("Stop the agent first.");
            if (deleteBranch) { string merged = GitTools.Run(s.RepoRoot, "branch --merged " + s.BaseBranch, false); if (!merged.Contains(s.Branch) && GitTools.Run(s.RepoRoot, "rev-list --count " + s.BaseBranch + ".." + s.Branch, false).Trim() != "0") throw new InvalidOperationException("This branch has commits that were never applied to '" + s.BaseBranch + "'. Apply them first, or remove the worktree and keep the branch."); }
            string path = s.WorktreePath, repo = s.RepoRoot, branch = s.Branch, cwdRel = "";
            GitTools.Run(repo, "worktree remove --force \"" + path + "\"");
            if (deleteBranch) GitTools.Run(repo, "branch -D " + branch, false);
            lock (gate) { s.Cwd = repo + (cwdRel == "" ? "" : "\\" + cwdRel); s.Isolated = false; s.WorktreePath = null; s.Branch = null; s.Snapshot = null; }
            SaveSession(s); Raise();
        }
        public object History(string id) { lock (gate) { return Get(id).Events.ToArray(); } }
        Session Get(string id) { Session s; if (id == null || !sessions.TryGetValue(id, out s)) throw new ArgumentException("That chat no longer exists."); return s; }
        public void Rename(string id, string title) { lock (gate) { Get(id).Title = String.IsNullOrWhiteSpace(title) ? "Chat" : title.Trim(); } SaveSession(sessions[id]); Raise(); }
        public void Close(string id)
        {
            string kidId = null; lock (gate) { Session cs; if (sessions.TryGetValue(id, out cs) && cs.Child != null) kidId = cs.Child.Id; }
            try { Stop(id); } catch (Exception) { }
            if (kidId != null) { lock (gate) sessions.Remove(kidId); try { File.Delete(Path.Combine(dir, "sessions", Safe(kidId) + ".json")); File.Delete(Path.Combine(dir, "sessions", Safe(kidId) + ".snap.json")); } catch (Exception) { } }
            lock (gate) sessions.Remove(id);
            // closing is undoable: the chat moves to a trash folder for two weeks instead of being deleted
            try
            {
                string trash = Path.Combine(dir, "trash"); Directory.CreateDirectory(trash);
                foreach (string suffix in new[] { ".json", ".snap.json" })
                {
                    string from = Path.Combine(dir, "sessions", Safe(id) + suffix), to = Path.Combine(trash, Safe(id) + suffix);
                    if (!File.Exists(from)) continue; if (File.Exists(to)) File.Delete(to); File.Move(from, to);
                }
                string marker = Path.Combine(trash, Safe(id) + ".json"); if (File.Exists(marker)) File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
            }
            catch (Exception) { try { File.Delete(Path.Combine(dir, "sessions", Safe(id) + ".json")); File.Delete(Path.Combine(dir, "sessions", Safe(id) + ".snap.json")); } catch (Exception) { } }
            Raise();
        }
        /// <summary>Brings a closed chat back (Undo).</summary>
        public object RestoreClosed(string id)
        {
            string trash = Path.Combine(dir, "trash"), file = Path.Combine(trash, Safe(id) + ".json");
            if (!File.Exists(file)) throw new ArgumentException("That chat can't be restored any more.");
            lock (gate) if (sessions.ContainsKey(id)) return Dto(sessions[id]);
            Directory.CreateDirectory(Path.Combine(dir, "sessions"));
            foreach (string suffix in new[] { ".json", ".snap.json" })
            {
                string from = Path.Combine(trash, Safe(id) + suffix), to = Path.Combine(dir, "sessions", Safe(id) + suffix);
                if (File.Exists(from)) { if (File.Exists(to)) File.Delete(to); File.Move(from, to); }
            }
            LoadSessions(Path.Combine(dir, "sessions", Safe(id) + ".json"));
            Session s; lock (gate) { if (!sessions.TryGetValue(id, out s)) throw new InvalidOperationException("That chat couldn't be restored."); }
            Raise(); return Dto(s);
        }
        void PurgeTrash()
        {
            try { string trash = Path.Combine(dir, "trash"); if (Directory.Exists(trash)) foreach (string f in Directory.GetFiles(trash)) if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalDays > 14) { File.Delete(f); try { string imgs = Path.Combine(dir, "images", Safe(Path.GetFileNameWithoutExtension(f))); if (Directory.Exists(imgs)) Directory.Delete(imgs, true); } catch (Exception) { } } } catch (Exception) { }
        }
        public void Stop(string id)
        {
            Process p; Session sx; Session kid; lock (gate) { sx = Get(id); p = sx.Proc; kid = sx.Child; if (sx.Pair.WaitUntil != DateTime.MinValue) { sx.Pair.WaitUntil = DateTime.MinValue; sx.Pair.Pending = ""; } }
            if (kid != null) { try { Stop(kid.Id); } catch (Exception) { } }
            CancelBuiltin(sx); ClearPause(sx);
            if (p != null) { try { if (!p.HasExited) KillTree(p.Id); } catch (Exception) { } }
        }
        static void KillTree(int pid)
        {
            try { using (var k = Process.Start(new ProcessStartInfo("taskkill", "/PID " + pid + " /T /F") { CreateNoWindow = true, UseShellExecute = false })) k.WaitForExit(5000); } catch (Exception) { }
        }
        static string Safe(string id) { return new string((id ?? "").Where(char.IsLetterOrDigit).ToArray()); }

        public void Send(string id, string prompt)
        {
            Session top; lock (gate) top = Get(id);
            if (PairRoutes(top)) { PairSend(top, prompt); return; }
            SendCore(id, prompt, null, true);
        }
        /// <summary>Starts one turn. agentPrompt (when given) is what the agent is told; prompt is what the chat shows. showUser=false when the chat already shows the message.</summary>
        void SendCore(string id, string prompt, string agentPrompt, bool showUser)
        {
            if (String.IsNullOrWhiteSpace(prompt) && String.IsNullOrWhiteSpace(agentPrompt)) throw new ArgumentException("Write a message first.");
            if (prompt.Length > 100000) throw new ArgumentException("Message is too long.");
            Session s;
            lock (gate) { s = Get(id); if (s.Busy) throw new InvalidOperationException("This chat is still working. Stop it or wait."); s.HandoffDone = false; }
            var info = Harnesses().FirstOrDefault(h => h.Id == s.Harness);
            if (info == null || !info.Available) throw new InvalidOperationException((info == null ? "That agent" : info.Name) + " isn't installed on this computer.");
            string core = String.IsNullOrEmpty(agentPrompt) ? prompt : agentPrompt;
            try { if (s.Snapshot != null) { string drift = GitTools.DescribeDrift(s.Snapshot, GitTools.Snapshot(s.Cwd), s.SnapHead, GitTools.Head(s.Cwd), s.Cwd); if (drift != null) { Emit(s, "notice", drift, null); core = drift + "\n\n---\n\n" + core; } } } catch (Exception) { }
            string sent = core;
            if (!String.IsNullOrWhiteSpace(s.Rules) && !s.HasSentRules) { sent = "Follow these standing instructions for this whole conversation:\n" + s.Rules.Trim() + "\n\n---\n\n" + core; }
            if (s.Harness == "laica") { RunBuiltin(s, info, String.IsNullOrEmpty(agentPrompt) ? prompt : agentPrompt, sent); return; }
            if (s.Harness == "workflow") { RunWorkflowChat(s, prompt); return; }
            string exe = ResolveExe(info.Path);
            var psi = new ProcessStartInfo { FileName = exe, WorkingDirectory = s.Cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            psi.Arguments = Arguments(s, info, sent); CoAuthorEnv(psi, s.Cwd);
            if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) { psi.Arguments = "/c \"\"" + exe + "\" " + psi.Arguments + "\""; psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"; }
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            lock (gate) { s.Busy = true; s.Ended = false; s.Proc = proc; s.HasSentRules = true; if (s.Title.EndsWith(" chat") || s.Title == "New chat") s.Title = prompt.Length > 40 ? prompt.Substring(0, 40).Replace("\n", " ") + "…" : prompt.Replace("\n", " "); }
            if (showUser) Emit(s, "user", prompt, null);
            Raise();
            try { proc.Start(); }
            catch (Exception ex) { Finish(s, "Could not start " + info.Name + ": " + ex.Message); return; }
            if (info.Parser == "claude")
            {
                // Interactive stream-json: the user message goes in as one JSON line and stdin stays open so permission answers can follow.
                s.Interactive = true;
                WriteLine(s, json.Serialize(new Dictionary<string, object> { { "type", "user" }, { "message", new Dictionary<string, object> { { "role", "user" }, { "content", sent } } } }));
            }
            else if (info.Parser == "codex" && s.Mode == "ask-first")
            {
                s.Interactive = true; s.TurnPrompt = sent;
                WriteLine(s, json.Serialize(new Dictionary<string, object> { { "id", 1 }, { "method", "initialize" }, { "params", new Dictionary<string, object> { { "clientInfo", new Dictionary<string, object> { { "name", "laica" }, { "title", "LAICA" }, { "version", AppVersion.Value } } }, { "capabilities", new Dictionary<string, object> { { "experimentalApi", true } } } } } }));
            }
            else
            {
                s.Interactive = false;
                byte[] bytes = new UTF8Encoding(false).GetBytes(sent);
                try { if (!(info.Args ?? "").Contains("{prompt}")) proc.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); proc.StandardInput.Close(); } catch (Exception) { }
            }
            var errors = new StringBuilder();
            proc.ErrorDataReceived += (o, e) => { if (e.Data != null && errors.Length < 4000) errors.AppendLine(e.Data); };
            proc.BeginErrorReadLine();
            var reader = new Thread(() =>
            {
                try { string line; while ((line = proc.StandardOutput.ReadLine()) != null) { if (line.Length > 0) Parse(s, info.Parser, line); } } catch (Exception) { }
                try { proc.WaitForExit(); } catch (Exception) { }
                int code = 0; try { code = proc.ExitCode; } catch (Exception) { }
                bool clean; lock (gate) clean = s.Ended;
                Finish(s, clean ? null : code != 0 && errors.Length > 0 ? errors.ToString().Trim() : (code != 0 ? info.Name + " stopped (exit " + code + ")." : null));
            }) { IsBackground = true, Name = "harness-" + s.Id };
            reader.Start();
        }

        static string Quote(string v) { return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
        static readonly string[] Efforts = { "minimal", "low", "medium", "high", "xhigh", "max" };
        static string ValidEffort(string e) { e = (e ?? "").Trim().ToLowerInvariant(); return Array.IndexOf(Efforts, e) >= 0 ? e : null; }
        static bool UseEffort(Session s) { return !String.IsNullOrEmpty(s.Effort) && Array.IndexOf(Efforts, s.Effort) >= 0; }
        /// <summary>The command line LAICA would use for a new or resumed chat (used by tests and diagnostics).</summary>
        public string ArgumentPreview(string harness, string mode, string model, string effort, bool resumed) { var info = Harnesses().First(h => h.Id == harness); var s = new Session { Harness = harness, Mode = mode, Model = model, Effort = ValidEffort(effort), Cwd = "." }; if (resumed) s.ExternalId = "prior-session"; return Arguments(s, info, ""); }
        static bool UseModel(Session s) { return !String.IsNullOrEmpty(s.Model) && s.Model != "default" && System.Text.RegularExpressions.Regex.IsMatch(s.Model, @"^[A-Za-z0-9._\-]{1,80}$"); }
        string Arguments(Session s, HarnessInfo info, string prompt)
        {
            if (info.Custom) return (info.Args ?? "").Replace("{prompt}", Quote(prompt)).Replace("{cwd}", Quote(s.Cwd));
            if (s.Harness == "claude")
                return "-p --output-format stream-json --input-format stream-json --verbose --permission-prompt-tool stdio --permission-mode " + s.Mode + (UseModel(s) ? " --model " + s.Model : "") + (UseEffort(s) ? " --effort " + s.Effort : "") + (s.ExternalId != null ? " --resume " + s.ExternalId : "");
            if (s.Harness == "codex" && s.Mode == "ask-first") return "app-server";
            if (s.Harness == "codex")
            {
                string sandbox = s.Mode == "danger-full-access" ? "--dangerously-bypass-approvals-and-sandbox" : "-s " + (s.Mode == "read-only" ? "read-only" : "workspace-write");
                string review = s.Mode == "approve-for-me" ? "--approve-for-me " : "";   // Codex's own automatic reviewer approves the safe requests and refuses the rest
                if (s.Mode == "approve-for-me") sandbox = "";
                return s.ExternalId != null ? "exec " + review + "resume " + s.ExternalId + " --json --skip-git-repo-check -" : "exec " + review + "--json --skip-git-repo-check " + (UseModel(s) ? "-m " + s.Model + " " : "") + (UseEffort(s) ? "-c model_reasoning_effort=" + s.Effort + " " : "") + sandbox + " -";
            }
            if (s.Harness == "gemini" || s.Harness == "qwen") return "-p \" \"" + (UseModel(s) ? " -m " + s.Model : "") + (s.Mode == "yolo" ? " --yolo" : "");
            if (s.Harness == "opencode") return "run";
            if (s.Harness == "goose") return "run -i -";
            if (s.Harness == "aider") return "--yes --no-pretty --message-file -";
            return "-p \" \"";
        }

        void Finish(Session s, string error)
        {
            try { var snap = GitTools.Snapshot(s.Cwd); string head = GitTools.Head(s.Cwd); lock (gate) { s.Snapshot = snap; s.SnapHead = head; } } catch (Exception) { }
            lock (gate) { s.Busy = false; s.Proc = null; s.Pending.Clear(); }
            ClearPause(s);
            if (!String.IsNullOrEmpty(error)) Emit(s, "error", error, null);
            CountTurn(s);
            Emit(s, "done", "", null);
            if (s.Notify) { string last = ""; lock (gate) { for (int i = s.Events.Count - 1; i >= 0; i--) if (Str(s.Events[i], "Kind") == "assistant") { last = Str(s.Events[i], "Text"); break; } } var done = Completed; if (done != null) done("Task finished: " + s.Title, String.IsNullOrEmpty(error) ? last : "Problem: " + error); }
            PairAfterTurn(s);
            SaveSession(s); Raise();
        }

        void Parse(Session s, string parser, string line)
        {
            if (parser == "text") { Emit(s, "assistant", line, "append"); return; }
            Dictionary<string, object> o;
            try { o = json.Deserialize<Dictionary<string, object>>(line); } catch (Exception) { Emit(s, "log", line, null); return; }
            string type = Str(o, "type");
            if (parser == "claude") ParseClaude(s, o, type); else if (s.Mode == "ask-first" && parser == "codex") ParseCodexApp(s, o); else ParseCodex(s, o, type);
        }

        void WriteLine(Session s, string line)
        {
            Process p; lock (gate) p = s.Proc; if (p == null) return;
            lock (s.InLock) { try { var bytes = new UTF8Encoding(false).GetBytes(line + "\n"); p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); p.StandardInput.BaseStream.Flush(); } catch (Exception) { } }
        }
        static string Summarize(string tool, Dictionary<string, object> input)
        {
            if (input == null) return "";
            string cmd = Str(input, "command"), path = Str(input, "file_path");
            if (cmd != "") return cmd;
            if (path != "") { string body = Str(input, "content") != "" ? Str(input, "content") : Str(input, "new_string"); return path + (body != "" ? "\n\n" + Trim(body) : ""); }
            return Trim(new JavaScriptSerializer().Serialize(input));
        }
        void HandleControl(Session s, Dictionary<string, object> o)
        {
            string rid = Str(o, "request_id"); var req = Obj(o, "request");
            if (req == null || Str(req, "subtype") != "can_use_tool") { if (rid != "") WriteLine(s, json.Serialize(new Dictionary<string, object> { { "type", "control_response" }, { "response", new Dictionary<string, object> { { "subtype", "error" }, { "request_id", rid }, { "error", "Unsupported request." } } } })); return; }
            string tool = Str(req, "tool_name"); var input = Obj(req, "input") ?? new Dictionary<string, object>();
            bool auto; lock (gate) auto = s.AlwaysAllow.Contains(tool);
            if (auto) { Respond(s, rid, input, true); return; }
            lock (gate) s.Pending[rid] = input;
            Emit(s, "approval", tool, json.Serialize(new Dictionary<string, object> { { "RequestId", rid }, { "Input", Summarize(tool, input) } }));
        }
        void Respond(Session s, string rid, Dictionary<string, object> input, bool allow)
        {
            var body = allow ? new Dictionary<string, object> { { "behavior", "allow" }, { "updatedInput", input } } : new Dictionary<string, object> { { "behavior", "deny" }, { "message", "The user denied this action in LAICA." } };
            WriteLine(s, json.Serialize(new Dictionary<string, object> { { "type", "control_response" }, { "response", new Dictionary<string, object> { { "subtype", "success" }, { "request_id", rid }, { "response", body } } } }));
        }
        public void Approve(string id, string requestId, bool allow, bool always)
        {
            Session s; Dictionary<string, object> input; string tool = null;
            lock (gate) { s = Get(id); if (!s.Pending.ContainsKey(requestId) && s.Child != null && s.Child.Pending.ContainsKey(requestId)) s = s.Child; if (!s.Pending.TryGetValue(requestId, out input)) throw new InvalidOperationException("That request was already answered."); s.Pending.Remove(requestId); }
            object waiter; if (input.TryGetValue("Wait", out waiter)) { if (always && allow) lock (gate) s.AlwaysAllow.Add(Str(input, "Tool")); input["Allow"] = allow; ((ManualResetEventSlim)waiter).Set(); Emit(s, "approval_result", allow ? "allowed" : "denied", requestId); return; }
            if (always && allow) { lock (gate) { var ev = s.Events.LastOrDefault(e => Str(e, "Kind") == "approval" && Str(e, "Detail").Contains(requestId)); if (ev != null) tool = Str(ev, "Text"); if (tool != null) s.AlwaysAllow.Add(tool); } }
            if (input.ContainsKey("RpcId")) RespondCodex(s, input["RpcId"], allow, always); else Respond(s, requestId, input, allow);
            Emit(s, "approval_result", allow ? "allowed" : "denied", requestId);
        }

        void ParseClaude(Session s, Dictionary<string, object> o, string type)
        {
            if (type == "control_request") { HandleControl(s, o); return; }
            if (type == "system") { string sid = Str(o, "session_id"); if (sid != "" && Str(o, "subtype") == "init") lock (gate) s.ExternalId = sid; return; }
            if (type == "rate_limit_event") { NoteClaudeRateLimit(o); return; } if (type == "result") { AddTokens(s, Obj(o, "usage")); if (s.Interactive) { try { Process p; lock (gate) p = s.Proc; if (p != null) lock (s.InLock) p.StandardInput.Close(); } catch (Exception) { } } string sid = Str(o, "session_id"); if (sid != "") lock (gate) s.ExternalId = sid; if (o.ContainsKey("is_error") && Convert.ToBoolean(o["is_error"])) Emit(s, "error", Str(o, "result"), null); return; }
            var msg = Obj(o, "message"); var content = msg == null ? null : Arr(msg, "content");
            if (content == null) return;
            foreach (object item in content)
            {
                var c = item as Dictionary<string, object>; if (c == null) continue;
                string ct = Str(c, "type");
                if (type == "assistant" && ct == "text") Emit(s, "assistant", Str(c, "text"), null);
                else if (type == "assistant" && ct == "thinking") Emit(s, "thinking", Str(c, "thinking"), null);
                else if (type == "assistant" && ct == "tool_use") Emit(s, "tool", Str(c, "name"), json.Serialize(c.ContainsKey("input") ? c["input"] : null));
                else if (type == "user" && ct == "tool_result") Emit(s, "tool_result", Trim(Flatten(c.ContainsKey("content") ? c["content"] : null)), Str(c, "is_error") == "True" ? "error" : null, SaveImages(s, c.ContainsKey("content") ? c["content"] : null));
                else if (type == "user" && ct == "image") { var im = SaveImages(s, new object[] { c }); if (im.Count > 0) Emit(s, "image", "", "attached", im); }
            }
        }

        // ---------- Codex app-server (approval-capable) ----------
        void EndTurn(Session s) { Process p; lock (gate) { s.Ended = true; p = s.Proc; } if (p != null) KillTree(p.Id); }
        void RespondCodex(Session s, object rpcId, bool allow, bool always)
        {
            WriteLine(s, json.Serialize(new Dictionary<string, object> { { "id", rpcId }, { "result", new Dictionary<string, object> { { "decision", allow ? (always ? "acceptForSession" : "accept") : "decline" } } } }));
        }
        void ParseCodexApp(Session s, Dictionary<string, object> o)
        {
            string method = Str(o, "method"); bool hasId = o.ContainsKey("id");
            if (method == "" && hasId)
            {
                string id = Str(o, "id"); var err = Obj(o, "error"); var res = Obj(o, "result");
                if (err != null) { Emit(s, "error", Str(err, "message"), null); EndTurn(s); return; }
                string approval = "untrusted", sandbox = s.Mode == "ask-first" ? "workspace-write" : s.Mode;
                if (id == "1")
                {
                    WriteLine(s, "{\"method\":\"initialized\"}");
                    var p = new Dictionary<string, object> { { "cwd", s.Cwd }, { "approvalPolicy", approval }, { "sandbox", sandbox } }; if (UseModel(s)) p["model"] = s.Model;
                    if (s.ExternalId != null) p["threadId"] = s.ExternalId;
                    WriteLine(s, json.Serialize(new Dictionary<string, object> { { "id", 2 }, { "method", s.ExternalId != null ? "thread/resume" : "thread/start" }, { "params", p } }));
                }
                else if (id == "2")
                {
                    string tid = Str(Obj(res, "thread"), "id"); if (tid != "") lock (gate) s.ExternalId = tid;
                    WriteLine(s, json.Serialize(new Dictionary<string, object> { { "id", 3 }, { "method", "turn/start" }, { "params", new Dictionary<string, object> { { "threadId", s.ExternalId }, { "effort", UseEffort(s) ? s.Effort : null }, { "input", new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", s.TurnPrompt } } } } } } }));
                }
                return;
            }
            var prm = Obj(o, "params");
            if (method != "" && hasId)
            {
                if (method == "item/commandExecution/requestApproval" || method == "item/fileChange/requestApproval")
                {
                    bool cmd = method.StartsWith("item/command"); string tool = cmd ? "shell" : "file change";
                    bool auto; lock (gate) auto = s.AlwaysAllow.Contains(tool);
                    if (auto) { RespondCodex(s, o["id"], true, false); return; }
                    string key = "c" + Str(o, "id"); string detail = cmd ? Str(prm, "command") : (Str(prm, "grantRoot") != "" ? "Write access to " + Str(prm, "grantRoot") : "Apply file changes");
                    if (Str(prm, "reason") != "") detail += "\n\n" + Str(prm, "reason");
                    lock (gate) s.Pending[key] = new Dictionary<string, object> { { "RpcId", o["id"] } };
                    Emit(s, "approval", tool, json.Serialize(new Dictionary<string, object> { { "RequestId", key }, { "Input", detail } }));
                }
                else WriteLine(s, json.Serialize(new Dictionary<string, object> { { "id", o["id"] }, { "error", new Dictionary<string, object> { { "code", -32601 }, { "message", "LAICA does not support this request." } } } }));
                return;
            }
            if (method == "turn/completed") { EndTurn(s); return; }
            if (method == "error") { Emit(s, "error", Str(Obj(prm, "error"), "message") != "" ? Str(Obj(prm, "error"), "message") : json.Serialize(prm), null); return; }
            var item = Obj(prm, "item"); if (item == null) return;
            string it = Str(item, "type");
            if (method == "item/started" && it == "commandExecution") { Emit(s, "tool", "shell", Str(item, "command")); return; }
            if (method != "item/completed") return;
            if (it == "agentMessage") Emit(s, "assistant", Str(item, "text"), null);
            else if (it == "reasoning") { string txt = Flatten(item.ContainsKey("summary") ? item["summary"] : null); if (txt != "") Emit(s, "thinking", txt, null); }
            else if (it == "commandExecution") Emit(s, "tool_result", Trim(Str(item, "aggregatedOutput")), Str(item, "command"));
            else if (it == "fileChange") Emit(s, "tool", "file change", json.Serialize(item.ContainsKey("changes") ? item["changes"] : null));
            else if (it == "mcpToolCall") Emit(s, "tool", Str(item, "server") + "." + Str(item, "tool"), json.Serialize(item.ContainsKey("arguments") ? item["arguments"] : null), SaveImages(s, Obj(item, "result") != null && Obj(item, "result").ContainsKey("content") ? Obj(item, "result")["content"] : item.ContainsKey("result") ? item["result"] : null));
            else if (it.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0) { var im = ImagesFromItem(s, item); if (im.Count > 0) Emit(s, "image", it, Str(item, "path"), im); }
        }

        void ParseCodex(Session s, Dictionary<string, object> o, string type)
        {
            if (type == "thread.started") { lock (gate) s.ExternalId = Str(o, "thread_id"); return; }
            if (type == "turn.failed") return;
            if (type == "turn.completed") { AddTokens(s, Obj(o, "usage")); return; }
            if (type == "error") { Emit(s, "error", Str(o, "message") != "" ? Str(o, "message") : json.Serialize(o), null); return; }
            var item = Obj(o, "item"); if (item == null) return;
            string it = Str(item, "type");
            if (type == "item.started" && it == "command_execution") { Emit(s, "tool", "shell", Str(item, "command")); return; }
            if (type != "item.completed") return;
            if (it == "agent_message") Emit(s, "assistant", Str(item, "text"), null);
            else if (it == "reasoning") Emit(s, "thinking", Str(item, "text"), null);
            else if (it == "command_execution") Emit(s, "tool_result", Trim(Str(item, "aggregated_output")), Str(item, "command"));
            else if (it == "file_change") Emit(s, "tool", "file change", json.Serialize(item.ContainsKey("changes") ? item["changes"] : null));
            else if (it == "mcp_tool_call") Emit(s, "tool", Str(item, "server") + "." + Str(item, "tool"), json.Serialize(item.ContainsKey("arguments") ? item["arguments"] : null), SaveImages(s, Obj(Obj(item, "result"), "content") != null ? null : (Obj(item, "result") != null && Obj(item, "result").ContainsKey("content") ? Obj(item, "result")["content"] : item.ContainsKey("result") ? item["result"] : null)));
            else if (it.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0) { var im = ImagesFromItem(s, item); if (im.Count > 0) Emit(s, "image", it, Str(item, "path"), im); }
        }

        static string Str(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : ""; }
        static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }
        static IEnumerable Arr(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && !(v is string) ? v as IEnumerable : null; }
        static string Trim(string t) { return t != null && t.Length > 6000 ? t.Substring(0, 6000) + "\n… (truncated)" : t; }
        static string Flatten(object v)
        {
            if (v == null) return "";
            if (v is string) return (string)v;
            var list = v as IEnumerable; var sb = new StringBuilder();
            if (list != null) foreach (object x in list) { var d = x as Dictionary<string, object>; sb.AppendLine(d != null ? Str(d, "text") : Convert.ToString(x)); }
            return sb.ToString().Trim();
        }

        void Emit(Session s, string kind, string text, string detail) { Emit(s, kind, text, detail, null); }
        void Emit(Session s, string kind, string text, string detail, IList<string> images)
        {
            var e = new Dictionary<string, object> { { "SessionId", s.Id }, { "Kind", kind }, { "Text", text }, { "Detail", detail }, { "TimeUtc", DateTime.UtcNow.ToString("o") } };
            if (images != null && images.Count > 0) e["Images"] = images.ToArray();
            lock (gate) { e["N"] = ++s.Seq; if (s.Parent != null || s.Child != null || s.Pair.Mode != "") e["By"] = VendorKey(s); s.Events.Add(e); if (s.Events.Count > 3000) s.Events.RemoveRange(0, 500); }
            var h = Event; if (h != null) h(e);
            if (s.Parent != null && kind != "user") MirrorToMain(s, e);
            if (kind == "error") NoteError(s, text);
        }
        /// <summary>What the tag-team partner does appears in the main chat as well, tagged with the vendor that did it.</summary>
        void MirrorToMain(Session child, Dictionary<string, object> e)
        {
            var main = child.Parent; var c = new Dictionary<string, object>(e);
            lock (gate) { c["SessionId"] = main.Id; c["N"] = ++main.Seq; c["By"] = VendorKey(child); main.Events.Add(c); if (main.Events.Count > 3000) main.Events.RemoveRange(0, 500); }
            var h = Event; if (h != null) h(c);
        }
        void Raise() { var h = Changed; if (h != null) h(); }

        // ---------- persistence ----------
        List<Dictionary<string, object>> LoadList(string name)
        {
            try { string p = Path.Combine(dir, name); if (File.Exists(p)) return json.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(p)) ?? new List<Dictionary<string, object>>(); } catch (Exception) { }
            return new List<Dictionary<string, object>>();
        }
        void SaveList(string name, object list) { try { string p = Path.Combine(dir, name), t = p + ".tmp"; File.WriteAllText(t, json.Serialize(list)); if (File.Exists(p)) File.Delete(p); File.Move(t, p); } catch (Exception) { } }
        void SaveSession(Session s)
        {
            Dictionary<string, object> d;
            lock (gate) d = new Dictionary<string, object> { { "Id", s.Id }, { "Harness", s.Harness }, { "Cwd", s.Cwd }, { "Mode", s.Mode }, { "ExternalId", s.ExternalId }, { "Title", s.Title }, { "Rules", s.Rules }, { "AssistantId", s.AssistantId }, { "HasSentRules", s.HasSentRules }, { "Isolated", s.Isolated }, { "RepoRoot", s.RepoRoot }, { "Branch", s.Branch }, { "BaseBranch", s.BaseBranch }, { "BaseCommit", s.BaseCommit }, { "WorktreePath", s.WorktreePath }, { "ServiceId", s.ServiceId }, { "Model", s.Model }, { "ImageModel", s.ImageModel }, { "Effort", s.Effort }, { "TeamId", s.TeamId }, { "ParentId", s.ParentId }, { "Seq", s.Seq }, { "Pair", PairToDict(s.Pair) }, { "Messages", s.Messages.ToArray() }, { "Events", s.Events.ToArray() } };
            try { string p = Path.Combine(dir, "sessions", Safe(s.Id) + ".json"), t = p + ".tmp"; File.WriteAllText(t, json.Serialize(d)); if (File.Exists(p)) File.Delete(p); File.Move(t, p); } catch (Exception) { }
            try { Dictionary<string, string> snap; string head; lock (gate) { snap = s.Snapshot; head = s.SnapHead; } if (snap != null) { string p = Path.Combine(dir, "sessions", Safe(s.Id) + ".snap.json"), t = p + ".tmp"; File.WriteAllText(t, json.Serialize(new Dictionary<string, object> { { "Head", head }, { "Files", snap } })); if (File.Exists(p)) File.Delete(p); File.Move(t, p); } } catch (Exception) { }
        }
        void LoadSessions(string only = null)
        {
            try
            {
                foreach (string f in only != null ? new[] { only } : Directory.GetFiles(Path.Combine(dir, "sessions"), "*.json").Where(x => !x.EndsWith(".snap.json", StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    try
                    {
                        var d = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
                        var s = new Session { Id = Str(d, "Id"), Harness = Str(d, "Harness"), Cwd = Str(d, "Cwd"), Mode = Str(d, "Mode"), ExternalId = Str(d, "ExternalId") == "" ? null : Str(d, "ExternalId"), Title = Str(d, "Title"), Rules = Str(d, "Rules"), AssistantId = Str(d, "AssistantId"), HasSentRules = Str(d, "HasSentRules") == "True", Isolated = Str(d, "Isolated") == "True", RepoRoot = Str(d, "RepoRoot"), Branch = Str(d, "Branch"), BaseBranch = Str(d, "BaseBranch"), BaseCommit = Str(d, "BaseCommit"), WorktreePath = Str(d, "WorktreePath"), ServiceId = Str(d, "ServiceId"), Model = Str(d, "Model"), ImageModel = Str(d, "ImageModel") == "" ? null : Str(d, "ImageModel"), Effort = Str(d, "Effort") == "" ? null : Str(d, "Effort"), TeamId = Str(d, "TeamId") == "" ? null : Str(d, "TeamId"), ParentId = Str(d, "ParentId"), Seq = Num(d, "Seq"), Pair = PairFromDict(Obj(d, "Pair")) };
                        var msgs = Arr(d, "Messages"); if (msgs != null) foreach (object mo in msgs) { var md = mo as Dictionary<string, object>; if (md != null) s.Messages.Add(md); }
                        if (s.Id == "") continue;
                        var ev = Arr(d, "Events"); if (ev != null) foreach (object o in ev) { var e = o as Dictionary<string, object>; if (e != null) s.Events.Add(e); }
                        try { string sp = Path.Combine(dir, "sessions", Safe(s.Id) + ".snap.json"); if (File.Exists(sp)) { var sd = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(sp)); s.SnapHead = Str(sd, "Head"); var files = Obj(sd, "Files"); if (files != null) { s.Snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); foreach (var kv in files) s.Snapshot[kv.Key] = Convert.ToString(kv.Value); } } } catch (Exception) { }
                        sessions[s.Id] = s;
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        // ---------- custom agents ----------
        public object Agents() { lock (gate) return agents.ToArray(); }
        public object SaveAgent(Dictionary<string, object> a)
        {
            string name = Str(a, "Name").Trim(), cmd = Str(a, "Command").Trim();
            if (name == "" || cmd == "") throw new ArgumentException("Give the agent a name and a command.");
            string id = Str(a, "Id"); if (id == "") id = "custom-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var rec = new Dictionary<string, object> { { "Id", id }, { "Name", name }, { "Command", cmd }, { "Args", Str(a, "Args") }, { "Parser", Str(a, "Parser") == "" ? "text" : Str(a, "Parser") } };
            lock (gate) { agents.RemoveAll(x => Str(x, "Id") == id); agents.Add(rec); SaveList("agents.json", agents); }
            Raise(); return rec;
        }
        public void DeleteAgent(string id) { lock (gate) { agents.RemoveAll(x => Str(x, "Id") == id); SaveList("agents.json", agents); } Raise(); }

        // ---------- scheduled tasks ----------
        public object Tasks() { lock (gate) return tasks.ToArray(); }
        public object SaveTask(Dictionary<string, object> t)
        {
            string kind = Str(t, "Kind"), prompt = Str(t, "Prompt").Trim(), name = Str(t, "Name").Trim();
            if (prompt == "" || name == "") throw new ArgumentException("Give the task a name and a prompt.");
            if (kind != "interval" && kind != "cron" && kind != "once") throw new ArgumentException("Choose how often the task runs.");
            if (kind == "cron") CronMatches(Str(t, "Cron"), DateTime.Now);
            string id = Str(t, "Id"); if (id == "") id = Guid.NewGuid().ToString("N");
            string last = ""; lock (gate) { var old = tasks.FirstOrDefault(x => Str(x, "Id") == id); if (old != null) last = Str(old, "LastRunUtc"); }
            var rec = new Dictionary<string, object> { { "Id", id }, { "Name", name }, { "Kind", kind }, { "Prompt", prompt }, { "Harness", Str(t, "Harness") }, { "Cwd", Str(t, "Cwd") }, { "Mode", Str(t, "Mode") }, { "Cron", Str(t, "Cron") }, { "Minutes", Str(t, "Minutes") == "" ? 60 : Convert.ToInt32(Str(t, "Minutes")) }, { "RunAt", Str(t, "RunAt") }, { "Enabled", Str(t, "Enabled") != "False" }, { "NewSession", Str(t, "NewSession") != "False" }, { "Isolate", Str(t, "Isolate") == "True" }, { "SessionId", Str(t, "SessionId") }, { "LastRunUtc", last }, { "CreatedUtc", Str(t, "CreatedUtc") == "" ? DateTime.UtcNow.ToString("o") : Str(t, "CreatedUtc") } };
            lock (gate) { tasks.RemoveAll(x => Str(x, "Id") == id); tasks.Add(rec); SaveList("tasks.json", tasks); }
            Raise(); return rec;
        }
        public void DeleteTask(string id) { lock (gate) { tasks.RemoveAll(x => Str(x, "Id") == id); SaveList("tasks.json", tasks); } Raise(); }
        public void RunTask(string id) { Dictionary<string, object> t; lock (gate) t = tasks.FirstOrDefault(x => Str(x, "Id") == id); if (t == null) throw new ArgumentException("That task no longer exists."); Execute(t); }

        void Tick()
        {
            List<Dictionary<string, object>> due = new List<Dictionary<string, object>>();
            DateTime now = DateTime.UtcNow;
            lock (gate)
            {
                foreach (var t in tasks)
                {
                    if (Str(t, "Enabled") == "False") continue;
                    DateTime last; bool has = DateTime.TryParse(Str(t, "LastRunUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out last);
                    string kind = Str(t, "Kind");
                    try
                    {
                        if (kind == "interval") { DateTime created = DateTime.Parse(Str(t, "CreatedUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind); DateTime basis = has ? last : created; if (basis.AddMinutes(Math.Max(1, Convert.ToInt32(Str(t, "Minutes")))) <= now) due.Add(t); }
                        else if (kind == "once") { DateTime at; if (!has && DateTime.TryParse(Str(t, "RunAt"), out at) && at.ToUniversalTime() <= now) due.Add(t); }
                        else if (kind == "cron") { DateTime local = DateTime.Now; if (CronMatches(Str(t, "Cron"), local) && (!has || last.ToLocalTime().ToString("yyyyMMddHHmm") != local.ToString("yyyyMMddHHmm"))) due.Add(t); }
                    }
                    catch (Exception) { }
                }
            }
            foreach (var t in due) { try { Execute(t); } catch (Exception) { } }
            try { PairTick(); } catch (Exception) { }
        }

        void Execute(Dictionary<string, object> t)
        {
            string sid = Str(t, "SessionId"), cwd = Str(t, "Cwd");
            lock (gate) { t["LastRunUtc"] = DateTime.UtcNow.ToString("o"); SaveList("tasks.json", tasks); }
            Session s = null;
            lock (gate) { if (Str(t, "NewSession") == "False" && sid != "" && sessions.ContainsKey(sid)) s = sessions[sid]; }
            if (s != null && s.Busy) return;
            if (s == null)
            {
                var dto = (Dictionary<string, object>)Create(Str(t, "Harness"), cwd, Str(t, "Mode"), null, null, "⏰ " + Str(t, "Name"), Str(t, "Isolate") == "True");
                sid = Str(dto, "Id"); lock (gate) { t["SessionId"] = sid; sessions[sid].Notify = true; SaveList("tasks.json", tasks); }
            }
            Send(sid, Str(t, "Prompt"));
        }

        // Minimal 5-field cron: minute hour day-of-month month day-of-week; supports * , - /
        public static bool CronMatches(string expr, DateTime when)
        {
            var f = (expr ?? "").Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length != 5) throw new ArgumentException("Cron needs 5 fields: minute hour day month weekday.");
            int dow = (int)when.DayOfWeek;
            return Field(f[0], when.Minute, 0, 59) & Field(f[1], when.Hour, 0, 23) & Field(f[2], when.Day, 1, 31) & Field(f[3], when.Month, 1, 12) & (Field(f[4], dow, 0, 7) | (dow == 0 && Field(f[4], 7, 0, 7)));
        }
        static bool Field(string spec, int value, int min, int max)
        {
            bool ok = false;
            foreach (string part in spec.Split(','))
            {
                string range = part; int step = 1; int slash = part.IndexOf('/');
                if (slash >= 0) { range = part.Substring(0, slash); if (!Int32.TryParse(part.Substring(slash + 1), out step) || step < 1) throw new ArgumentException("Bad cron step: " + part); }
                int lo = min, hi = max;
                if (range != "*" && range != "") { int dash = range.IndexOf('-'); if (dash > 0) { if (!Int32.TryParse(range.Substring(0, dash), out lo) || !Int32.TryParse(range.Substring(dash + 1), out hi)) throw new ArgumentException("Bad cron range: " + part); } else { if (!Int32.TryParse(range, out lo)) throw new ArgumentException("Bad cron value: " + part); hi = slash >= 0 ? max : lo; } }
                if (lo < min || hi > max || lo > hi) throw new ArgumentException("Cron value out of range: " + part);
                if (value >= lo && value <= hi && (value - lo) % step == 0) ok = true;
            }
            return ok;
        }

        // ---------- workspace files ----------
        static string Inside(string cwd, string rel)
        {
            string root = Path.GetFullPath(cwd).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, (rel ?? "").TrimStart('\\', '/')));
            if (!(full + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("That path is outside the working folder.");
            return full;
        }
        string CwdOf(string id) { lock (gate) return Get(id).Cwd; }
        public object Files(string id, string rel)
        {
            string cwd = CwdOf(id), full = Inside(cwd, rel);
            if (!Directory.Exists(full)) throw new ArgumentException("That folder doesn't exist.");
            string root = Path.GetFullPath(cwd).TrimEnd('\\', '/');
            var list = new List<Dictionary<string, object>>();
            foreach (var d in new DirectoryInfo(full).GetDirectories().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(400)) { if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue; list.Add(new Dictionary<string, object> { { "Name", d.Name }, { "Path", d.FullName.Substring(root.Length).TrimStart('\\') }, { "Dir", true }, { "Size", 0L } }); }
            foreach (var f in new DirectoryInfo(full).GetFiles().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(600)) list.Add(new Dictionary<string, object> { { "Name", f.Name }, { "Path", f.FullName.Substring(root.Length).TrimStart('\\') }, { "Dir", false }, { "Size", f.Length } });
            return list.ToArray();
        }
        public object ReadFile(string id, string rel)
        {
            string full = Inside(CwdOf(id), rel);
            var fi = new FileInfo(full); if (!fi.Exists) throw new ArgumentException("That file doesn't exist.");
            string ext = fi.Extension.ToLowerInvariant();
            if (ext == ".docx" || ext == ".xlsx" || ext == ".pptx" || ext == ".pdf") return OfficePreview.Read(full, ext);
            string mime = ext == ".png" ? "image/png" : ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : ext == ".gif" ? "image/gif" : ext == ".webp" ? "image/webp" : ext == ".bmp" ? "image/bmp" : ext == ".svg" ? "image/svg+xml" : null;
            if (mime != null) { if (fi.Length > 6 * 1024 * 1024) return new Dictionary<string, object> { { "Kind", "toolarge" }, { "Size", fi.Length } }; return new Dictionary<string, object> { { "Kind", "image" }, { "DataUri", "data:" + mime + ";base64," + Convert.ToBase64String(File.ReadAllBytes(full)) } }; }
            if (fi.Length > 2 * 1024 * 1024) return new Dictionary<string, object> { { "Kind", "toolarge" }, { "Size", fi.Length } };
            byte[] bytes = File.ReadAllBytes(full);
            if (bytes.Take(8000).Any(b => b == 0)) return new Dictionary<string, object> { { "Kind", "binary" }, { "Size", fi.Length } };
            return new Dictionary<string, object> { { "Kind", "text" }, { "Text", Encoding.UTF8.GetString(bytes) }, { "Ext", ext }, { "Size", fi.Length } };
        }
        public void WriteFile(string id, string rel, string text)
        {
            string full = Inside(CwdOf(id), rel);
            if (!File.Exists(full)) throw new ArgumentException("Only existing files can be saved.");
            if (text != null && text.Length > 2 * 1024 * 1024) throw new ArgumentException("File is too large to save here.");
            File.WriteAllText(full, text ?? "", new UTF8Encoding(false));
        }

        // ---------- small key/value stores owned by the UI (assistants) ----------
        public object StoreGet(string name) { string p = Path.Combine(dir, "store-" + Safe(name) + ".json"); try { return File.Exists(p) ? json.DeserializeObject(File.ReadAllText(p)) : null; } catch (Exception) { return null; } }
        public void StoreSet(string name, object value) { SaveList("store-" + Safe(name) + ".json", value); }

        public void Dispose()
        {
            if (scheduler != null) scheduler.Dispose();
            List<Session> all; lock (gate) all = sessions.Values.ToList();
            foreach (var s in all) { try { if (s.Proc != null && !s.Proc.HasExited) KillTree(s.Proc.Id); } catch (Exception) { } }
        }
    }
}
