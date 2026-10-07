using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Laica;

public sealed class FakeApi : IDisposable
{
    public readonly System.Net.HttpListener Listener = new System.Net.HttpListener(); public readonly int Port; public readonly List<string> Bodies = new List<string>(); public readonly List<string> Headers = new List<string>(); readonly Thread loop;
    public FakeApi()
    {
        Port = new Random().Next(20000, 40000);
        for (int i = 0; ; i++) { try { Listener.Prefixes.Clear(); Listener.Prefixes.Add("http://127.0.0.1:" + Port + "/"); Listener.Start(); break; } catch (Exception) { if (i > 20) throw; Port++; } }
        loop = new Thread(Run) { IsBackground = true }; loop.Start();
    }
    static string Esc(string s) { return s.Replace("\\", "\\\\").Replace("\"", "\\\""); }
    void Run()
    {
        while (Listener.IsListening)
        {
            System.Net.HttpListenerContext c; try { c = Listener.GetContext(); } catch (Exception) { return; }
            string body; using (var r = new StreamReader(c.Request.InputStream)) body = r.ReadToEnd();
            lock (Bodies) { Bodies.Add(body); Headers.Add("x-api-key=" + c.Request.Headers["x-api-key"] + ";version=" + c.Request.Headers["anthropic-version"] + ";auth=" + c.Request.Headers["Authorization"]); }
            string path = c.Request.Url.AbsolutePath, reply;
            int iu = Math.Max(body.LastIndexOf("\"role\":\"user\",\"content\":\""), body.LastIndexOf("\"role\":\"user\",\"content\":["));
            bool afterTool = iu >= 0 && body.LastIndexOf("\"role\":\"tool\"") > iu || (iu >= 0 && body.Substring(iu).Contains("tool_result"));
            string last = ""; { int i = body.LastIndexOf("\"role\":\"user\",\"content\":\""); if (i >= 0) { last = body.Substring(i + 25); int e = last.IndexOf('"'); if (e >= 0) last = last.Substring(0, e); } }
            if (path.EndsWith("/images/generations")) reply = "{\"data\":[{\"b64_json\":\"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==\"}]}";
            else if (path.EndsWith("/messages"))
                reply = afterTool ? "{\"content\":[{\"type\":\"text\",\"text\":\"All done\"}]}" : "{\"content\":[{\"type\":\"tool_use\",\"id\":\"tu1\",\"name\":\"write_file\",\"input\":{\"path\":\"a.txt\",\"content\":\"anthropic\"}}]}";
            else if (afterTool) reply = "{\"choices\":[{\"message\":{\"content\":\"All done\"}}]}";
            else
            {
                string name = "write_file", args = "{\\\"path\\\":\\\"out/hello.txt\\\",\\\"content\\\":\\\"hi there\\\"}";
                if (last.Contains("run it")) { name = "run_command"; args = "{\\\"command\\\":\\\"Write-Output zzz\\\"}"; }
                else if (last.Contains("escape")) args = "{\\\"path\\\":\\\"..\\\\\\\\evil.txt\\\",\\\"content\\\":\\\"x\\\"}";
                else if (last.Contains("draw")) { name = "generate_image"; args = "{\\\"prompt\\\":\\\"a dot\\\",\\\"path\\\":\\\"dot.png\\\"}"; }
                reply = "{\"choices\":[{\"message\":{\"content\":null,\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"" + name + "\",\"arguments\":\"" + args + "\"}}]}}]}";
            }
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(reply); c.Response.ContentType = "application/json"; c.Response.OutputStream.Write(bytes, 0, bytes.Length); c.Response.Close();
        }
    }
    public void Dispose() { try { Listener.Stop(); } catch (Exception) { } }
}

public sealed class Seen
{
    readonly List<string> items = new List<string>();
    public void Add(string s) { lock (items) items.Add(s); }
    public void Clear() { lock (items) items.Clear(); }
    public string Dump() { lock (items) return String.Join("\n   ", items.Select(x => x.Length > 220 ? x.Substring(0, 220) : x)); }
    public bool Contains(string s) { lock (items) return items.Contains(s); }
    public bool Any(Func<string, bool> p) { lock (items) return items.Any(p); }
    public string First(Func<string, bool> p) { lock (items) return items.First(p); }
    public string FirstOrDefault(Func<string, bool> p) { lock (items) return items.FirstOrDefault(p); }
}

public static class HarnessTests
{
    static int failures;
    static Dictionary<string, object>[] Dicts(object o) { return ((System.Collections.IEnumerable)o).Cast<object>().Cast<Dictionary<string, object>>().ToArray(); }
    static void Check(bool ok, string name) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
    // Stand-in for `claude --input-format stream-json --permission-prompt-tool stdio`: asks to run a tool, then reports the answer it got.
    static int Fake()
    {
        string first = Console.In.ReadLine();
        Console.WriteLine("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"fake-1\"}");
        Console.WriteLine("{\"type\":\"control_request\",\"request_id\":\"r1\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Bash\",\"input\":{\"command\":\"echo hi\"}}}");
        string resp = Console.In.ReadLine() ?? "";
        string behavior = resp.Contains("\"behavior\":\"allow\"") ? "allow" : "deny";
        Console.WriteLine("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"got " + behavior + " (" + (first ?? "").Length + ")\"}]}}");
        Console.WriteLine("{\"type\":\"result\",\"session_id\":\"fake-1\",\"is_error\":false}");
        return 0;
    }
    // Stand-in for `codex app-server`: JSON-RPC over stdio with one command-approval request per turn.
    static int FakeLeader()
    {
        string all = Console.In.ReadToEnd();
        if (all.Contains("Your teammates have finished")) Console.WriteLine("FINAL SUMMARY: reports=" + System.Text.RegularExpressions.Regex.Matches(all, "### ").Count);
        else Console.WriteLine("Here is the plan: {\"tasks\":[{\"assignee\":\"Ann\",\"title\":\"Part A\",\"prompt\":\"do A\"},{\"assignee\":\"bob\",\"title\":\"Part B\",\"prompt\":\"do B\"},{\"assignee\":\"Nobody\",\"title\":\"X\",\"prompt\":\"ignored\"}]}");
        return 0;
    }
    static int FakeLimited()
    {
        Console.In.ReadToEnd();
        Console.WriteLine("{\"type\":\"error\",\"message\":\"You have hit your usage limit. Try again in 2 hours.\"}");
        return 0;
    }
    static int FakeMember()
    {
        string all = Console.In.ReadToEnd(); Thread.Sleep(2500);
        int i = all.IndexOf("YOUR TASK:\n"); Console.WriteLine("report: finished " + (i >= 0 ? all.Substring(i + 11, 4).Trim() : "?"));
        return 0;
    }
    static void MakeZip(string path, Dictionary<string, string> parts)
    {
        using (var z = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
            foreach (var kv in parts) { var e = z.CreateEntry(kv.Key); using (var w = new StreamWriter(e.Open(), new System.Text.UTF8Encoding(false))) w.Write(kv.Value); }
    }
    static int FakeCodex()
    {
        string line; string decision = "none";
        while ((line = Console.In.ReadLine()) != null)
        {
            if (line.Contains("\"method\":\"initialize\"")) Console.WriteLine("{\"id\":1,\"result\":{\"userAgent\":\"fake\"}}");
            else if (line.Contains("\"method\":\"thread/start\"") || line.Contains("\"method\":\"thread/resume\"")) Console.WriteLine("{\"id\":2,\"result\":{\"thread\":{\"id\":\"thr-1\"}}}");
            else if (line.Contains("\"method\":\"turn/start\""))
            {
                Console.WriteLine("{\"method\":\"item/started\",\"params\":{\"item\":{\"type\":\"commandExecution\",\"command\":\"echo hi\"}}}");
                Console.WriteLine("{\"id\":77,\"method\":\"item/commandExecution/requestApproval\",\"params\":{\"itemId\":\"i\",\"threadId\":\"thr-1\",\"turnId\":\"t\",\"startedAtMs\":1,\"command\":\"echo hi\",\"reason\":\"needs shell\"}}");
            }
            else if (line.Contains("\"id\":77"))
            {
                decision = line.Contains("\"decision\":\"decline\"") ? "decline" : line.Contains("acceptForSession") ? "acceptForSession" : "accept";
                Console.WriteLine("{\"method\":\"item/completed\",\"params\":{\"item\":{\"type\":\"agentMessage\",\"text\":\"codex got " + decision + "\"}}}");
                Console.WriteLine("{\"method\":\"turn/completed\",\"params\":{}}");
                System.Threading.Thread.Sleep(60000);   // a real app-server stays alive; LAICA must end it
            }
        }
        return 0;
    }
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--fake-claude") return Fake();
        if (args.Length > 0 && args[0] == "--fake-leader") return FakeLeader();
        if (args.Length > 0 && args[0] == "--fake-member") return FakeMember();
        if (args.Length > 0 && args[0] == "--fake-limited") return FakeLimited();
        if (args.Length > 0 && args[0] == "--fake-codex") return FakeCodex();
        string root = Path.Combine(Path.GetTempPath(), "laica-harness-test-" + Guid.NewGuid().ToString("N")), work = Path.Combine(root, "work");
        Directory.CreateDirectory(work); File.WriteAllText(Path.Combine(work, "a.txt"), "hello"); Directory.CreateDirectory(Path.Combine(work, "sub"));
        string outside = Path.Combine(root, "secret.txt"); File.WriteAllText(outside, "nope");
        try
        {
            Check(HarnessManager.CronMatches("*/15 9-17 * * 1-5", new DateTime(2026, 10, 5, 9, 30, 0)), "cron matches weekday 09:30");
            Check(!HarnessManager.CronMatches("*/15 9-17 * * 1-5", new DateTime(2026, 10, 4, 9, 30, 0)), "cron rejects Sunday");
            Check(!HarnessManager.CronMatches("0 9 * * *", new DateTime(2026, 10, 5, 9, 1, 0)), "cron rejects wrong minute");
            bool bad = false; try { HarnessManager.CronMatches("nonsense", DateTime.Now); } catch (ArgumentException) { bad = true; }
            Check(bad, "cron rejects malformed expression");

            string sid;
            using (var m = new HarnessManager(root))
            {
                m.SaveAgent(new Dictionary<string, object> { { "Name", "Echo" }, { "Command", "cmd.exe" }, { "Args", "/c more" } });
                var echo = m.Harnesses().FirstOrDefault(h => h.Name == "Echo");
                Check(echo != null && echo.Available, "custom agent detected");
                var s = (Dictionary<string, object>)m.Create(echo.Id, work, null, "Be brief.", "x", "Test chat");
                sid = (string)s["Id"];
                var got = new Seen(); m.Event += e => { var d = (Dictionary<string, object>)e; got.Add((string)d["Kind"] + ":" + (string)d["Text"]); };
                m.Send(sid, "ping-123");
                for (int i = 0; i < 100 && !got.Contains("done:"); i++) Thread.Sleep(100);
                Check(got.Any(g => g.StartsWith("assistant:") && g.Contains("ping-123")), "custom agent streamed the prompt back");
                Check(got.Any(g => g.StartsWith("assistant:") && g.Contains("Be brief.")), "assistant rules prepended to first message");
                Check(got.Contains("done:"), "turn completes");
                var files = Dicts(m.Files(sid, ""));
                Check(files.Length == 2 && (bool)files[0]["Dir"], "file list shows folder then file");
                var doc = (Dictionary<string, object>)m.ReadFile(sid, "a.txt");
                Check((string)doc["Text"] == "hello", "file read");
                m.WriteFile(sid, "a.txt", "changed"); Check(File.ReadAllText(Path.Combine(work, "a.txt")) == "changed", "file write");
                bool blocked = false; try { m.ReadFile(sid, "..\\secret.txt"); } catch (ArgumentException) { blocked = true; }
                Check(blocked, "path traversal blocked");
                blocked = false; try { m.ReadFile(sid, outside); } catch (ArgumentException) { blocked = true; }
                Check(blocked, "absolute path outside folder blocked");
                bool tooMuch = false; try { m.Send(sid + "zz", "x"); } catch (ArgumentException) { tooMuch = true; }
                Check(tooMuch, "unknown chat rejected");
                var task = (Dictionary<string, object>)m.SaveTask(new Dictionary<string, object> { { "Name", "T" }, { "Kind", "once" }, { "Prompt", "tick-456" }, { "Harness", echo.Id }, { "Cwd", work }, { "RunAt", DateTime.Now.AddSeconds(-5).ToString("s") } });
                m.RunTask((string)task["Id"]);
                for (int i = 0; i < 100 && !got.Any(g => g.Contains("tick-456")); i++) Thread.Sleep(100);
                Check(got.Any(g => g.Contains("tick-456")), "scheduled task ran in a new chat");
            }
            if (!GitTools.Available) Console.WriteLine("SKIP worktree tests (git not installed)");
            else
            {
                string repo = Path.Combine(root, "repo"); Directory.CreateDirectory(repo);
                GitTools.Run(repo, "init -q -b main"); GitTools.Run(repo, "config user.email t@t"); GitTools.Run(repo, "config user.name t");
                File.WriteAllText(Path.Combine(repo, "app.txt"), "v1"); GitTools.Run(repo, "add -A"); GitTools.Run(repo, "commit -q -m init");
                using (var m = new HarnessManager(Path.Combine(root, "data2")))
                {
                    m.SaveAgent(new Dictionary<string, object> { { "Name", "Echo" }, { "Command", "cmd.exe" }, { "Args", "/c more" } });
                    string echoId = m.Harnesses().First(h => h.Name == "Echo").Id;
                    var info = (Dictionary<string, object>)m.GitInfo(repo);
                    Check((bool)info["IsRepo"] && (string)info["Branch"] == "main", "git repo detected");
                    Check(!(bool)((Dictionary<string, object>)m.GitInfo(root))["IsRepo"], "plain folder is not a repo");
                    var ws = (Dictionary<string, object>)m.Create(echoId, repo, null, null, null, "Feature work", true);
                    string wid = (string)ws["Id"], wcwd = (string)ws["Cwd"];
                    Check((bool)ws["Isolated"] && wcwd != repo && ((string)ws["Branch"]).StartsWith("laica/feature-work-"), "worktree chat gets its own folder and branch");
                    Check(File.Exists(Path.Combine(wcwd, "app.txt")), "worktree contains the project files");
                    File.WriteAllText(Path.Combine(wcwd, "app.txt"), "v2-from-agent"); File.WriteAllText(Path.Combine(wcwd, "new.txt"), "n");
                    Check(File.ReadAllText(Path.Combine(repo, "app.txt")) == "v1", "main checkout untouched by worktree edits");
                    var ch = (Dictionary<string, object>)m.Changes(wid);
                    Check(Dicts(ch["Files"]).Length == 2, "changes lists modified + new file");
                    Check(((string)m.Diff(wid, "app.txt")).Contains("+v2-from-agent"), "diff shows the edit");
                    bool refused = false; try { m.Merge(wid); } catch (InvalidOperationException) { refused = true; }
                    Check(refused, "merge refuses while the chat has uncommitted changes");
                    m.Commit(wid, "agent work");
                    Check((int)((Dictionary<string, object>)m.Changes(wid))["Ahead"] == 1, "commit recorded on the chat branch");
                    File.WriteAllText(Path.Combine(repo, "dirty.txt"), "x");
                    refused = false; try { m.Merge(wid); } catch (InvalidOperationException) { refused = true; }
                    Check(refused, "merge refuses when the main checkout has uncommitted work");
                    File.Delete(Path.Combine(repo, "dirty.txt"));
                    m.Merge(wid);
                    Check(File.ReadAllText(Path.Combine(repo, "app.txt")) == "v2-from-agent" && File.Exists(Path.Combine(repo, "new.txt")), "apply to main brings the changes over");
                    m.RemoveWorktree(wid, true);
                    Check(!Directory.Exists(wcwd) && !GitTools.Run(repo, "branch --list laica/*", false).Contains("laica/"), "removing the worktree cleans up folder and branch");

                    // "furniture re-arranged": changes made outside a chat are reported to the agent on return
                    var plain = (Dictionary<string, object>)m.Create(echoId, repo, null, null, null, "Drift", false); string pid = (string)plain["Id"];
                    var seen = new Seen(); m.Event += e => { var d = (Dictionary<string, object>)e; seen.Add((string)d["Kind"] + ":" + (string)d["Text"]); };
                    m.Send(pid, "first"); for (int i = 0; i < 100 && !seen.Contains("done:"); i++) Thread.Sleep(100);
                    Check(!seen.Any(x => x.StartsWith("notice:")), "no drift note on the first return when nothing changed");
                    File.WriteAllText(Path.Combine(repo, "moved.txt"), "z"); File.WriteAllText(Path.Combine(repo, "app.txt"), "edited-elsewhere");
                    seen.Clear(); m.Send(pid, "second"); for (int i = 0; i < 100 && !seen.Contains("done:"); i++) Thread.Sleep(100);
                    Check(seen.Any(x => x.StartsWith("notice:") && x.Contains("moved.txt") && x.Contains("app.txt")), "returning chat is told which files changed while away");
                    Check(seen.Any(x => x.StartsWith("assistant:") && x.Contains("changes were made outside")), "the note is also sent to the agent");
                }
            }
            {
                string fakeExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                using (var m = new HarnessManager(Path.Combine(root, "data3")))
                {
                    m.SaveAgent(new Dictionary<string, object> { { "Name", "FakeClaude" }, { "Command", fakeExe }, { "Args", "--fake-claude" }, { "Parser", "claude" } });
                    string fid = m.Harnesses().First(h => h.Name == "FakeClaude").Id;
                    string cid = (string)((Dictionary<string, object>)m.Create(fid, work, null, null, null, "Approvals"))["Id"];
                    var seen = new Seen(); m.Event += e => { var d = (Dictionary<string, object>)e; seen.Add((string)d["Kind"] + ":" + (string)d["Text"] + "|" + (string)d["Detail"]); };
                    Func<string, bool, bool, string> turn = (text, allow, always) =>
                    {
                        seen.Clear(); m.Send(cid, text);
                        for (int i = 0; i < 100 && !seen.Any(x => x.StartsWith("approval:")); i++) Thread.Sleep(50);
                        if (!seen.Any(x => x.StartsWith("approval:"))) return "NO-APPROVAL";
                        m.Approve(cid, "r1", allow, always);
                        for (int i = 0; i < 100 && !seen.Any(x => x.StartsWith("done:")); i++) Thread.Sleep(50);
                        return seen.FirstOrDefault(x => x.StartsWith("assistant:")) ?? "NO-REPLY";
                    };
                    Check(turn("one", true, false).StartsWith("assistant:got allow"), "approval prompt shown and Allow reaches the agent");
                    Check(seen.Any(x => x.StartsWith("approval:Bash") && x.Contains("echo hi")), "approval request names the tool and command");
                    Check(seen.Any(x => x.StartsWith("approval_result:allowed")), "approval result recorded");
                    Check(turn("two", false, false).StartsWith("assistant:got deny"), "Deny reaches the agent");
                    Check(turn("three", true, true).StartsWith("assistant:got allow"), "Always allow is accepted");
                    seen.Clear(); m.Send(cid, "four");
                    for (int i = 0; i < 100 && !seen.Any(x => x.StartsWith("done:")); i++) Thread.Sleep(50);
                    Check(!seen.Any(x => x.StartsWith("approval:")) && seen.Any(x => x.StartsWith("assistant:got allow")), "always-allowed tool no longer prompts");
                    bool stale = false; try { m.Approve(cid, "r1", true, false); } catch (InvalidOperationException) { stale = true; }
                    Check(stale, "answering a finished request is rejected");
                }
            }
            {
                string fakeExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                using (var m = new HarnessManager(Path.Combine(root, "data4")))
                {
                    m.SaveAgent(new Dictionary<string, object> { { "Name", "FakeCodex" }, { "Command", fakeExe }, { "Args", "--fake-codex" }, { "Parser", "codex" } });
                    string fid = m.Harnesses().First(h => h.Name == "FakeCodex").Id;
                    string cid = (string)((Dictionary<string, object>)m.Create(fid, work, "ask-first", null, null, "Codex approvals"))["Id"];
                    var seen = new Seen(); m.Event += e => { var d = (Dictionary<string, object>)e; seen.Add((string)d["Kind"] + ":" + (string)d["Text"] + "|" + (string)d["Detail"]); };
                    Func<string, bool, bool, string> turn = (text, allow, always) =>
                    {
                        seen.Clear(); m.Send(cid, text);
                        for (int i = 0; i < 100 && !seen.Any(x => x.StartsWith("approval:")); i++) Thread.Sleep(50);
                        if (!seen.Any(x => x.StartsWith("approval:"))) return "NO-APPROVAL";
                        string rid = seen.First(x => x.StartsWith("approval:")).Split('|')[1]; rid = rid.Substring(rid.IndexOf("c77"), 3);
                        m.Approve(cid, rid, allow, always);
                        for (int i = 0; i < 100 && !seen.Any(x => x.StartsWith("done:")); i++) Thread.Sleep(50);
                        return seen.FirstOrDefault(x => x.StartsWith("assistant:")) ?? "NO-REPLY";
                    };
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    Check(turn("one", true, false).StartsWith("assistant:codex got accept"), "codex: approval shown and accept sent");
                    Check(sw.ElapsedMilliseconds < 20000, "codex: finishing the turn ends the app-server promptly");
                    Check(seen.Any(x => x.StartsWith("approval:shell") && x.Contains("echo hi") && x.Contains("needs shell")), "codex: request shows command and reason");
                    Check(!seen.Any(x => x.StartsWith("error:")), "codex: ending the turn is not reported as an error");
                    Check(turn("two", false, false).StartsWith("assistant:codex got decline"), "codex: decline sent");
                    Check(turn("three", true, true).StartsWith("assistant:codex got accept"), "codex: accept-for-session request");
                    seen.Clear(); m.Send(cid, "four"); for (int i = 0; i < 100 && !seen.Any(x => x.StartsWith("done:")); i++) Thread.Sleep(50);
                    Check(!seen.Any(x => x.StartsWith("approval:")) && seen.Any(x => x.StartsWith("assistant:codex got accept")), "codex: remembered tool no longer prompts");
                }
            }
            {
                using (var api = new FakeApi())
                {
                    var svcOpenAi = new ServiceConnection { Id = "svc1", Name = "Fake", BaseUrl = "http://127.0.0.1:" + api.Port + "/v1", Provider = ServicePresets.Compatible, ProtectedKey = ApiServices.ProtectKey("sk-open") };
                    var svcAnth = new ServiceConnection { Id = "svc2", Name = "FakeA", BaseUrl = "http://127.0.0.1:" + api.Port + "/v1", Provider = ServicePresets.Anthropic, ProtectedKey = ApiServices.ProtectKey("sk-anth") };
                    string dataDir = Path.Combine(root, "data5"); string agentDir = Path.Combine(root, "agentwork"); Directory.CreateDirectory(agentDir);
                    string cid;
                    using (var m = new HarnessManager(dataDir, () => new[] { svcOpenAi, svcAnth }))
                    {
                        Check(m.Harnesses().First(h => h.Id == "laica").Available, "built-in agent is available when a service exists");
                        cid = (string)((Dictionary<string, object>)m.Create("laica", agentDir, null, null, null, "Builtin", false, "svc1", "gpt-test"))["Id"];
                        var seen = new Seen(); m.Event += e => { var d = (Dictionary<string, object>)e; seen.Add((string)d["Kind"] + ":" + (string)d["Text"] + "|" + (string)d["Detail"]); };
                        Func<string, string, bool, bool> turn = (chat, text, allow) =>
                        {
                            seen.Clear(); m.Send(chat, text);
                            for (int i = 0; i < 100 && !seen.Any(x => x.StartsWith("approval:")) && !seen.Any(x => x.StartsWith("done:")); i++) Thread.Sleep(50);
                            bool asked = seen.Any(x => x.StartsWith("approval:"));
                            if (asked) { string rid = seen.First(x => x.StartsWith("approval:")); rid = rid.Substring(rid.IndexOf("\"RequestId\":\"") + 13); rid = rid.Substring(0, rid.IndexOf('"')); m.Approve(chat, rid, allow, false); }
                            for (int i = 0; i < 200 && !seen.Any(x => x.StartsWith("done:")); i++) Thread.Sleep(50);
                            return asked;
                        };
                        Check(turn(cid, "make a file", true), "built-in: write_file asks for approval in ask-first mode");
                        if (!File.Exists(Path.Combine(agentDir, "out", "hello.txt"))) Console.WriteLine("   DEBUG events:\n   " + seen.Dump());
                        Check(File.Exists(Path.Combine(agentDir, "out", "hello.txt")) && File.ReadAllText(Path.Combine(agentDir, "out", "hello.txt")) == "hi there", "built-in: approved write creates the file");
                        Check(seen.Any(x => x.StartsWith("assistant:All done")), "built-in: final answer streamed to the chat");
                        File.Delete(Path.Combine(agentDir, "out", "hello.txt"));
                        turn(cid, "make a file", false);
                        Check(!File.Exists(Path.Combine(agentDir, "out", "hello.txt")) && seen.Any(x => x.StartsWith("tool_result:") && x.Contains("denied")), "built-in: denied write does nothing and tells the model");
                        turn(cid, "please escape", true);
                        Check(!File.Exists(Path.Combine(root, "evil.txt")) && seen.Any(x => x.StartsWith("tool_result:") && x.Contains("outside the working folder")), "built-in: paths outside the folder are refused");
                        string yolo = (string)((Dictionary<string, object>)m.Create("laica", agentDir, "yolo", null, null, "Yolo", false, "svc1", "gpt-test"))["Id"];
                        bool asked2 = turn(yolo, "run it", true);
                        if (asked2 || !seen.Any(x => x.StartsWith("tool_result:") && x.Contains("zzz"))) Console.WriteLine("   DEBUG yolo:\n   " + seen.Dump());
                        Check(!asked2 && seen.Any(x => x.StartsWith("tool_result:") && x.Contains("zzz")), "built-in: yolo runs a command with no prompt and returns its output");
                        turn(yolo, "draw something", true);
                        if (!File.Exists(Path.Combine(agentDir, "dot.png"))) Console.WriteLine("   DEBUG image:\n   " + seen.Dump());
                        Check(File.Exists(Path.Combine(agentDir, "dot.png")) && File.ReadAllBytes(Path.Combine(agentDir, "dot.png"))[1] == (byte)'P', "built-in: image generation saves a PNG");
                        string anth = (string)((Dictionary<string, object>)m.Create("laica", agentDir, "yolo", null, null, "Anth", false, "svc2", "claude-test"))["Id"];
                        turn(anth, "write via anthropic", true);
                        Check(File.Exists(Path.Combine(agentDir, "a.txt")) && seen.Any(x => x.StartsWith("assistant:All done")), "built-in: Anthropic tool loop works");
                        string hdr; lock (api.Headers) hdr = String.Join("\n", api.Headers);
                        Check(hdr.Contains("x-api-key=sk-anth;version=2023-06-01") && hdr.Contains("auth=Bearer sk-open"), "built-in: each provider gets its own auth headers");
                        bool noService = false; try { m.Create("laica", agentDir, null, null, null, "x", false, "missing", "m"); } catch (ArgumentException) { noService = true; }
                        Check(noService, "built-in: unknown service rejected");
                    }
                    using (var m2 = new HarnessManager(dataDir, () => new[] { svcOpenAi, svcAnth }))
                    {
                        var seen2 = new Seen(); m2.Event += e => { var d = (Dictionary<string, object>)e; seen2.Add((string)d["Kind"] + ":" + (string)d["Text"]); };
                        int before; lock (api.Bodies) before = api.Bodies.Count;
                        m2.Send(cid, "make a file");
                        for (int i = 0; i < 100 && !seen2.Any(x => x.StartsWith("approval:")); i++) Thread.Sleep(50);
                        m2.Stop(cid);
                        string firstBody; lock (api.Bodies) firstBody = api.Bodies.Count > before ? api.Bodies[before] : "";
                        Check(firstBody.Contains("please escape") && firstBody.Contains("make a file"), "built-in: conversation memory survives a restart");
                    }
                }
            }
            {
                string fakeExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                using (var m = new HarnessManager(Path.Combine(root, "data6")))
                {
                    m.SaveAgent(new Dictionary<string, object> { { "Name", "TLeader" }, { "Command", fakeExe }, { "Args", "--fake-leader" } });
                    m.SaveAgent(new Dictionary<string, object> { { "Name", "TMember" }, { "Command", fakeExe }, { "Args", "--fake-member" } });
                    string lid = m.Harnesses().First(h => h.Name == "TLeader").Id, mid = m.Harnesses().First(h => h.Name == "TMember").Id;
                    var team = (Dictionary<string, object>)m.SaveTeam(new Dictionary<string, object> { { "Title", "Crew" }, { "Cwd", work }, { "Leader", new Dictionary<string, object> { { "Harness", lid } } }, { "Members", new object[] { new Dictionary<string, object> { { "Name", "Ann" }, { "Role", "builder" }, { "Harness", mid } }, new Dictionary<string, object> { { "Name", "Bob" }, { "Harness", mid } } } } });
                    string tid = (string)team["Id"];
                    bool dup = false; try { m.SaveTeam(new Dictionary<string, object> { { "Title", "Bad" }, { "Cwd", work }, { "Leader", new Dictionary<string, object> { { "Harness", lid } } }, { "Members", new object[] { new Dictionary<string, object> { { "Name", "A" }, { "Harness", mid } }, new Dictionary<string, object> { { "Name", "a" }, { "Harness", mid } } } } }); } catch (ArgumentException) { dup = true; }
                    Check(dup, "team: duplicate teammate names rejected");
                    var sw = System.Diagnostics.Stopwatch.StartNew(); m.RunTeam(tid, "Build the thing");
                    Dictionary<string, object> cur = null;
                    for (int i = 0; i < 200; i++) { cur = Dicts(m.Teams()).First(x => (string)x["Id"] == tid); if (!(bool)cur["Running"]) break; Thread.Sleep(100); }
                    long ms = sw.ElapsedMilliseconds;
                    Check(!(bool)cur["Running"] && (string)cur["Phase"] == "done", "team: run finishes in the done phase");
                    var tasks = Dicts(cur["Tasks"]);
                    Check(tasks.Length == 2 && tasks.All(x => (string)x["Status"] == "done") && ((string)tasks[0]["Output"]).Contains("do A") && ((string)tasks[1]["Output"]).Contains("do B"), "team: leader's plan assigns tasks (case-insensitive names, unknown names ignored)");
                    Check(ms < 4500, "team: teammates work in parallel (" + ms + " ms)");
                    Check(((string)cur["Result"]).Contains("FINAL SUMMARY: reports=2"), "team: leader reviews both reports and answers");
                    var sess = Dicts(m.List()); Check(sess.Count(x => ((string)x["Title"]).StartsWith("Team · Crew")) == 3, "team: leader and both teammates have their own chats");
                    string leader1 = (string)cur["LeaderSessionId"]; m.RunTeam(tid, "Second goal");
                    for (int i = 0; i < 200; i++) { cur = Dicts(m.Teams()).First(x => (string)x["Id"] == tid); if (!(bool)cur["Running"]) break; Thread.Sleep(100); }
                    Check((string)cur["LeaderSessionId"] == leader1 && (string)cur["Phase"] == "done", "team: second run reuses the leader chat");
                    m.DeleteTeam(tid); Check(Dicts(m.Teams()).Length == 0, "team: deleted");
                }
            }
            {
                string cfgRoot = Path.Combine(root, "cfg"); string claudeDir = Path.Combine(cfgRoot, "claude"), codexDir = Path.Combine(cfgRoot, "codex"); Directory.CreateDirectory(claudeDir); Directory.CreateDirectory(codexDir);
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claudeDir); Environment.SetEnvironmentVariable("CODEX_HOME", codexDir);
                using (var m = new HarnessManager(Path.Combine(root, "data7")))
                {
                    m.SkillSave("claude", "my-skill", "Use when testing skills", "Do the thing carefully.");
                    var tools = (Dictionary<string, object>)m.Tools(); var sk = Dicts(tools["Skills"]);
                    Check(sk.Any(x => (string)x["Name"] == "my-skill" && (string)x["Detail"] == "Use when testing skills" && (string)x["Target"] == "claude"), "skills: saved skill is listed with its description");
                    Check(((string)m.SkillRead("claude", "my-skill")).Contains("Do the thing carefully."), "skills: instructions can be read back");
                    bool badName = false; try { m.SkillSave("claude", "..\\evil", "d", "b"); } catch (ArgumentException) { badName = true; }
                    Check(badName, "skills: unsafe names are rejected");
                    Directory.CreateDirectory(Path.Combine(claudeDir, "skills", "not-a-skill")); bool refusedDelete = false; try { m.SkillDelete("claude", "not-a-skill"); } catch (ArgumentException) { refusedDelete = true; }
                    Check(refusedDelete && Directory.Exists(Path.Combine(claudeDir, "skills", "not-a-skill")), "skills: folders without SKILL.md are never deleted");
                    m.SkillDelete("claude", "my-skill");
                    Check(!Dicts(((Dictionary<string, object>)m.Tools())["Skills"]).Any(x => (string)x["Name"] == "my-skill") && Directory.GetDirectories(Path.Combine(root, "data7", "harness", "deleted-skills")).Length == 1, "skills: delete moves the skill to a recoverable archive");
                    if (ModelCatalog.FindCodex() != null)
                    {
                        m.McpAdd("codex", "tsrv", "cmd /c echo \"hello world\"", "", "A=1");
                        Check(Dicts(((Dictionary<string, object>)m.Tools())["Mcp"]).Any(x => (string)x["Name"] == "tsrv" && (string)x["Target"] == "codex"), "mcp: Codex server added through the Codex CLI");
                        m.McpRemove("codex", "tsrv");
                        Check(!Dicts(((Dictionary<string, object>)m.Tools())["Mcp"]).Any(x => (string)x["Name"] == "tsrv"), "mcp: Codex server removed");
                    }
                    else Console.WriteLine("SKIP codex MCP tests");
                    if (HarnessManager.FindClaude() != null)
                    {
                        m.McpAdd("claude", "tsrv2", "cmd /c echo hi", "", "");
                        Check(Dicts(((Dictionary<string, object>)m.Tools())["Mcp"]).Any(x => (string)x["Name"] == "tsrv2" && (string)x["Target"] == "claude"), "mcp: Claude Code server added through the Claude CLI");
                        m.McpRemove("claude", "tsrv2");
                        Check(!Dicts(((Dictionary<string, object>)m.Tools())["Mcp"]).Any(x => (string)x["Name"] == "tsrv2"), "mcp: Claude Code server removed");
                    }
                    else Console.WriteLine("SKIP claude MCP tests");
                    bool badMcp = false; try { m.McpAdd("codex", "bad name!", "x", "", ""); } catch (ArgumentException) { badMcp = true; }
                    Check(badMcp, "mcp: unsafe names are rejected");

                    // Office + PDF previews
                    m.SaveAgent(new Dictionary<string, object> { { "Name", "OfficeEcho" }, { "Command", "cmd.exe" }, { "Args", "/c more" } });
                    string oid = (string)((Dictionary<string, object>)m.Create(m.Harnesses().First(h => h.Name == "OfficeEcho").Id, work, null, null, null, "Office"))["Id"];
                    string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
                    MakeZip(Path.Combine(work, "doc.docx"), new Dictionary<string, string> { { "word/document.xml", "<w:document " + W + "><w:body><w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>Title Here</w:t></w:r></w:p><w:p><w:r><w:t>Body text</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>a</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>b</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>" } });
                    var d1 = (Dictionary<string, object>)m.ReadFile(oid, "doc.docx");
                    Check((string)d1["Kind"] == "markdown" && ((string)d1["Text"]).Contains("# Title Here") && ((string)d1["Text"]).Contains("Body text") && ((string)d1["Text"]).Contains("a | b"), "office: Word document previews as headings, text and tables");
                    string S = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
                    MakeZip(Path.Combine(work, "book.xlsx"), new Dictionary<string, string> {
                        { "xl/workbook.xml", "<workbook " + S + "><sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>" },
                        { "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/></Relationships>" },
                        { "xl/sharedStrings.xml", "<sst " + S + "><si><t>Hello</t></si></sst>" },
                        { "xl/worksheets/sheet1.xml", "<worksheet " + S + "><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"C1\"><v>42</v></c></row><row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>inline</t></is></c></row></sheetData></worksheet>" } });
                    var d2 = (Dictionary<string, object>)m.ReadFile(oid, "book.xlsx"); var sheet = Dicts(d2["Sheets"])[0]; var rows = ((System.Collections.IEnumerable)sheet["Rows"]).Cast<string[]>().ToArray();
                    Check((string)d2["Kind"] == "sheet" && (string)sheet["Name"] == "Data" && rows[0][0] == "Hello" && rows[0][2] == "42" && rows[1][0] == "inline", "office: spreadsheet previews cells, shared strings and gaps");
                    string A = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
                    MakeZip(Path.Combine(work, "deck.pptx"), new Dictionary<string, string> { { "ppt/slides/slide1.xml", "<p:sld xmlns:p=\"urn:p\" " + A + "><a:p><a:r><a:t>Deck Title</a:t></a:r></a:p><a:p><a:r><a:t>Point one</a:t></a:r></a:p></p:sld>" } });
                    var d3 = (Dictionary<string, object>)m.ReadFile(oid, "deck.pptx");
                    Check(((string)d3["Text"]).Contains("## Slide 1") && ((string)d3["Text"]).Contains("**Deck Title**") && ((string)d3["Text"]).Contains("- Point one"), "office: presentation previews slide titles and bullets");
                    File.WriteAllText(Path.Combine(work, "x.pdf"), "%PDF-1.4 test"); var d4 = (Dictionary<string, object>)m.ReadFile(oid, "x.pdf");
                    Check((string)d4["Kind"] == "pdf" && ((string)d4["DataUri"]).StartsWith("data:application/pdf;base64,"), "office: PDF is returned for the viewer");
                    File.WriteAllText(Path.Combine(work, "broken.docx"), "not a zip"); Check((string)((Dictionary<string, object>)m.ReadFile(oid, "broken.docx"))["Kind"] == "binary", "office: a corrupt document degrades gracefully");
                }
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null); Environment.SetEnvironmentVariable("CODEX_HOME", null);
            }
            {
                string hroot = Path.Combine(root, "hist"), claudeDir = Path.Combine(hroot, "claude"), codexDir = Path.Combine(hroot, "codex"), proj = Path.Combine(hroot, "myproject"); Directory.CreateDirectory(proj);
                Directory.CreateDirectory(Path.Combine(claudeDir, "projects", "p1")); Directory.CreateDirectory(Path.Combine(codexDir, "sessions", "2026", "10", "06"));
                string esc = proj.Replace("\\", "\\\\"), gone = Path.Combine(hroot, "deleted-folder").Replace("\\", "\\\\");
                File.WriteAllText(Path.Combine(claudeDir, "projects", "p1", "aaaaaaaa-1111.jsonl"),
                    "{\"type\":\"queue-operation\"}\n{\"type\":\"user\",\"cwd\":\"" + esc + "\",\"timestamp\":\"2026-10-01T00:00:00Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"<system-reminder>ignore</system-reminder>\"},{\"type\":\"text\",\"text\":\"Fix the login bug please\"}]}}\n" +
                    "{\"type\":\"assistant\",\"timestamp\":\"2026-10-01T00:00:05Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Looking now.\"},{\"type\":\"tool_use\",\"name\":\"Read\",\"input\":{\"file_path\":\"a.cs\"}}]}}\n" +
                    "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"content\":\"file text\"}]}}\n{\"type\":\"assistant\",\"isSidechain\":true,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"SIDECHAIN\"}]}}\n");
                string cx = Path.Combine(codexDir, "sessions", "2026", "10", "06");
                File.WriteAllText(Path.Combine(cx, "rollout-2026-10-06T10-00-00-main.jsonl"),
                    "{\"timestamp\":\"2026-10-06T10:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"cx-main-1\",\"cwd\":\"\\\\\\\\?\\\\" + esc + "\"}}\n" +
                    "{\"timestamp\":\"2026-10-06T10:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"# AGENTS.md instructions\\nblah\"}]}}\n" +
                    "{\"timestamp\":\"2026-10-06T10:00:02Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"Add a dark mode toggle\"}]}}\n" +
                    "{\"timestamp\":\"2026-10-06T10:00:03Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"On it.\"}]}}\n" +
                    "{\"timestamp\":\"2026-10-06T10:00:04Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"name\":\"shell\",\"input\":\"ls\"}}\n{\"timestamp\":\"2026-10-06T10:00:05Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call_output\",\"output\":\"a.cs\"}}\n");
                File.WriteAllText(Path.Combine(cx, "rollout-2026-10-06T10-05-00-sub.jsonl"), "{\"timestamp\":\"2026-10-06T10:05:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"cx-sub-1\",\"parent_thread_id\":\"cx-main-1\",\"cwd\":\"" + esc + "\"}}\n");
                File.WriteAllText(Path.Combine(cx, "rollout-2026-10-06T10-10-00-gone.jsonl"), "{\"timestamp\":\"2026-10-06T10:10:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"cx-gone-1\",\"cwd\":\"" + gone + "\"}}\n{\"timestamp\":\"2026-10-06T10:10:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"Old chat in a deleted folder\"}]}}\n");
                // Codex's own metadata: thread names, project names/order and thread->project assignments; Claude Code's custom title
                File.WriteAllText(Path.Combine(codexDir, "session_index.jsonl"), "{\"id\":\"cx-gone-1\",\"thread_name\":\"Renamed in Codex\",\"updated_at\":\"2026-10-06T10:20:00Z\"}\n");
                File.WriteAllText(Path.Combine(codexDir, ".codex-global-state.json"), "{\"local-projects\":{\"p1\":{\"id\":\"p1\",\"name\":\"My Project\",\"rootPaths\":[\"" + esc + "\"]},\"p2\":{\"id\":\"p2\",\"name\":\"Second\",\"rootPaths\":[\"" + gone + "\"]}},\"project-order\":[\"p2\",\"p1\"],\"thread-project-assignments\":{\"cx-gone-1\":{\"projectKind\":\"local\",\"projectId\":\"p1\"}}}");
                File.AppendAllText(Path.Combine(claudeDir, "projects", "p1", "aaaaaaaa-1111.jsonl"), "{\"type\":\"custom-title\",\"customTitle\":\"Login fix session\",\"sessionId\":\"aaaaaaaa-1111\"}\n");
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", claudeDir); Environment.SetEnvironmentVariable("CODEX_HOME", codexDir);
                using (var m = new HarnessManager(Path.Combine(root, "data8")))
                using (var locker = new FileStream(Path.Combine(cx, "rollout-2026-10-06T10-00-00-main.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                {
                    var list = Dicts(m.ImportList(true));
                    Check(list.Length == 3, "history: finds Claude and Codex conversations, skipping sub-agent threads (" + list.Length + ")");
                    var c = list.FirstOrDefault(x => (string)x["ExternalId"] == "aaaaaaaa-1111"); var k = list.FirstOrDefault(x => (string)x["ExternalId"] == "cx-main-1");
                    Check(c != null && (string)c["Title"] == "Login fix session" && (string)c["Project"] == proj && (string)c["Source"] == "claude", "history: Claude Code's own custom title is used and the project comes from the session");
                    Check(k != null && (string)k["Title"] == "Add a dark mode toggle" && (string)k["Project"] == proj, "history: Codex title skips AGENTS.md context and the \\\\?\\ prefix is cleaned (file was locked by another writer)");
                    var s1 = (Dictionary<string, object>)m.ImportOpen("claude", "aaaaaaaa-1111", work); string sid1 = (string)s1["Id"];
                    var ev = Dicts(m.History(sid1)); var kinds = String.Join(",", ev.Select(e => (string)e["Kind"]));
                    Check(kinds == "log,user,assistant,tool,tool_result", "history: Claude transcript replays user, assistant, tool and result in order, without sidechains (" + kinds + ")");
                    Check((string)s1["Harness"] == "claude" && (string)s1["Cwd"] == proj, "history: opened chat uses the original folder and the claude agent");
                    var s2 = (Dictionary<string, object>)m.ImportOpen("claude", "aaaaaaaa-1111", work); Check((string)s2["Id"] == sid1, "history: opening twice reuses the same chat");
                    var s3 = (Dictionary<string, object>)m.ImportOpen("codex", "cx-main-1", work); var kinds3 = String.Join(",", Dicts(m.History((string)s3["Id"])).Select(e => (string)e["Kind"]));
                    Check(kinds3 == "log,user,assistant,tool,tool_result", "history: Codex transcript replays correctly (" + kinds3 + ")");
                    var s4 = (Dictionary<string, object>)m.ImportOpen("codex", "cx-gone-1", work); Check((string)s4["Cwd"] == proj && (string)s4["Title"] == "Renamed in Codex", "history: Codex thread names are used, and a deleted folder falls back to the project's real root");
                    var cxGone = list.First(x => (string)x["ExternalId"] == "cx-gone-1"); Check((string)cxGone["ProjectName"] == "My Project" && (string)cxGone["Title"] == "Renamed in Codex", "history: threads are filed under the project Codex assigned them to");
                    Check((string)k["ProjectName"] == "My Project", "history: threads without an assignment are matched to a project by their folder");
                    var cxp = Dicts(m.CodexProjects()); Check(cxp.Length == 2 && (string)cxp[0]["Name"] == "Second" && (string)cxp[1]["Name"] == "My Project" && (string)cxp[1]["Path"] == proj, "history: projects keep the names and order shown in Codex");
                    Check(Dicts(m.ImportList(false)).Count(x => (string)x["SessionId"] != "") == 3, "history: list marks conversations that are already open");
                    bool badOpen = false; try { m.ImportOpen("codex", "nope", work); } catch (ArgumentException) { badOpen = true; } Check(badOpen, "history: unknown conversation rejected");
                }
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null); Environment.SetEnvironmentVariable("CODEX_HOME", null);
                using (var m = new HarnessManager(Path.Combine(root, "data9")))
                {
                    m.SaveAgent(new Dictionary<string, object> { { "Name", "NodeEcho" }, { "Command", "cmd.exe" }, { "Args", "/c more" } }); string eid = m.Harnesses().First(h => h.Name == "NodeEcho").Id;
                    var models = m.CliModels(); Check(models.Any(x => x.ConnectionId == "cli:" + eid && x.Id == "default") && !models.Any(x => x.ConnectionId == "cli:codex" || x.ConnectionId == "cli:laica"), "designer: installed agents appear as services with models, Codex and LAICA Agent excluded");
                    Check(HarnessManager.FindClaude() == null || models.Count(x => x.ConnectionId == "cli:claude") == 4, "designer: Claude Code offers default, opus, sonnet and haiku");
                    Check(HarnessManager.FindClaude() == null || models.Where(x => x.ConnectionId == "cli:claude").All(x => x.Efforts.SequenceEqual(new[] { "default", "low", "medium", "high", "xhigh", "max" })), "designer: Claude Code nodes can choose a reasoning effort");
                    string answer = m.RunOnceAsync(eid, "default", "node prompt 42", work, System.Threading.CancellationToken.None).Result;
                    Check(answer.Contains("node prompt 42"), "designer: a node runs through the agent and returns its text");
                    bool badModel = false; try { m.RunOnceAsync(eid, "x; calc", "p", work, System.Threading.CancellationToken.None).Wait(); } catch (AggregateException ex) { badModel = ex.InnerException is ArgumentException; } Check(badModel, "designer: model names cannot inject arguments");
                    var cts = new System.Threading.CancellationTokenSource(); m.SaveAgent(new Dictionary<string, object> { { "Name", "Sleeper" }, { "Command", "cmd.exe" }, { "Args", "/c ping -n 30 127.0.0.1" } }); string slp = m.Harnesses().First(h => h.Name == "Sleeper").Id;
                    var run = m.RunOnceAsync(slp, "default", "x", work, cts.Token); Thread.Sleep(600); cts.Cancel(); bool cancelled = false; try { run.Wait(8000); } catch (AggregateException ex) { cancelled = ex.InnerException is OperationCanceledException; } Check(cancelled, "designer: stopping a run cancels the agent process");
                }
            }
            {
                using (var m = new HarnessManager(Path.Combine(root, "data10")))
                {
                    Check(!m.Harnesses().First(h => h.Id == "workflow").Available, "workflow: unavailable until the host supplies a runner");
                    m.WorkflowRunner = (profile, goal, folder, token, progress) => { progress("Supervisor — planning"); if (goal.Contains("explode")) throw new ArgumentException("Model unavailable for Worker: x"); return System.Threading.Tasks.Task.FromResult("answer for " + goal + " via " + profile); };
                    Check(m.Harnesses().First(h => h.Id == "workflow").Available, "workflow: available once a runner exists");
                    bool noProfile = false; try { m.Create("workflow", work, null, null, null, "WF", false, "", ""); } catch (ArgumentException) { noProfile = true; } Check(noProfile, "workflow: a profile is required");
                    string wid = (string)((Dictionary<string, object>)m.Create("workflow", work, null, null, null, "WF", false, "profile-1", "My team"))["Id"];
                    var seen = new Seen(); m.Event += e => { var d = (Dictionary<string, object>)e; seen.Add((string)d["Kind"] + ":" + (string)d["Text"]); };
                    m.Send(wid, "hello wf"); for (int i = 0; i < 100 && !seen.Contains("done:"); i++) Thread.Sleep(50);
                    Check(seen.Contains("log:Supervisor — planning") && seen.Contains("assistant:answer for hello wf via profile-1"), "workflow: progress lines and the team's answer appear in the chat");
                    seen.Clear(); m.Send(wid, "explode please"); for (int i = 0; i < 100 && !seen.Contains("done:"); i++) Thread.Sleep(50);
                    Check(seen.Any(x => x.StartsWith("error:") && x.Contains("refresh models")), "workflow: model errors tell the user how to fix them");
                }
            }
            {
                using (var m = new HarnessManager(Path.Combine(root, "data11")))
                {
                    string c1 = m.ArgumentPreview("claude", "acceptEdits", "opus", "high", false);
                    Check(c1.Contains("--model opus") && c1.Contains("--effort high") && c1.Contains("--permission-mode acceptEdits"), "effort: Claude Code gets --model and --effort");
                    string c2 = m.ArgumentPreview("claude", "default", "default", "", false);
                    Check(!c2.Contains("--model") && !c2.Contains("--effort"), "effort: no flags when the defaults are chosen");
                    string c3 = m.ArgumentPreview("claude", "default", "x; calc", "high; calc", false);
                    Check(!c3.Contains("calc"), "effort: unsafe model and effort values are dropped");
                    string x1 = m.ArgumentPreview("codex", "workspace-write", "gpt-6-luna", "xhigh", false);
                    Check(x1.Contains("-m gpt-6-luna") && x1.Contains("-c model_reasoning_effort=xhigh") && x1.Contains("-s workspace-write"), "effort: Codex gets -m and a reasoning-effort override");
                    string x2 = m.ArgumentPreview("codex", "workspace-write", "gpt-6-luna", "xhigh", true);
                    Check(x2.StartsWith("exec resume prior-session") && !x2.Contains("model_reasoning_effort"), "effort: resumed Codex chats keep their own thread settings");
                }
            }
            {
                string fakeExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                using (var m = new HarnessManager(Path.Combine(root, "data-usage")))
                {
                    m.SaveAgent(new Dictionary<string, object> { { "Name", "ULeader" }, { "Command", fakeExe }, { "Args", "--fake-leader" } });
                    m.SaveAgent(new Dictionary<string, object> { { "Name", "UMember" }, { "Command", fakeExe }, { "Args", "--fake-member" } });
                    m.SaveAgent(new Dictionary<string, object> { { "Name", "ULimited" }, { "Command", fakeExe }, { "Args", "--fake-limited" }, { "Parser", "codex" } });
                    string lid = m.Harnesses().First(h => h.Name == "ULeader").Id, mid = m.Harnesses().First(h => h.Name == "UMember").Id, xid = m.Harnesses().First(h => h.Name == "ULimited").Id;
                    Check(HarnessManager.IsLimitText("You have hit your usage limit") && HarnessManager.IsLimitText("429 Too Many Requests") && HarnessManager.IsLimitText("Your credit balance is too low") && !HarnessManager.IsLimitText("Build failed: syntax error"), "usage: limit messages are recognised, ordinary errors are not");
                    // a single chat that hits the limit
                    var chat = (Dictionary<string, object>)m.Create(xid, work, null, null, null, "limited chat"); string cid = (string)chat["Id"];
                    m.Send(cid, "hello there");
                    for (int i = 0; i < 100 && Dicts(m.List()).First(x => (string)x["Id"] == cid)["Busy"].Equals(true); i++) Thread.Sleep(100);
                    var hist = Dicts(m.History(cid));
                    Check(hist.Any(h => (string)h["Kind"] == "limit" && (string)h["Detail"] == xid), "usage: a limit error adds a limit event for the vendor");
                    Check(m.IsLimited(xid), "usage: the vendor is parked after a limit error");
                    var row = Dicts(m.Usage()).FirstOrDefault(x => (string)x["Key"] == xid);
                    Check(row != null && (bool)row["Limited"] && (string)row["Warn"] == "limited" && (int)row["TurnsToday"] == 0, "usage: usage report shows the vendor limited with no successful turns");
                    // continue on another vendor
                    var moved = (Dictionary<string, object>)m.ContinueElsewhere(cid, mid, "", "");
                    Check(((string)moved["Title"]).StartsWith("Continued:") && (string)moved["Harness"] == mid, "usage: continue elsewhere starts a new chat on the other vendor");
                    string movedId = (string)moved["Id"];
                    for (int i = 0; i < 100 && Dicts(m.List()).First(x => (string)x["Id"] == movedId)["Busy"].Equals(true); i++) Thread.Sleep(100);
                    var mh = Dicts(m.History(movedId));
                    Check(mh.Any(h => (string)h["Kind"] == "user" && ((string)h["Text"]).Contains("TRANSCRIPT SO FAR") && ((string)h["Text"]).Contains("hello there")), "usage: the new chat receives the transcript and the last request");
                    var mrow = Dicts(m.Usage()).First(x => (string)x["Key"] == mid);
                    Check((int)mrow["TurnsToday"] >= 1 && !(bool)mrow["Limited"], "usage: a finished turn counts and the vendor stays available");
                    // budget warning
                    m.SetUsageBudget(mid, 1000);
                    Check((long)Dicts(m.Usage()).First(x => (string)x["Key"] == mid)["Budget"] == 1000, "usage: daily budget is saved");
                    m.ClearUsageLimit(xid); Check(!m.IsLimited(xid), "usage: a limit can be cleared by hand");
                    // a team whose teammate's vendor runs out is moved to another vendor without failing
                    var team = (Dictionary<string, object>)m.SaveTeam(new Dictionary<string, object> { { "Title", "Mixed" }, { "Cwd", work }, { "Leader", new Dictionary<string, object> { { "Harness", lid } } }, { "Members", new object[] { new Dictionary<string, object> { { "Name", "Ann" }, { "Role", "builder" }, { "Harness", xid } }, new Dictionary<string, object> { { "Name", "bob" }, { "Role", "checker" }, { "Harness", mid } } } } });
                    string tid = (string)team["Id"]; m.RunTeam(tid, "Do it");
                    Dictionary<string, object> cur = null;
                    for (int i = 0; i < 300; i++) { cur = Dicts(m.Teams()).First(x => (string)x["Id"] == tid); if (!(bool)cur["Running"]) break; Thread.Sleep(100); }
                    Check((string)cur["Phase"] == "done", "usage: the team finishes even though one vendor ran out (phase " + cur["Phase"] + ")");
                    var tasks = Dicts(cur["Tasks"]); var ann = tasks.FirstOrDefault(x => (string)x["Assignee"] == "Ann");
                    Check(ann != null && (string)ann["Status"] == "done" && ann.ContainsKey("Note") && ((string)ann["Note"]).Contains("Switched from"), "usage: the limited teammate's task moved to another agent and is noted");
                    Check(tasks.All(x => (string)x["Status"] == "done"), "usage: every task completes");
                    // with the vendor still parked, the next run starts the task on a vendor that has usage
                    m.RunTeam(tid, "Again");
                    for (int i = 0; i < 300; i++) { cur = Dicts(m.Teams()).First(x => (string)x["Id"] == tid); if (!(bool)cur["Running"]) break; Thread.Sleep(100); }
                    var ann2 = Dicts(cur["Tasks"]).FirstOrDefault(x => (string)x["Assignee"] == "Ann");
                    Check((string)cur["Phase"] == "done" && ann2 != null && ann2.ContainsKey("Note") && ((string)ann2["Note"]).Contains("out of usage"), "usage: a parked vendor is skipped up front on the next run");
                    // when nothing has usage left the team fails cleanly
                    m.SetUsageBudget(lid, 0);
                }
                using (var m = new HarnessManager(Path.Combine(root, "data-usage")))
                    Check(Dicts(m.Usage()).Any(x => (int)x["Turns"] > 0), "usage: totals survive a restart");
            }
            {
                string fakeExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                using (var m = new HarnessManager(Path.Combine(root, "data-pause")))
                {
                    m.SaveAgent(new Dictionary<string, object> { { "Name", "PMember" }, { "Command", fakeExe }, { "Args", "--fake-member" } });
                    string pid = m.Harnesses().First(h => h.Name == "PMember").Id;
                    var chat = (Dictionary<string, object>)m.Create(pid, work, null, null, null, "pause chat"); string cid = (string)chat["Id"];
                    bool idleRefused = false; try { m.Pause(cid); } catch (InvalidOperationException) { idleRefused = true; }
                    Check(idleRefused, "pause: an idle chat can't be paused");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    m.Send(cid, "YOUR TASK:\nwork"); Thread.Sleep(500);
                    m.Pause(cid);
                    Check((bool)Dicts(m.List()).First(x => (string)x["Id"] == cid)["Paused"], "pause: the chat reports it is paused");
                    Thread.Sleep(3200);   // the agent would have finished by itself after 2.5 s if it were still running
                    var during = Dicts(m.List()).First(x => (string)x["Id"] == cid);
                    Check((bool)during["Busy"] && (bool)during["Paused"], "pause: a paused agent is frozen, not finished (" + sw.ElapsedMilliseconds + " ms in)");
                    m.Resume(cid);
                    for (int i = 0; i < 80 && (bool)Dicts(m.List()).First(x => (string)x["Id"] == cid)["Busy"]; i++) Thread.Sleep(100);
                    var after = Dicts(m.List()).First(x => (string)x["Id"] == cid);
                    Check(!(bool)after["Busy"] && !(bool)after["Paused"], "pause: resuming lets the agent finish");
                    var hist = Dicts(m.History(cid));
                    Check(hist.Any(h => (string)h["Kind"] == "paused") && hist.Any(h => (string)h["Kind"] == "resumed") && hist.Any(h => ((string)h["Text"]).Contains("report: finished work")), "pause: the history records pause, resume and the finished work");
                    // stopping a paused chat ends it cleanly
                    m.Send(cid, "YOUR TASK:\nmore"); Thread.Sleep(400); m.Pause(cid); m.Stop(cid);
                    for (int i = 0; i < 60 && (bool)Dicts(m.List()).First(x => (string)x["Id"] == cid)["Busy"]; i++) Thread.Sleep(100);
                    var stopped = Dicts(m.List()).First(x => (string)x["Id"] == cid);
                    Check(!(bool)stopped["Busy"] && !(bool)stopped["Paused"], "pause: stopping a paused chat ends it and clears the pause");
                }
            }
            {
                using (var m = new HarnessManager(Path.Combine(root, "data-attach")))
                {
                    string proj = Path.Combine(root, "attach-proj"); Directory.CreateDirectory(proj);
                    string outsideFile = Path.Combine(root, "outside-note.txt"); File.WriteAllText(outsideFile, "from outside");
                    string inside = Path.Combine(proj, "inside.txt"); File.WriteAllText(inside, "already here");
                    var chat = (Dictionary<string, object>)m.Create("codex", proj, null, null, null, "attach chat"); string cid = (string)chat["Id"];
                    var got = Dicts(m.AttachStage(cid, null, new[] { outsideFile, inside }));
                    Check(got.Length == 2 && (bool)got[0]["Copied"] && ((string)got[0]["Path"]).Contains(Path.Combine(".laica", "attachments")) && File.ReadAllText((string)got[0]["Path"]) == "from outside", "attach: a file from outside the project is copied into .laica/attachments");
                    Check(!(bool)got[1]["Copied"] && (string)got[1]["Path"] == inside, "attach: a file already in the project is referenced in place");
                    var again = Dicts(m.AttachStage(cid, null, new[] { outsideFile }));
                    Check((string)again[0]["Path"] != (string)got[0]["Path"] && File.Exists((string)again[0]["Path"]), "attach: attaching the same name twice keeps both files");
                    bool missing = false; try { m.AttachStage(cid, null, new[] { Path.Combine(root, "nope.bin") }); } catch (ArgumentException) { missing = true; }
                    Check(missing, "attach: a missing file is refused");
                    var evil = (Dictionary<string, object>)m.AttachSave(cid, null, "..\\..\\evil.txt", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("AAAA")), null, false);
                    string evilPath = (string)evil["Path"];
                    Check(Path.GetFileName(evilPath) == "evil.txt" && evilPath.StartsWith(Path.Combine(proj, ".laica", "attachments")), "attach: a hostile file name can't escape the attachments folder");
                    var more = (Dictionary<string, object>)m.AttachSave(cid, null, "evil.txt", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("BBBB")), evilPath, true);
                    Check(File.ReadAllText(evilPath) == "AAAABBBB" && (long)more["Size"] == 8, "attach: a file arrives in pieces and is reassembled");
                    bool refused = false; try { m.AttachSave(cid, null, "x", Convert.ToBase64String(new byte[] { 1 }), inside, true); } catch (ArgumentException) { refused = true; }
                    Check(refused && File.ReadAllText(inside) == "already here", "attach: pieces can't be appended to a file outside the attachments folder");
                    string md = m.ExportMarkdown(cid);
                    Check(md.StartsWith("# attach chat"), "export: the chat exports as Markdown with its title");
                }
            }
            {
                string cfg = Path.Combine(root, "cfg-plugins"); Directory.CreateDirectory(cfg);
                string prevClaude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"); Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", cfg);
                try
                {
                    using (var m = new HarnessManager(Path.Combine(root, "data-plugins")))
                    {
                        var lib = (Dictionary<string, object>)m.PluginLibrary(); var items = Dicts(lib["Items"]);
                        Check(items.Length >= 20 && items.Any(x => (string)x["Id"] == "filesystem") && items.Any(x => (string)x["Id"] == "agent-gemini") && items.Any(x => (string)x["Id"] == "skill-code-review"), "plugins: the library lists MCP servers, agents and skills");
                        Check(items.All(x => !String.IsNullOrEmpty((string)x["Summary"]) && !String.IsNullOrEmpty((string)x["Preview"])), "plugins: every entry explains itself and previews what installing does");
                        Check(items.Where(x => (bool)x["Suggested"]).All(x => ((string)x["Because"]).EndsWith("is installed") || ((string)x["Kind"] == "runtime" && ((string)x["Because"]).Length > 0)), "plugins: a suggestion always says which program it is for");
                        bool unknown = false; try { m.PluginInstall("nope", new[] { "claude" }, null); } catch (ArgumentException) { unknown = true; }
                        Check(unknown, "plugins: an unknown plugin is refused");
                        bool noFolder = false; try { m.PluginInstall("filesystem", new[] { "claude" }, new Dictionary<string, string>()); } catch (Exception ex) { noFolder = ex is ArgumentException || ex is InvalidOperationException; }
                        Check(noFolder, "plugins: a missing required field (or missing Node.js) stops the install");
                        bool noTarget = false; try { m.PluginInstall("skill-code-review", new string[0], null); } catch (ArgumentException) { noTarget = true; }
                        Check(noTarget, "plugins: you must choose where to install");
                        m.PluginInstall("skill-code-review", new[] { "claude" }, null);
                        string skillFile = Path.Combine(cfg, "skills", "code-review", "SKILL.md");
                        Check(File.Exists(skillFile) && File.ReadAllText(skillFile).Contains("Correctness"), "plugins: a skill installs as a SKILL.md");
                        var after = Dicts(((Dictionary<string, object>)m.PluginLibrary())["Items"]).First(x => (string)x["Id"] == "skill-code-review");
                        Check(((string[])after["InstalledOn"]).Contains("claude"), "plugins: the library shows the skill as installed");
                        m.PluginRemove("skill-code-review", "claude");
                        Check(!File.Exists(skillFile), "plugins: a skill can be removed again");
                    }
                }
                finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", prevClaude); }
            }
            {
                string b = Path.Combine(root, "desk"); string cl = Path.Combine(b, "claude"), cx = Path.Combine(b, "codex"), ds = Path.Combine(b, "desktop"); string proj = Path.Combine(b, "work", "RealProject"), scratch = Path.Combine(b, "AppData", "Claude", "scratch-workspaces", "aaa", "bbb", "scratch-2026-10-06-abc123");
                foreach (string d in new[] { Path.Combine(cl, "projects", "p1"), cx, ds, proj, scratch }) Directory.CreateDirectory(d);
                string idA = "aaaaaaaa-0000-4000-8000-000000000001", idB = "bbbbbbbb-0000-4000-8000-000000000002";
                Func<string, string, string> rec = (cwd, text) => "{\"type\":\"user\",\"isSidechain\":false,\"cwd\":" + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(cwd) + ",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"<system-reminder>boilerplate</system-reminder>\"},{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";
                File.WriteAllText(Path.Combine(cl, "projects", "p1", idA + ".jsonl"), rec(scratch, "first words of chat A") + "\n");
                File.WriteAllText(Path.Combine(cl, "projects", "p1", idB + ".jsonl"), rec(scratch, "first words of chat B") + "\n");
                File.WriteAllText(Path.Combine(ds, "local_x.json"), "{\"sessionId\":\"local_x\",\"cliSessionId\":\"" + idA + "\",\"cwd\":" + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(proj) + ",\"title\":\"Named by the desktop app\",\"isArchived\":false,\"lastActivityAt\":1791323557081,\"remoteMcpServersConfig\":[{\"tools\":[\"x\"]}]}");
                string pc = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), px = Environment.GetEnvironmentVariable("CODEX_HOME"), pd = Environment.GetEnvironmentVariable("LAICA_CLAUDE_DESKTOP_DIR");
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", cl); Environment.SetEnvironmentVariable("CODEX_HOME", cx); Environment.SetEnvironmentVariable("LAICA_CLAUDE_DESKTOP_DIR", ds);
                try
                {
                    using (var m = new HarnessManager(Path.Combine(root, "data-desk")))
                    {
                        var h = Dicts(m.ImportList(true)); var a = h.FirstOrDefault(x => (string)x["ExternalId"] == idA); var bb = h.FirstOrDefault(x => (string)x["ExternalId"] == idB);
                        Check(a != null && (string)a["Title"] == "Named by the desktop app", "claude desktop: a chat takes the name the desktop app gave it");
                        Check(a != null && (string)a["Project"] == proj && (string)a["ProjectName"] == "RealProject", "claude desktop: a chat appears under the project folder the desktop app recorded");
                        Check(bb != null && ((string)bb["Title"]).StartsWith("first words of chat B") && (string)bb["ProjectName"] == "", "claude desktop: a chat from a scratch folder with no metadata lands in Chats, not a fake project");
                    }
                }
                finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", pc); Environment.SetEnvironmentVariable("CODEX_HOME", px); Environment.SetEnvironmentVariable("LAICA_CLAUDE_DESKTOP_DIR", pd); }
            }
            {
                string trashDir = Path.Combine(root, "data-trash");
                using (var m = new HarnessManager(trashDir))
                {
                    var c = (Dictionary<string, object>)m.Create("codex", work, null, null, null, "keep me"); string cid = (string)c["Id"];
                    m.Close(cid);
                    Check(!Dicts(m.List()).Any(x => (string)x["Id"] == cid), "undo: a closed chat leaves the list");
                    Check(File.Exists(Path.Combine(trashDir, "harness", "trash", cid + ".json")), "undo: a closed chat waits in the trash instead of being deleted");
                    var back = (Dictionary<string, object>)m.RestoreClosed(cid);
                    Check((string)back["Title"] == "keep me" && Dicts(m.List()).Any(x => (string)x["Id"] == cid), "undo: restoring brings the chat back as it was");
                    bool gone = false; try { m.RestoreClosed("not-a-chat"); } catch (ArgumentException) { gone = true; }
                    Check(gone, "undo: restoring something that was never closed is refused");
                    m.Close(cid); string old = Path.Combine(trashDir, "harness", "trash", cid + ".json"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
                }
                using (var m = new HarnessManager(trashDir)) Check(!File.Exists(Path.Combine(trashDir, "harness", "trash", Directory.GetFiles(Path.Combine(trashDir, "harness", "sessions"), "*.json").Length.ToString() + "x.json")) && Directory.GetFiles(Path.Combine(trashDir, "harness", "trash")).Length == 0, "undo: chats closed more than two weeks ago are cleared");
            }
            {
                string b = Path.Combine(root, "plan"); string cx = Path.Combine(b, "codex"), cl = Path.Combine(b, "claude");
                Directory.CreateDirectory(Path.Combine(cx, "sessions", "2026", "10", "07")); Directory.CreateDirectory(Path.Combine(cl, "projects", "p"));
                long nowSec = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; string iso = DateTime.UtcNow.ToString("o");
                Func<long, long, string> codexLine = (r1, r2) => "{\"timestamp\":\"" + iso + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null,\"rate_limits\":{\"limit_id\":\"codex\",\"limit_name\":null,\"primary\":{\"used_percent\":24.0,\"window_minutes\":300,\"resets_at\":" + r1 + "},\"secondary\":{\"used_percent\":91.0,\"window_minutes\":10080,\"resets_at\":" + r2 + "},\"credits\":null,\"plan_type\":null}}}";
                string rollout = Path.Combine(cx, "sessions", "2026", "10", "07", "rollout-2026-10-07T00-00-00-aaaaaaaa-0000-4000-8000-000000000009.jsonl");
                File.WriteAllText(rollout, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"x\",\"cwd\":\"C:\\\\\"}}\n" + codexLine(nowSec + 3600, nowSec + 3 * 86400) + "\n");
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                Func<string, int, string> claudeLine = (id, outTok) => "{\"type\":\"assistant\",\"timestamp\":\"" + iso + "\",\"message\":{\"id\":\"" + id + "\",\"role\":\"assistant\",\"usage\":{\"input_tokens\":10,\"cache_creation_input_tokens\":5,\"cache_read_input_tokens\":999,\"output_tokens\":" + outTok + "}}}";
                File.WriteAllText(Path.Combine(cl, "projects", "p", "ccccccc1-0000-4000-8000-000000000001.jsonl"), claudeLine("msg_A", 5) + "\n" + claudeLine("msg_A", 50) + "\n" + claudeLine("msg_B", 20) + "\n");
                string pc = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), px = Environment.GetEnvironmentVariable("CODEX_HOME");
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", cl); Environment.SetEnvironmentVariable("CODEX_HOME", cx);
                try
                {
                    using (var m = new HarnessManager(Path.Combine(root, "data-plan")))
                    {
                        var rows = Dicts(m.Usage());
                        var cxr = rows.FirstOrDefault(x => (string)x["Key"] == "codex"); var clr = rows.FirstOrDefault(x => (string)x["Key"] == "claude");
                        if (cxr != null)
                        {
                            var wins = Dicts(cxr["Windows"]);
                            Check(wins.Length == 2 && (string)wins[0]["Label"] == "5-hour" && (double)wins[0]["Percent"] == 24.0 && (string)wins[1]["Label"] == "Weekly" && (double)wins[1]["Percent"] == 91.0, "plan: Codex's 5-hour and weekly percentages are read from its session logs");
                            Check((string)wins[1]["ResetsUtc"] != "" && (double)cxr["TopPercent"] == 91.0 && (string)cxr["Warn"] == "near", "plan: the weekly reset time is known and 91% raises a warning");
                        }
                        if (clr != null)
                        {
                            Check((long)clr["TokensToday"] == (10 + 5 + 50) + (10 + 5 + 20), "plan: Claude tokens come from the transcripts, counting each streamed message once (" + clr["TokensToday"] + ")");
                            Check((long)clr["TokensWeek"] >= (long)clr["TokensToday"], "plan: the weekly total includes today");
                        }
                        m.SetUsageBudget("claude", 1000, 5000); var again = Dicts(m.Usage()).FirstOrDefault(x => (string)x["Key"] == "claude");
                        if (again != null) Check((long)again["Budget"] == 1000 && (long)again["WeekBudget"] == 5000, "plan: daily and weekly limits are saved");
                    }
                    // a window that has reset since the last reading shows 0%
                    File.WriteAllText(rollout, codexLine(nowSec - 60, nowSec + 3 * 86400) + "\n");
                    using (var m = new HarnessManager(Path.Combine(root, "data-plan2")))
                    {
                        var cxr = Dicts(m.Usage()).FirstOrDefault(x => (string)x["Key"] == "codex");
                        if (cxr != null) { var w0 = Dicts(cxr["Windows"])[0]; Check((double)w0["Percent"] == 0.0 && (bool)w0["Stale"], "plan: a window that has renewed since the last reading shows 0%"); }
                    }
                }
                finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", pc); Environment.SetEnvironmentVariable("CODEX_HOME", px); }
            }
            {
                // three kinds of vendor in one team: a supervisor from one vendor, Gemini teammates (found on PATH like a real install), and a teammate from a third
                string shim = Path.Combine(root, "shim"); Directory.CreateDirectory(shim);
                File.WriteAllText(Path.Combine(shim, "gemini.cmd"), "@echo off\r\necho gemini-report args: %*\r\n");
                string fakeExe = System.Reflection.Assembly.GetExecutingAssembly().Location; string oldPath = Environment.GetEnvironmentVariable("PATH");
                Environment.SetEnvironmentVariable("PATH", shim + ";" + oldPath);
                try
                {
                    using (var m = new HarnessManager(Path.Combine(root, "data-cross")))
                    {
                        m.SaveAgent(new Dictionary<string, object> { { "Name", "XLeader" }, { "Command", fakeExe }, { "Args", "--fake-leader" } });
                        m.SaveAgent(new Dictionary<string, object> { { "Name", "XMember" }, { "Command", fakeExe }, { "Args", "--fake-member" } });
                        string xl = m.Harnesses().First(h => h.Name == "XLeader").Id, xm = m.Harnesses().First(h => h.Name == "XMember").Id;
                        Check(m.Harnesses().Any(h => h.Id == "gemini" && h.Available), "cross-vendor: Gemini CLI is detected as a vendor when it is installed");
                        string flag = m.ArgumentPreview("gemini", "default", "gemini-2.5-flash", "", false);
                        Check(flag.Contains("-m gemini-2.5-flash"), "cross-vendor: a Gemini teammate can be pinned to a Gemini model (" + flag + ")");
                        var team = (Dictionary<string, object>)m.SaveTeam(new Dictionary<string, object> { { "Title", "Mixed vendors" }, { "Cwd", work }, { "Leader", new Dictionary<string, object> { { "Harness", xl } } }, { "Members", new object[] {
                            new Dictionary<string, object> { { "Name", "Ann" }, { "Role", "researcher" }, { "Harness", "gemini" }, { "Model", "gemini-2.5-flash" } },
                            new Dictionary<string, object> { { "Name", "bob" }, { "Role", "builder" }, { "Harness", xm } },
                            new Dictionary<string, object> { { "Name", "Cara" }, { "Role", "reviewer" }, { "Harness", "gemini" } } } } });
                        string tid = (string)team["Id"]; m.RunTeam(tid, "Do the mixed thing");
                        Dictionary<string, object> cur = null;
                        for (int i = 0; i < 300; i++) { cur = Dicts(m.Teams()).First(x => (string)x["Id"] == tid); if (!(bool)cur["Running"]) break; Thread.Sleep(100); }
                        Check((string)cur["Phase"] == "done", "cross-vendor: a team of three vendors runs to the end (phase " + cur["Phase"] + ")");
                        var tasks = Dicts(cur["Tasks"]); var ann = tasks.FirstOrDefault(x => (string)x["Assignee"] == "Ann"); var bob = tasks.FirstOrDefault(x => (string)x["Assignee"] == "bob");
                        Check(ann != null && ((string)ann["Output"]).Contains("gemini-report") && ((string)ann["Output"]).Contains("gemini-2.5-flash"), "cross-vendor: the Gemini teammate did its task on Gemini with its chosen model");
                        Check(bob != null && ((string)bob["Output"]).Contains("report: finished"), "cross-vendor: the other vendor's teammate worked in parallel on its own agent");
                        var sess = Dicts(m.List()); var annS = sess.FirstOrDefault(x => ((string)x["Title"]).EndsWith("Ann")); var bobS = sess.FirstOrDefault(x => ((string)x["Title"]).EndsWith("bob"));
                        Check(annS != null && (string)annS["Harness"] == "gemini" && bobS != null && (string)bobS["Harness"] == xm, "cross-vendor: each teammate ran as its own chat on its own vendor");
                        Check(((string)cur["Result"]).Contains("FINAL SUMMARY: reports=2"), "cross-vendor: the supervisor read both vendors' reports and wrote the answer");
                        var ta = (Dictionary<string, object>)m.TeamAnalytics("all"); var tl = Dicts(ta["Teams"]).FirstOrDefault(x => (string)x["TeamId"] == tid);
                        Check(tl != null && (int)tl["Runs"] == 1 && (int)tl["Succeeded"] == 1, "team analytics: a finished run is recorded for its team");
                        var tm = tl == null ? new Dictionary<string, object>[0] : Dicts(tl["Members"]).ToArray();
                        Check(tm.Any(x => (string)x["Name"] == "Leader") && tm.Any(x => (string)x["Name"] == "Ann" && (string)x["Harness"] == "gemini") && tm.Any(x => (string)x["Name"] == "bob"), "team analytics: the leader and each teammate are listed with their vendor");
                        Check(tl != null && ((string[])tl["Vendors"]).Length >= 2, "team analytics: the team shows which vendors it mixes");
                    }
                }
                finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
            }
            {
                string b = Path.Combine(root, "ana"); string cx = Path.Combine(b, "codex"), cl = Path.Combine(b, "claude");
                Directory.CreateDirectory(Path.Combine(cx, "sessions", "2026", "10", "07")); Directory.CreateDirectory(Path.Combine(cl, "projects", "p"));
                DateTime now = DateTime.UtcNow, old = DateTime.UtcNow.AddDays(-40);
                Func<DateTime, string> ts = d => d.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z";
                Func<DateTime, string, string> turn = (d, model) => "{\"timestamp\":\"" + ts(d) + "\",\"type\":\"turn_context\",\"payload\":{\"model\":\"" + model + "\"}}";
                Func<DateTime, string, string> msg = (d, role) => "{\"timestamp\":\"" + ts(d) + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"" + role + "\",\"content\":[]}}";
                Func<DateTime, int, string> tok = (d, n) => "{\"timestamp\":\"" + ts(d) + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":99999},\"last_token_usage\":{\"input_tokens\":1,\"output_tokens\":1000,\"total_tokens\":" + n + "}}}}";
                Func<DateTime, string> call = d => "{\"timestamp\":\"" + ts(d) + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"id\":\"fc_1\",\"name\":\"shell_command\",\"arguments\":\"{}\"}}";
                File.WriteAllText(Path.Combine(cx, "sessions", "2026", "10", "07", "rollout-2026-10-07T00-00-00-aaaaaaaa-0000-4000-8000-0000000000a1.jsonl"), String.Join("\n", turn(now, "gpt-test-1"), msg(now, "user"), msg(now, "assistant"), tok(now, 500), call(now)) + "\n");
                File.WriteAllText(Path.Combine(cx, "sessions", "2026", "10", "07", "rollout-2026-08-28T00-00-00-aaaaaaaa-0000-4000-8000-0000000000a2.jsonl"), String.Join("\n", turn(old, "gpt-test-1"), msg(old, "assistant"), tok(old, 100)) + "\n");
                Func<DateTime, string, int, string> asst = (d, id, outTok) => "{\"type\":\"assistant\",\"timestamp\":\"" + ts(d) + "\",\"message\":{\"id\":\"" + id + "\",\"model\":\"claude-test\",\"usage\":{\"input_tokens\":10,\"cache_creation_input_tokens\":5,\"output_tokens\":" + outTok + "}},\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\"}]}";
                File.WriteAllText(Path.Combine(cl, "projects", "p", "bbbbbbbb-0000-4000-8000-0000000000b1.jsonl"), String.Join("\n", "{\"type\":\"user\",\"timestamp\":\"" + ts(now) + "\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}", asst(now, "msg_a", 5), asst(now, "msg_a", 20), asst(now, "msg_b", 7)) + "\n");
                string pc = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), px = Environment.GetEnvironmentVariable("CODEX_HOME");
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", cl); Environment.SetEnvironmentVariable("CODEX_HOME", cx);
                try
                {
                    using (var m = new HarnessManager(Path.Combine(root, "data-ana")))
                    {
                        Dictionary<string, object> all = null;
                        for (int i = 0; i < 100; i++) { all = (Dictionary<string, object>)m.Analytics("all"); var ix = (Dictionary<string, object>)all["Indexing"]; if (!(bool)ix["Running"] && (int)ix["Total"] > 0) break; Thread.Sleep(100); }
                        Check((int)all["Sessions"] == 3 && (long)all["Messages"] == 6 && (long)all["Tokens"] == 500 + 100 + 35 + 22, "analytics: sessions, messages and tokens add up across Codex and Claude (" + all["Sessions"] + "/" + all["Messages"] + "/" + all["Tokens"] + ")");
                        Check((int)all["ActiveDays"] == 2 && (string)all["FavoriteModel"] == "gpt-test-1" && (int)all["PeakHour"] >= 0, "analytics: active days, favourite model and peak hour are found");
                        var d30 = (Dictionary<string, object>)m.Analytics("30d"); var d7 = (Dictionary<string, object>)m.Analytics("7d");
                        Check((int)d30["Sessions"] == 2 && (long)d30["Tokens"] == 557 && (long)d7["Messages"] == 5, "analytics: the 30-day and 7-day ranges leave the old session out");
                        var models = Dicts(all["Models"]); var gpt = models.FirstOrDefault(x => (string)x["Name"] == "gpt-test-1"); var cld = models.FirstOrDefault(x => (string)x["Name"] == "claude-test");
                        Check(gpt != null && (long)gpt["Tokens"] == 600 && cld != null && (long)cld["Tokens"] == 57 && (string)cld["Vendor"] == "Claude", "analytics: tokens are attributed to the model that used them (a streamed Claude message counts once)");
                        Check(Dicts(all["Tools"]).Any(x => (string)x["Name"] == "shell_command") && Dicts(all["Tools"]).Any(x => (string)x["Name"] == "Bash"), "analytics: tool use is counted for both agents");
                        Check((int)all["CurrentStreak"] >= 1 && ((object[])all["Grid"]).Length >= 26 * 7 - 7, "analytics: the streak and the heatmap grid are produced");
                        var onlyCodex = (Dictionary<string, object>)m.Analytics("all", "codex"); var onlyClaude = (Dictionary<string, object>)m.Analytics("all", "claude");
                        Check((int)onlyCodex["Sessions"] == 2 && (long)onlyCodex["Tokens"] == 600 && (int)onlyClaude["Sessions"] == 1 && (long)onlyClaude["Tokens"] == 57, "analytics: each vendor can be viewed on its own");
                        var daily = (object[])all["Daily"]; long dailySum = 0; foreach (object o in daily) dailySum += Convert.ToInt64(((object[])o)[2]) + Convert.ToInt64(((object[])o)[3]);
                        Check(daily.Length >= 2 && dailySum == 657 && ((object[])all["Weekdays"]).Cast<object>().Sum(x => Convert.ToInt64(x)) == 6, "analytics: the daily series and weekday totals add up to the same figures");
                        double costBefore = Convert.ToDouble(all["Cost"], System.Globalization.CultureInfo.InvariantCulture);
                        Check(costBefore > 0, "analytics: an estimated cost is produced from the token counts");
                        var rows = new List<object>(); foreach (object pr in (object[])m.PricesGet()) { var d = (Dictionary<string, object>)pr; rows.Add(new Dictionary<string, object> { { "Class", d["Class"] }, { "Input", "0" }, { "Output", "0" }, { "CacheRead", "0" }, { "CacheWrite", "0" } }); }
                        m.PricesSet(new Dictionary<string, object> { { "Rows", rows } });
                        Check(Convert.ToDouble(((Dictionary<string, object>)m.Analytics("all", ""))["Cost"], System.Globalization.CultureInfo.InvariantCulture) == 0.0, "analytics: editing prices changes the estimate");
                        m.PricesSet(new Dictionary<string, object>());                    }
                    using (var m = new HarnessManager(Path.Combine(root, "data-ana")))
                    {
                        var again = (Dictionary<string, object>)m.Analytics("all"); Thread.Sleep(400);
                        Check(File.Exists(Path.Combine(root, "data-ana", "harness", "analytics.json")) && (long)((Dictionary<string, object>)m.Analytics("all"))["Tokens"] == 657, "analytics: the index is cached on disk and reused after a restart");
                    }
                }
                finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", pc); Environment.SetEnvironmentVariable("CODEX_HOME", px); }
            }
            {
                string crepo = Path.Combine(root, "coauthor-repo"); Directory.CreateDirectory(crepo); GitTools.Run(crepo, "init -q -b main"); GitTools.Run(crepo, "config user.email t@t"); GitTools.Run(crepo, "config user.name t");
                string hookFile = Path.Combine(crepo, ".git", "hooks", "commit-msg"); File.WriteAllText(hookFile, "#!/bin/sh\necho \"Reviewed-by: repo hook\" >> \"$1\"\n");
                using (var m = new HarnessManager(Path.Combine(root, "data-coauthor")))
                {
                    Func<string, string> commitAs = text =>
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo { FileName = GitTools.Exe(), Arguments = "commit -q --allow-empty -m \"" + text + "\" -m \"Co-authored-by: Claude <noreply@anthropic.com>\"", WorkingDirectory = crepo, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                        m.CoAuthorEnv(psi, crepo); using (var pr = System.Diagnostics.Process.Start(psi)) { pr.StandardOutput.ReadToEnd(); pr.StandardError.ReadToEnd(); pr.WaitForExit(); }
                        return GitTools.Run(crepo, "log -1 --format=%B");
                    };
                    string on = commitAs("one");
                    Check(on.Contains("Co-authored-by: LAICA <noreply@laica.invalid>") && on.Contains("Co-authored-by: Claude <noreply@anthropic.com>"), "co-author: LAICA is added next to the agent's own trailer");
                    Check(on.Contains("Reviewed-by: repo hook"), "co-author: the repository's own commit hook still runs");
                    Check(on.Split(new[] { "Co-authored-by: LAICA" }, StringSplitOptions.None).Length == 2, "co-author: the trailer is added only once");
                    var cfg0 = (Dictionary<string, object>)m.CoAuthorGet(); Check((bool)cfg0["Enabled"] && (bool)cfg0["Placeholder"], "co-author: on by default, with a placeholder address until one is chosen");
                    m.CoAuthorSet(new Dictionary<string, object> { { "Cwd", crepo }, { "ProjectEnabled", false } });
                    Check(!commitAs("two").Contains("LAICA"), "co-author: it can be turned off for one project");
                    m.CoAuthorSet(new Dictionary<string, object> { { "Cwd", crepo }, { "ProjectEnabled", true } });
                    Check(commitAs("three").Contains("Co-authored-by: LAICA"), "co-author: and turned back on");
                    m.CoAuthorSet(new Dictionary<string, object> { { "Email", "12345+laica-bot@users.noreply.github.com" }, { "Name", "LAICA" } });
                    Check(commitAs("four").Contains("Co-authored-by: LAICA <12345+laica-bot@users.noreply.github.com>"), "co-author: a chosen GitHub address is used in the trailer");
                    bool badMail = false; try { m.CoAuthorSet(new Dictionary<string, object> { { "Email", "not an email" } }); } catch (ArgumentException) { badMail = true; }
                    Check(badMail, "co-author: an invalid address is refused");
                    m.CoAuthorSet(new Dictionary<string, object> { { "Enabled", false } });
                    Check(!commitAs("five").Contains("LAICA"), "co-author: the global switch turns it off everywhere");
                }
            }            {
                string fakeExe = System.Reflection.Assembly.GetExecutingAssembly().Location; string hroot = Path.Combine(root, "data-handoff");
                Func<HarnessManager, string, string, string> agent = (hm, hname, hargs) => { hm.SaveAgent(new Dictionary<string, object> { { "Name", hname }, { "Command", fakeExe }, { "Args", hargs } }); return hm.Harnesses().First(h => h.Name == hname).Id; };
                Func<HarnessManager, string, string, string, string> team = (m, title, leaderId, memberId) => (string)((Dictionary<string, object>)m.SaveTeam(new Dictionary<string, object> { { "Title", title }, { "Cwd", work }, { "Leader", new Dictionary<string, object> { { "Harness", leaderId } } }, { "Members", new object[] { new Dictionary<string, object> { { "Name", "Ann" }, { "Harness", memberId } }, new Dictionary<string, object> { { "Name", "bob" }, { "Harness", memberId } } } } }))["Id"];
                Func<HarnessManager, string, string, bool> waitEvent = (wm, wsid, wkind) => { for (int i = 0; i < 80; i++) { if (Dicts(wm.History(wsid)).Any(h => (string)h["Kind"] == wkind)) return true; Thread.Sleep(100); } return false; };
                Action<HarnessManager, string> limitChat = (m, xid) => { };
                using (var m = new HarnessManager(hroot))
                {
                    m.SaveAgent(new Dictionary<string, object> { { "Name", "HLimited" }, { "Command", fakeExe }, { "Args", "--fake-limited" }, { "Parser", "codex" } }); string xl = m.Harnesses().First(h => h.Name == "HLimited").Id;
                    string mid = agent(m, "HMember", "--fake-member"), lid = agent(m, "HLeader", "--fake-leader");
                    bool badAgent = false; try { m.HandoffSet(new Dictionary<string, object> { { "Mode", "agent" }, { "Harness", "no-such-agent" } }); } catch (ArgumentException) { badAgent = true; }
                    bool bad2 = false; try { m.HandoffSet(new Dictionary<string, object> { { "Mode", "team" }, { "TeamId", "nope" } }); } catch (ArgumentException) { bad2 = true; }
                    Check(badAgent && bad2 && (string)((Dictionary<string, object>)m.HandoffGet())["Mode"] == "manual", "handoff: an unknown agent or team is refused and the setting stays on manual");
                    // manual: nothing moves
                    var c0 = (Dictionary<string, object>)m.Create(xl, work, null, null, null, "manual chat"); m.Send((string)c0["Id"], "go");
                    Check(waitEvent(m, (string)c0["Id"], "limit"), "handoff: a usage limit is noticed");
                    Thread.Sleep(600); Check(!Dicts(m.History((string)c0["Id"])).Any(h => (string)h["Kind"] == "handoff") && Dicts(m.List()).Count(x => ((string)x["Title"]).StartsWith("Continued:")) == 0, "handoff: manual mode moves nothing by itself");
                    // automatic to a single agent
                    m.ClearUsageLimit(xl);
                    m.HandoffSet(new Dictionary<string, object> { { "Mode", "agent" }, { "Harness", mid } });
                    var c1 = (Dictionary<string, object>)m.Create(xl, work, null, null, null, "agent handoff chat"); m.Send((string)c1["Id"], "please do the thing");
                    Check(waitEvent(m, (string)c1["Id"], "handoff"), "handoff: the chat is handed to the chosen agent automatically");
                    var moved = Dicts(m.List()).FirstOrDefault(x => ((string)x["Title"]).StartsWith("Continued:") && (string)x["Harness"] == mid);
                    Check(moved != null, "handoff: a new chat continues the work on the other vendor");
                    // automatic to a backup team
                    string backup = team(m, "Backup crew", lid, mid); m.ClearUsageLimit(xl);
                    m.HandoffSet(new Dictionary<string, object> { { "Mode", "team" }, { "TeamId", backup } });
                    var c2 = (Dictionary<string, object>)m.Create(xl, work, null, null, null, "team handoff chat"); m.Send((string)c2["Id"], "build it");
                    Check(waitEvent(m, (string)c2["Id"], "handoff"), "handoff: the chat is handed to the backup team automatically");
                    Dictionary<string, object> bt = null; for (int i = 0; i < 300; i++) { bt = Dicts(m.Teams()).First(x => (string)x["Id"] == backup); if ((string)bt["Phase"] == "done") break; Thread.Sleep(100); }
                    Check((string)bt["Phase"] == "done" && ((string)bt["Goal"]).Contains("taking over work"), "handoff: the backup team received the conversation and finished");
                    // a backup team that uses the vendor that ran out is refused
                    string sameVendorTeam = team(m, "Same vendor crew", lid, xl); m.ClearUsageLimit(xl);
                    m.HandoffSet(new Dictionary<string, object> { { "Mode", "team" }, { "TeamId", sameVendorTeam } });
                    var c3 = (Dictionary<string, object>)m.Create(xl, work, null, null, null, "refused chat"); m.Send((string)c3["Id"], "again");
                    Check(waitEvent(m, (string)c3["Id"], "notice") && !Dicts(m.History((string)c3["Id"])).Any(h => (string)h["Kind"] == "handoff"), "handoff: a backup team that uses the exhausted vendor is not used");
                    // manual team handoff keeps what was done
                    m.ClearUsageLimit(xl); string primary = team(m, "Primary crew", lid, mid); m.RunTeam(primary, "Ship it");
                    for (int i = 0; i < 300; i++) { if ((string)Dicts(m.Teams()).First(x => (string)x["Id"] == primary)["Phase"] == "done") break; Thread.Sleep(100); }
                    m.HandoffTeam(primary, new Dictionary<string, object> { { "Mode", "team" }, { "TeamId", backup } });
                    var pt = Dicts(m.Teams()).First(x => (string)x["Id"] == primary);
                    Check(((string)pt["Note"]).Contains("Handed to the team Backup crew") && ((string)pt["HandoffTo"]) == "team:" + backup, "handoff: a team's work can be moved by hand and the team says where it went");
                    var prefs = Dicts(m.Teams()).First(x => (string)x["Id"] == backup);
                    Check(((string)prefs["Goal"]).Contains("Work reported so far"), "handoff: the work already done travels with the goal");
                }
                using (var m = new HarnessManager(hroot))
                    Check((string)((Dictionary<string, object>)m.HandoffGet())["Mode"] == "team", "handoff: the setting survives a restart");
            }
            using (var m2 = new HarnessManager(root))
            {
                var list = Dicts(m2.List());
                Check(list.Any(x => (string)x["Id"] == sid), "chat restored after restart");
                var hist = Dicts(m2.History(sid));
                Check(hist.Any(h => ((string)h["Text"]).Contains("ping-123")), "history restored after restart");
                Check(((object[])m2.Tasks()).Length == 1, "tasks restored after restart");
                m2.Close(sid);
                Check(!Dicts(m2.List()).Any(x => (string)x["Id"] == sid), "chat closed");
            }
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures;
    }
}
