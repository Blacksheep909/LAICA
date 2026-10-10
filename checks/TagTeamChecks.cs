using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Laica;

/// <summary>Checks for tag-team continuity: handoff built from the event log with no model, HANDOFF.md, the pair engine and its guards.</summary>
public static class TagTeamChecks
{
    static int failures;
    static void Check(bool ok, string name) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
    static Dictionary<string, object>[] Dicts(object o) { return ((System.Collections.IEnumerable)o).Cast<object>().Cast<Dictionary<string, object>>().ToArray(); }
    static bool Until(Func<bool> f, int ms) { for (int i = 0; i < ms / 50; i++) { try { if (f()) return true; } catch (Exception) { } Thread.Sleep(50); } return f(); }
    static Dictionary<string, object> Info(HarnessManager m, string id) { return (Dictionary<string, object>)m.PairInfo(id); }
    static string Active(HarnessManager m, string id) { return (string)Info(m, id)["Active"]; }
    static bool Busy(HarnessManager m, string id) { return Dicts(m.List()).First(x => (string)x["Id"] == id)["Busy"].Equals(true); }
    static int Count(HarnessManager m, string id, string kind) { return Dicts(m.History(id)).Count(h => (string)h["Kind"] == kind); }
    static void Settle(HarnessManager m, string id, string active, int switches)
    {
        Until(delegate { return Active(m, id) == active && Count(m, id, "switch") >= switches && !Busy(m, id); }, 40000);
        for (int i = 0; i < 6; i++) { Thread.Sleep(300); Until(delegate { return !Busy(m, id); }, 40000); }
    }
    static void Diag(HarnessManager m, string id, string agent) { Console.WriteLine("   active=" + Active(m, id) + " switches=" + Count(m, id, "switch") + " busy=" + Busy(m, id)); foreach (var h in Dicts(m.History(id))) { string k = (string)h["Kind"]; if (k == "user" || k == "assistant" || k == "switch" || k == "pairnote" || k == "error" || k == "limit" || k == "pairwait") Console.WriteLine("   [" + k + "] " + ((string)h["Text"]).Replace("\n", " ").Substring(0, Math.Min(110, ((string)h["Text"]).Length))); } try { Console.WriteLine("   LAST " + agent + ": " + LastSection(agent).Replace("\n", " | ").Substring(0, Math.Min(900, LastSection(agent).Length))); } catch (Exception) { } }
    static string logs;
    static string Flag(string name) { return Path.Combine(logs, name + ".flag"); }
    static void Set(string name) { File.WriteAllText(Flag(name), ""); }
    static void Unset(string name) { try { File.Delete(Flag(name)); } catch (Exception) { } }
    static string LastSection(string agent) { string t = File.ReadAllText(Path.Combine(logs, "tag-" + agent + ".log")); int i = t.LastIndexOf("====="); return i >= 0 ? t.Substring(i) : t; }
    static Dictionary<string, object> Ev(string kind, string text, string detail, long n, DateTime t)
    {
        return new Dictionary<string, object> { { "Kind", kind }, { "Text", text }, { "Detail", detail }, { "N", n }, { "TimeUtc", t.ToString("o") } };
    }

    // Stand-in for a vendor CLI used by the pair: logs what it was told, writes a file, or runs out of usage when its flag exists.
    public static int FakeTag(string name)
    {
        string all = Console.In.ReadToEnd(), dir = Environment.GetEnvironmentVariable("LAICA_TAG_LOGS") ?? Path.GetTempPath();
        File.AppendAllText(Path.Combine(dir, "tag-" + name + ".log"), "=====\n" + all + "\n");
        if (File.Exists(Path.Combine(dir, "limit-" + name + ".flag"))) { Console.WriteLine("{\"type\":\"error\",\"message\":\"You have hit your usage limit. Try again in 2 hours.\"}"); return 0; }
        if (File.Exists(Path.Combine(dir, "writer-" + name + ".flag"))) { Console.WriteLine("{\"type\":\"error\",\"message\":\"thread-store conflict: thread 11111111-1111-1111-1111-111111111111 already has an active writer\"}"); return 0; }
        if (File.Exists(Path.Combine(dir, "autherr-" + name + ".flag"))) { Console.WriteLine("{\"type\":\"error\",\"message\":\"Not logged in \u00b7 Please run /login\"}"); return 0; }
        string rf = Path.Combine(dir, "resumefail-" + name + ".flag");
        if (File.Exists(rf)) { File.Delete(rf); Console.WriteLine("{\"type\":\"error\",\"message\":\"No conversation found with session ID thr-" + name + "\"}"); return 0; }
        int turn = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(dir, "tag-" + name + ".log")), "=====").Count;
        if (!File.Exists(Path.Combine(dir, "nowrite.flag"))) File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "out-" + name + "-" + turn + ".txt"), "work");
        Console.WriteLine("{\"type\":\"thread.started\",\"thread_id\":\"thr-" + name + "\"}");
        Console.WriteLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"" + name + " worked on turn " + turn + "\"}}");
        return 0;
    }

    public static int Run(string root)
    {
        string fakeExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
        logs = Path.Combine(root, "taglogs"); Directory.CreateDirectory(logs); Environment.SetEnvironmentVariable("LAICA_TAG_LOGS", logs);

        // ---------- the handoff builder: no model, only the event log and the folder ----------
        {
            string folder = Path.Combine(root, "buildwork"); Directory.CreateDirectory(folder);
            var t0 = DateTime.UtcNow.AddMinutes(-10); var ev = new List<Dictionary<string, object>>();
            ev.Add(Ev("user", "Build a login page with the key sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWX", null, 1, t0));
            ev.Add(Ev("assistant", "I will start with the form.", null, 2, t0.AddSeconds(5)));
            ev.Add(Ev("tool", "Write", "{\"file_path\":\"" + folder.Replace("\\", "\\\\") + "\\\\login.html\",\"content\":\"x\"}", 3, t0.AddSeconds(10)));
            ev.Add(Ev("tool_result", "ok", null, 4, t0.AddSeconds(11)));
            ev.Add(Ev("tool", "Bash", "{\"command\":\"npm test\"}", 5, t0.AddSeconds(12)));
            ev.Add(Ev("tool_result", "failed", "error", 6, t0.AddSeconds(13)));
            ev.Add(Ev("tool", "Edit", "{\"file_path\":\"" + folder.Replace("\\", "\\\\") + "\\\\style.css\",\"old_string\":\"a\",\"new_string\":\"b\"}", 7, t0.AddSeconds(20)));
            ev.Add(Ev("error", "You have hit your usage limit", null, 8, t0.AddSeconds(21)));
            ev.Add(Ev("limit", "Claude Code has run out of usage.", "claude", 9, t0.AddSeconds(21)));
            var info = HandoffBuilder.Build(ev, folder, "Always use tabs", "Claude Code", DateTime.UtcNow);
            Check(info.Goal.Contains("Build a login page") && info.Unfinished && info.Latest.Contains("login page"), "handoff builder: goal, latest request and the unfinished state come from the event log alone");
            Check(info.Files.Any(f => f.StartsWith("login.html")) && info.Files.Any(f => f.StartsWith("style.css")), "handoff builder: files created and changed are listed relative to the project");
            Check(info.Commands.Any(c => c.Contains("npm test") && c.Contains("failed")), "handoff builder: recent commands carry their outcome");
            Check(info.VerifyFirst.Contains("style.css") && !info.VerifyFirst.Contains("login.html"), "handoff builder: the edit that never finished is flagged verify-first, finished ones are not");
            string md = HandoffBuilder.RenderManaged(info);
            Check(!md.Contains("ABCDEFGHIJKLMNOPQRSTUVWX") && md.Contains("[redacted]"), "handoff builder: keys are redacted in the file text");
            Check(HandoffBuilder.Redact("password=hunter2hunter2 and Bearer abcdefghijklmnopqrstuvwxyz and ghp_abcdefghijklmnopqrstuvwxyz0123456789").IndexOf("hunter2hunter2") < 0 && HandoffBuilder.Redact("-----BEGIN RSA PRIVATE KEY-----\nMIIabc\n-----END RSA PRIVATE KEY-----").Contains("[redacted]") && !HandoffBuilder.Redact("ghp_abcdefghijklmnopqrstuvwxyz0123456789").Contains("ghp_abc"), "redaction: passwords, bearer tokens, GitHub tokens and private keys are removed");
            // half-written file found by file time alone (no edit event at all, no git)
            string touched = Path.Combine(folder, "half.js"); File.WriteAllText(touched, "x");
            var ev2 = new List<Dictionary<string, object>> { Ev("user", "go", null, 1, DateTime.UtcNow.AddSeconds(-5)), Ev("error", "usage limit reached", null, 2, DateTime.UtcNow) };
            Check(HandoffBuilder.Build(ev2, folder, "", "x", DateTime.UtcNow).VerifyFirst.Contains("half.js"), "handoff builder: a file written seconds before the agent stopped is flagged verify-first without git");
            Check(HandoffBuilder.FolderChanges(folder, DateTime.UtcNow.AddMinutes(-1), 10).Contains("half.js") && !HandoffBuilder.FolderStamp(folder).Equals(""), "no git: changed files come from file times");
            // delta: only what is new
            var ev3 = new List<Dictionary<string, object>>();
            ev3.Add(Ev("user", "OLDREQUEST first thing", null, 1, t0)); ev3.Add(Ev("assistant", "OLDREPLY done", null, 2, t0));
            ev3.Add(Ev("user", "NEWREQUEST add search", null, 3, t0)); ev3.Add(Ev("assistant", "NEWREPLY added it", null, 4, t0));
            ev3.Add(Ev("tool", "file change", "[{\"path\":\"search.ts\",\"kind\":\"add\"}]", 5, t0));
            string delta = HandoffBuilder.Delta(ev3, 2, "Codex", folder, "HANDOFF.md", 6000);
            Check(delta.Contains("NEWREQUEST") && delta.Contains("NEWREPLY") && delta.Contains("search.ts") && !delta.Contains("OLDREQUEST") && !delta.Contains("OLDREPLY") && delta.Contains("3 new events"), "delta: contains only events newer than the receiver's last turn, never a full replay");
            // a longer conversation is carried in full, not squeezed to a line each
            var long1 = new List<Dictionary<string, object>>(); long n = 0;
            for (int q = 1; q <= 12; q++) { long1.Add(Ev("user", "Request number " + q + ": please do part " + q + " of the dashboard", null, ++n, t0)); long1.Add(Ev("assistant", "Part " + q + " is finished. I chose approach " + q + " because of reason " + q + ", and left a follow-up about caching for part " + (q + 1) + ".", null, ++n, t0)); long1.Add(Ev("done", "", null, ++n, t0)); }
            var big = HandoffBuilder.Build(long1, folder, "", "Codex", DateTime.UtcNow); string bigMd = HandoffBuilder.RenderManaged(big);
            Check(big.Requests.Count == 12 && big.Done.Count == 12 && big.Recent.Count == 6 && bigMd.Contains("approach 12 because of reason 12") && bigMd.Contains("approach 7 because of reason 7") && bigMd.Contains("Request number 1:") && bigMd.Length > 3500, "handoff builder: every request, every turn and the recent conversation (with the agent's own words) are carried, not just one line");
            string bigDelta = HandoffBuilder.Delta(long1, 12, "Codex", folder, "HANDOFF.md", 14000);
            Check(bigDelta.Contains("approach 12 because of reason 12") && bigDelta.Contains("approach 7 because of reason 7") && !bigDelta.Contains("approach 3 because"), "delta: carries each new reply in the agent's own words, and still nothing from before the cut-off");
            string merged = HandoffBuilder.Merge("# My notes\r\nkeep me\r\n", HandoffBuilder.RenderManaged(info));
            Check(merged.Contains("keep me") && merged.Contains(HandoffBuilder.StartMark) && HandoffBuilder.Merge(merged, HandoffBuilder.RenderManaged(info)).Split(new[] { HandoffBuilder.StartMark }, StringSplitOptions.None).Length == 2, "HANDOFF.md: an existing file is kept and the block is replaced, not duplicated");
            var d1 = HarnessManager.ParseResetClock("5-hour limit reached - resets 3pm", new DateTime(2026, 10, 9, 10, 0, 0));
            var d2 = HarnessManager.ParseResetClock("limit reached, resets 3:40 pm", new DateTime(2026, 10, 9, 16, 0, 0));
            Check(d1 == new DateTime(2026, 10, 9, 15, 0, 0) && d2 == new DateTime(2026, 10, 10, 15, 40, 0) && HarnessManager.ParseResetClock("nothing here", DateTime.Now) == DateTime.MinValue, "reset times: 'resets 3pm' in a limit message becomes the next 3 pm");
            Check(!new AgentNode().CanEdit, "designer: nodes are read-only unless switched to can-edit");
        }

        // ---------- a plan that is really spent must not sit at 99% ----------
        {
            Check(HarnessManager.ParseTryAgain("...or try again at Oct 10th, 2026 2:26 AM.") == new DateTime(2026, 10, 10, 2, 26, 0, DateTimeKind.Local).ToUniversalTime() && HarnessManager.ParseTryAgain("no time here") == DateTime.MinValue, "usage: 'try again at <date time>' in a limit message becomes the reset time");
            string codexHome = Path.Combine(root, "spent-codex"), day = Path.Combine(codexHome, "sessions", "2026", "10", "09"); Directory.CreateDirectory(day);
            string past = DateTime.UtcNow.AddMinutes(-20).ToString("o"), later = DateTime.UtcNow.AddMinutes(-10).ToString("o"); long soon = (long)(DateTime.UtcNow.AddHours(3) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            File.WriteAllText(Path.Combine(day, "rollout-2026-10-09T01-00-00-spent.jsonl"),
                "{\"timestamp\":\"" + past + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":99.0,\"window_minutes\":300,\"resets_at\":" + soon + "},\"secondary\":{\"used_percent\":31.0,\"window_minutes\":10080,\"resets_at\":" + (soon + 400000) + "},\"plan_type\":null}}}\n" +
                "{\"timestamp\":\"" + later + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{\"limit_id\":\"premium\",\"primary\":null,\"secondary\":null,\"plan_type\":null}}}\n" +
                "{\"timestamp\":\"" + later + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"error\":{\"message\":\"You\\u2019ve hit your usage limit. try again at Oct 10th, 2030 2:26 AM.\",\"codex_error_info\":\"usage_limit_exceeded\"}}}\n");
            string pc = Environment.GetEnvironmentVariable("CODEX_HOME"), pcl = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome); Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(root, "spent-claude"));
            try
            {
                using (var m = new HarnessManager(Path.Combine(root, "data-spent")))
                {
                    var row = Dicts(m.Usage()).FirstOrDefault(x => (string)x["Key"] == "codex");
                    if (row == null) Console.WriteLine("SKIP usage: Codex isn't installed here");
                    else
                    {
                        var wins = Dicts(row["Windows"]); var five = wins.FirstOrDefault(w => (string)w["Label"] == "5-hour");
                        Check(five != null && Convert.ToDouble(five["Percent"]) >= 100 && Convert.ToDouble(row["TopPercent"]) >= 100, "usage: a usage-limit error after the last reading shows the 5-hour window as spent, not 99%");
                        Check(five != null && DateTime.Parse((string)five["ResetsUtc"], null, System.Globalization.DateTimeStyles.RoundtripKind).Year == 2030, "usage: the reset time comes from the limit message when it names one");
                    }
                }
            }
            finally { Environment.SetEnvironmentVariable("CODEX_HOME", pc); Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", pcl); }
        }
        {
            Check(HarnessManager.ParseAuth("claude", 1, "{ \"loggedIn\": false, \"authMethod\": \"none\" }") == false && HarnessManager.ParseAuth("claude", 0, "{ \"loggedIn\": true }") == true && HarnessManager.ParseAuth("claude", 0, "garbage") == null, "sign-in: Claude's own status output says whether it is signed in");
            Check(HarnessManager.ParseAuth("codex", 0, "Logged in using ChatGPT") == true && HarnessManager.ParseAuth("codex", 1, "Not logged in") == false, "sign-in: Codex's own status output says whether it is signed in");
        }
        {
            using (var m = new HarnessManager(Path.Combine(root, "data-modes")))
            {
                string fresh = m.ArgumentPreview("codex", "approve-for-me", "", "", false), resumed = m.ArgumentPreview("codex", "approve-for-me", "", "", true);
                Check(fresh.Contains("--approve-for-me") && !fresh.Contains("-s workspace-write") && resumed.StartsWith("exec --approve-for-me resume "), "approve for me: Codex runs with its own automatic reviewer (new and resumed chats)");
                Check(m.ArgumentPreview("claude", "auto", "", "", false).Contains("--permission-mode auto") && m.Harnesses().First(h => h.Id == "claude").Modes.Contains("auto") && m.Harnesses().First(h => h.Id == "codex").Modes.Contains("approve-for-me"), "approve for me: Claude's auto mode and Codex's approve-for-me are offered as permission levels");
            }
        }
        {
            using (var m = new HarnessManager(Path.Combine(root, "data-pmode")))
            {
                string wk = Path.Combine(root, "pmodework"); Directory.CreateDirectory(wk);
                if (m.Harnesses().Any(h => h.Id == "codex" && h.Available) && m.Harnesses().Any(h => h.Id == "claude" && h.Available))
                {
                    string cid = (string)((Dictionary<string, object>)m.Create("codex", wk, "approve-for-me", null, null, "perm chat"))["Id"];
                    m.PairConfigure(cid, new Dictionary<string, object> { { "Mode", "automatic" }, { "PartnerHarness", "claude" } });
                    Check((string)((Dictionary<string, object>)Info(m, cid)["Partner"])["Mode"] == "auto", "partner permissions: Claude runs in auto mode when the chat uses Codex's Approve for me");
                    m.Configure(cid, "ask-first", null, null);
                    Check((string)((Dictionary<string, object>)Info(m, cid)["Partner"])["Mode"] == "default", "partner permissions: the partner follows the chat when its level changes (Ask first -> Claude asks)");
                    m.PairConfigure(cid, new Dictionary<string, object> { { "PartnerMode", "acceptEdits" } });
                    Check((string)((Dictionary<string, object>)Info(m, cid)["Partner"])["Mode"] == "acceptEdits" && (string)Info(m, cid)["PartnerMode"] == "acceptEdits", "partner permissions: a level chosen for the partner wins over following the chat");
                }
                else Console.WriteLine("SKIP partner permissions: Codex and Claude aren't both installed here");
            }
        }
        // ---------- settings: gentle migration ----------
        {
            string legacy = Path.Combine(root, "data-legacy", "harness"); Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "handoff.json"), "[{\"Mode\":\"agent\",\"TeamId\":\"\",\"Harness\":\"codex\",\"ServiceId\":\"\",\"Model\":\"\"}]");
            using (var m = new HarnessManager(Path.Combine(root, "data-legacy")))
            {
                var c = (Dictionary<string, object>)m.ContinuityGet();
                Check((string)((Dictionary<string, object>)m.HandoffGet())["Mode"] == "agent" && (string)c["Mode"] == "assisted", "settings: an old handoff.json keeps its mode and continuity starts as assisted");
                m.ContinuitySet(new Dictionary<string, object> { { "Mode", "automatic" }, { "MinGapMinutes", "3" }, { "ThrashSwitches", "999" }, { "HandoffPath", "..\\evil.md" } });
                m.HandoffSet(new Dictionary<string, object> { { "Mode", "manual" } });
            }
            using (var m = new HarnessManager(Path.Combine(root, "data-legacy")))
            {
                var c = (Dictionary<string, object>)m.ContinuityGet();
                Check((string)((Dictionary<string, object>)m.HandoffGet())["Mode"] == "manual" && (string)c["Mode"] == "automatic" && Convert.ToInt32(c["MinGapMinutes"]) == 3 && Convert.ToInt32(c["ThrashSwitches"]) == 20 && (string)c["HandoffPath"] == "HANDOFF.md", "settings: continuity is saved beside the handoff setting, survives a restart, is clamped and refuses paths outside the folder");
            }
        }

        // ---------- the pair ----------
        string tw = Path.Combine(root, "tagwork"); Directory.CreateDirectory(tw); File.WriteAllText(Path.Combine(tw, "readme.txt"), "project");
        using (var m = new HarnessManager(Path.Combine(root, "data-tag")))
        {
            m.SaveAgent(new Dictionary<string, object> { { "Name", "TagA" }, { "Command", fakeExe }, { "Args", "--fake-tag A" }, { "Parser", "codex" } });
            m.SaveAgent(new Dictionary<string, object> { { "Name", "TagB" }, { "Command", fakeExe }, { "Args", "--fake-tag B" }, { "Parser", "codex" } });
            string ida = m.Harnesses().First(h => h.Name == "TagA").Id, idb = m.Harnesses().First(h => h.Name == "TagB").Id;
            m.ContinuitySet(new Dictionary<string, object> { { "Mode", "assisted" }, { "ThrashSwitches", "10" }, { "MinGapMinutes", "0" } });
            string cid = (string)((Dictionary<string, object>)m.Create(ida, tw, null, "Never delete the database", null, "tag chat"))["Id"];
            m.PairConfigure(cid, new Dictionary<string, object> { { "Mode", "automatic" }, { "PartnerHarness", idb } });
            m.Send(cid, "Build the login page. My key is sk-abcdefghijklmnopqrstuvwxyz123456 and password=hunter2hunter2");
            Settle(m, cid, "primary", 0);
            string hp = Path.Combine(tw, "HANDOFF.md");
            string hmd = File.Exists(hp) ? File.ReadAllText(hp) : "";
            Check(hmd.Contains("Build the login page") && hmd.Contains("Never delete the database") && hmd.Contains("Last updated by"), "HANDOFF.md: written after a turn with the goal, constraints and who updated it, in a folder with no git");
            Check(hmd.IndexOf("hunter2hunter2") < 0 && hmd.IndexOf("sk-abcdefghijklmnopqrstuvwxyz") < 0, "HANDOFF.md: secrets in the chat never reach the file");
            m.HandoffFileSet(tw, "Keep it simple. Use tabs.");
            m.Send(cid, "Second request"); Settle(m, cid, "primary", 0);
            var got = (Dictionary<string, object>)m.HandoffFileGet(tw);
            Check((string)got["Pinned"] == "Keep it simple. Use tabs." && File.Exists(Path.Combine(tw, "HANDOFF.prev.md")) && ((string)got["Managed"]).Contains("Second request"), "HANDOFF.md: your Pinned notes survive every rewrite and the previous copy is kept");

            // A runs out -> B takes over, joining with the fuller briefing
            Set("limit-A"); m.Send(cid, "Add the signup form"); Settle(m, cid, "partner", 1);
            Check(Active(m, cid) == "partner" && Count(m, cid, "switch") == 1, "tag-team: when the first agent runs out, the other takes over by itself");
            string joinB = LastSection("B");
            Check(joinB.Contains("PROJECT STATE") && joinB.Contains("Never delete the database") && joinB.Contains("Keep it simple. Use tabs.") && joinB.Contains("Add the signup form"), "tag-team: the first time an agent joins it gets the fuller briefing and the unfinished request");
            Check(Dicts(m.History(cid)).Any(h => (string)h["Kind"] == "assistant" && h.ContainsKey("By") && (string)h["By"] == idb && ((string)h["Text"]).Contains("B worked")), "tag-team: the partner's work shows in the same chat, tagged with its vendor");
            m.PairConfigure(cid, new Dictionary<string, object> { { "PartnerModel", "opus-test" } });
            Check((string)((Dictionary<string, object>)Info(m, cid)["Partner"])["Model"] == "opus-test", "per-chat partner: the partner's model can be chosen for each chat");
            m.PairConfigure(cid, new Dictionary<string, object> { { "PartnerEffort", "high" } });
            Check((string)((Dictionary<string, object>)Info(m, cid)["Partner"])["Effort"] == "high", "per-chat partner: the partner's reasoning effort can be chosen too");
            bool badEffort = false; try { m.PairConfigure(cid, new Dictionary<string, object> { { "PartnerEffort", "ludicrous" } }); } catch (ArgumentException) { badEffort = true; }
            Check(badEffort, "per-chat partner: an unknown effort is refused");
            m.PairConfigure(cid, new Dictionary<string, object> { { "PartnerEffort", "default" } });
            bool badModel = false; try { m.PairConfigure(cid, new Dictionary<string, object> { { "PartnerModel", "x; calc" } }); } catch (ArgumentException) { badModel = true; }
            Check(badModel, "per-chat partner: a model name can't carry extra arguments");
            m.PairConfigure(cid, new Dictionary<string, object> { { "PartnerModel", "default" } });
            var wr = (Dictionary<string, object>)Info(m, cid)["Partner"];
            Check((string)wr["Mode"] == "default", "writable receiver: the partner runs in its normal permission mode, not plan mode");
            Check(m.CliArgumentPreview("claude", true).Contains("acceptEdits") && m.CliArgumentPreview("claude", false).Contains("--permission-mode plan"), "writable receiver: designer reasoning nodes stay read-only; can-edit nodes use the agent's normal mode");
            Check(((long)Convert.ToInt64(Info(m, cid)["EstTokens"])) > 0 && (string)Info(m, cid)["Currency"] == "USD" && ((string)Info(m, cid)["Preview"]).Length > 0, "cost: the next switch has an estimated size and cost, and a preview of what will be sent");

            // A resets, B runs out -> back to A, with only the new part
            Unset("limit-A"); m.ClearUsageLimit(ida); Set("limit-B"); m.Send(cid, "Now add password reset"); Settle(m, cid, "primary", 2);
            string backA = LastSection("A");
            if (!(Active(m, cid) == "primary" && backA.Contains("WHAT HAPPENED SINCE YOU LAST WORKED"))) Diag(m, cid, "A");
            Check(Active(m, cid) == "primary" && backA.Contains("WHAT HAPPENED SINCE YOU LAST WORKED") && backA.Contains("password reset") && !backA.Contains("Build the login page") && !backA.Contains("PROJECT STATE"), "tag-team: control comes back to the first agent when it has reset, and it is sent only what is new");
            // and again: A -> B, B resumes its own session with a delta
            Unset("limit-B"); m.ClearUsageLimit(idb); Set("limit-A"); m.Send(cid, "Final polish"); Settle(m, cid, "partner", 3);
            string againB = LastSection("B");
            Check(Active(m, cid) == "partner" && Count(m, cid, "switch") == 3 && againB.Contains("WHAT HAPPENED SINCE YOU LAST WORKED") && !againB.Contains("PROJECT STATE") && !againB.Contains("Never delete the database"), "tag-team: A to B to A to B works across cycles, each side resuming its own session with a delta");
            var sw = (Dictionary<string, object>)m.SwitchesGet();
            Check(Convert.ToInt32(sw["Count"]) >= 3 && Dicts(sw["Recent"]).All(x => Convert.ToInt64(x["EstTokens"]) > 0), "analytics: every switch is logged with its estimated cost");

            // both out of usage: pause, then carry on by itself
            Set("limit-B"); m.Send(cid, "Wrap up"); Until(delegate { return Count(m, cid, "pairwait") > 0 && !Busy(m, cid); }, 40000); Thread.Sleep(400);
            var waiting = Info(m, cid);
            Check(Count(m, cid, "pairwait") == 1 && (string)waiting["WaitUntilUtc"] != "", "both limited: the chat pauses and says when it will carry on");
            Unset("limit-A"); Unset("limit-B"); m.ClearUsageLimit(ida); m.ClearUsageLimit(idb);
            int assistantsBefore = Count(m, cid, "assistant"); m.PairPoll();
            Until(delegate { return Count(m, cid, "assistant") > assistantsBefore && !Busy(m, cid); }, 40000); Thread.Sleep(400);
            Check(Count(m, cid, "assistant") > assistantsBefore && (string)Info(m, cid)["WaitUntilUtc"] == "" && Dicts(m.History(cid)).Any(h => (string)h["Kind"] == "pairnote" && ((string)h["Text"]).Contains("carrying on")), "both limited: when one resets the paused work resumes without any click");

            // limited target is skipped with the reason
            Set("limit-B"); m.Send(cid, "Another one"); Settle(m, cid, "primary", 4);
            Unset("limit-B");
            bool refused = false; try { m.PairSwitchNow(cid); } catch (InvalidOperationException ex) { refused = ex.Message.Contains("out of usage"); }
            Check(refused, "tag-team: a limited agent is never switched to, and the reason is given");
        }

        // minimum gap, automatic return, resume failure
        using (var m = new HarnessManager(Path.Combine(root, "data-tag2")))
        {
            m.SaveAgent(new Dictionary<string, object> { { "Name", "TagA" }, { "Command", fakeExe }, { "Args", "--fake-tag A" }, { "Parser", "codex" } });
            m.SaveAgent(new Dictionary<string, object> { { "Name", "TagB" }, { "Command", fakeExe }, { "Args", "--fake-tag B" }, { "Parser", "codex" } });
            string ida = m.Harnesses().First(h => h.Name == "TagA").Id, idb = m.Harnesses().First(h => h.Name == "TagB").Id;
            string w3 = Path.Combine(root, "tagwork3"); Directory.CreateDirectory(w3);
            m.ContinuitySet(new Dictionary<string, object> { { "ThrashSwitches", "10" }, { "MinGapMinutes", "60" }, { "ReturnAtBoundary", "True" } });
            string cid = (string)((Dictionary<string, object>)m.Create(ida, w3, null, null, null, "gap chat"))["Id"];
            m.PairConfigure(cid, new Dictionary<string, object> { { "Mode", "automatic" }, { "PartnerHarness", idb } });
            Set("limit-A"); m.Send(cid, "first job"); Settle(m, cid, "partner", 1);
            Unset("limit-A"); m.ClearUsageLimit(ida);
            m.Send(cid, "second job"); Settle(m, cid, "partner", 1);
            Check(Active(m, cid) == "partner" && Count(m, cid, "switch") == 1, "minimum gap: an optional switch back is held off until the gap has passed (a limit switch is never delayed)");
            m.ContinuitySet(new Dictionary<string, object> { { "MinGapMinutes", "0" } });
            m.Send(cid, "third job"); Settle(m, cid, "primary", 2);
            Check(Active(m, cid) == "primary" && Dicts(m.History(cid)).Any(h => (string)h["Kind"] == "switch" && ((string)h["Detail"]).Contains("\"Reason\":\"return\"")), "automatic return: once the gap allows and the first agent has reset, the next message goes back to it");
            Set("resumefail-B"); m.PairSwitchNow(cid); Settle(m, cid, "partner", 3);
            Check(Dicts(m.History(cid)).Any(h => (string)h["Kind"] == "pairnote" && ((string)h["Text"]).Contains("Couldn't pick")) && LastSection("B").Contains("PROJECT STATE"), "resume failure: an agent that can't pick its session up is started again from HANDOFF.md");
            Check(Dicts(m.History(cid)).Any(h => (string)h["Kind"] == "assistant" && ((string)h["Text"]).Contains("B worked")), "resume failure: the work still carries on after the fallback");
        }

        // thrash guard: switching without any file changing stops by itself
        using (var m = new HarnessManager(Path.Combine(root, "data-tag3")))
        {
            m.SaveAgent(new Dictionary<string, object> { { "Name", "TagA" }, { "Command", fakeExe }, { "Args", "--fake-tag A" }, { "Parser", "codex" } });
            m.SaveAgent(new Dictionary<string, object> { { "Name", "TagB" }, { "Command", fakeExe }, { "Args", "--fake-tag B" }, { "Parser", "codex" } });
            string ida = m.Harnesses().First(h => h.Name == "TagA").Id, idb = m.Harnesses().First(h => h.Name == "TagB").Id;
            string w4 = Path.Combine(root, "tagwork4"); Directory.CreateDirectory(w4); File.WriteAllText(Path.Combine(w4, "a.txt"), "same");
            Set("nowrite"); m.ContinuitySet(new Dictionary<string, object> { { "ThrashSwitches", "2" }, { "ThrashMinutes", "30" }, { "MinGapMinutes", "0" }, { "ReturnAtBoundary", "False" } });
            string cid = (string)((Dictionary<string, object>)m.Create(ida, w4, null, null, null, "thrash chat"))["Id"];
            m.PairConfigure(cid, new Dictionary<string, object> { { "Mode", "automatic" }, { "PartnerHarness", idb } });
            Set("limit-A"); m.Send(cid, "job one"); Settle(m, cid, "partner", 1);
            Unset("limit-A"); m.ClearUsageLimit(ida); Set("limit-B"); m.Send(cid, "job two"); Settle(m, cid, "primary", 2);
            Unset("limit-B"); m.ClearUsageLimit(idb); Set("limit-A"); m.Send(cid, "job three"); Thread.Sleep(2500); Settle(m, cid, "primary", 2);
            var ti = Info(m, cid);
            Check((string)ti["Halted"] != "" && Count(m, cid, "switch") == 2 && Dicts(m.History(cid)).Any(h => (string)h["Kind"] == "pairnote" && ((string)h["Text"]).Contains("Automatic switching stopped")), "thrash guard: repeated switching with no file changing stops and asks");
            Unset("limit-A"); m.ClearUsageLimit(ida);
            m.PairSwitchNow(cid); Settle(m, cid, "partner", 3);
            Check(Active(m, cid) == "partner" && (string)Info(m, cid)["Halted"] == "", "thrash guard: Switch now clears the stop and carries on");
            Unset("nowrite");
        }
        // an agent that is not signed in gives the work back and is not tried again until the user fixes it
        using (var m = new HarnessManager(Path.Combine(root, "data-tag4")))
        {
            m.SaveAgent(new Dictionary<string, object> { { "Name", "TagA" }, { "Command", fakeExe }, { "Args", "--fake-tag A" }, { "Parser", "codex" } });
            m.SaveAgent(new Dictionary<string, object> { { "Name", "TagB" }, { "Command", fakeExe }, { "Args", "--fake-tag B" }, { "Parser", "codex" } });
            string ida = m.Harnesses().First(h => h.Name == "TagA").Id, idb = m.Harnesses().First(h => h.Name == "TagB").Id;
            string w5 = Path.Combine(root, "tagwork5"); Directory.CreateDirectory(w5);
            m.ContinuitySet(new Dictionary<string, object> { { "ThrashSwitches", "10" }, { "MinGapMinutes", "0" } });
            string cid = (string)((Dictionary<string, object>)m.Create(ida, w5, null, null, null, "auth chat"))["Id"];
            m.PairConfigure(cid, new Dictionary<string, object> { { "Mode", "automatic" }, { "PartnerHarness", idb } });
            m.Send(cid, "first job"); Settle(m, cid, "primary", 0);
            Set("autherr-B"); m.PairSwitchNow(cid); Settle(m, cid, "primary", 1);
            Check(Active(m, cid) == "primary" && Dicts(m.History(cid)).Any(h => (string)h["Kind"] == "pairnote" && ((string)h["Text"]).Contains("isn't signed in") && ((string)h["Text"]).Contains("keeps the work")), "not signed in: the hand-over is undone, the first agent keeps the work, and the chat says how to sign in");
            Check(Dicts(m.History(cid)).Count(h => (string)h["Kind"] == "assistant" && ((string)h["Text"]).Contains("A worked")) >= 2, "not signed in: the first agent carries on with the request instead of leaving it stranded");
            Set("limit-A"); m.Send(cid, "second job"); Settle(m, cid, "primary", 1); Thread.Sleep(800);
            Check(Active(m, cid) == "primary" && Dicts(m.History(cid)).Any(h => (string)h["Kind"] == "pairnote" && ((string)h["Text"]).Contains("Not switching to")), "not signed in: an agent that failed to sign in is not switched to again automatically");
            Unset("autherr-B"); Unset("limit-A"); m.ClearUsageLimit(ida);
            m.PairSwitchNow(cid); Settle(m, cid, "partner", 2);
            Check(Active(m, cid) == "partner", "not signed in: Switch now tries again once it is fixed");
            // the other agent's conversation is open in another window: keep the link, give the work back, say why
            m.PairSwitchNow(cid); Settle(m, cid, "primary", 3);
            Set("writer-B"); m.PairSwitchNow(cid); Settle(m, cid, "primary", 4);
            Check(Active(m, cid) == "primary" && Dicts(m.History(cid)).Any(h => (string)h["Kind"] == "pairnote" && ((string)h["Text"]).Contains("open somewhere else")) && !Dicts(m.History(cid)).Any(h => (string)h["Kind"] == "pairnote" && ((string)h["Text"]).Contains("Starting it again")), "open elsewhere: a conversation another window is writing to is not thrown away and replaced");
            Unset("writer-B"); m.PairSwitchNow(cid); Settle(m, cid, "partner", 5);
            Check(Active(m, cid) == "partner" && !LastSection("B").Contains("PROJECT STATE"), "open elsewhere: once it is closed there, the same conversation is picked up again (not a new one)");
        }
        // closed mid-turn: the chat says so when it opens again
        {
            string dd = Path.Combine(root, "data-busy"); string wb = Path.Combine(root, "busywork"); Directory.CreateDirectory(wb); string bid;
            using (var m = new HarnessManager(dd)) bid = (string)((Dictionary<string, object>)m.Create(m.Harnesses().First(h => h.Id == "codex" || h.Id == "claude").Id, wb, null, null, null, "busy chat"))["Id"];
            string file = Path.Combine(dd, "harness", "sessions", bid + ".json"); File.WriteAllText(file, File.ReadAllText(file).Replace("\"Busy\":false", "\"Busy\":true"));
            using (var m = new HarnessManager(dd)) Check(Dicts(m.History(bid)).Any(h => (string)h["Kind"] == "pairnote" && ((string)h["Text"]).StartsWith("LAICA was closed while this was working")), "closed mid-turn: the chat says so when it opens again");
        }        Console.WriteLine(failures == 0 ? "tag-team checks passed" : failures + " tag-team checks FAILED");
        return failures;
    }
}
