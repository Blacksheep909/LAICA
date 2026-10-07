using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Laica
{
    /// <summary>Team mode: a leader agent plans, teammates work in parallel, the leader reviews and writes the final answer.</summary>
    public sealed partial class HarnessManager
    {
        List<Dictionary<string, object>> teams = new List<Dictionary<string, object>>();
        readonly Dictionary<string, CancellationTokenSource> teamRuns = new Dictionary<string, CancellationTokenSource>();

        Dictionary<string, object> TeamDto(Dictionary<string, object> t)
        {
            var d = new Dictionary<string, object>(t); lock (gate) d["Running"] = teamRuns.ContainsKey(Str(t, "Id")); return d;
        }
        public object Teams() { lock (gate) return teams.Select(TeamDto).OrderByDescending(x => Str(x, "UpdatedUtc")).ToArray(); }
        void SaveTeams() { SaveList("teams.json", teams); }

        // ---------- team analytics: how efficient each team design is ----------
        sealed class RunSess { public string Sid, Name, Role, Harness, Model; public DateTime Created; public double Seconds; public bool Leader { get { return Name == ""; } } }
        readonly Dictionary<string, List<RunSess>> runSess = new Dictionary<string, List<RunSess>>();
        List<Dictionary<string, object>> teamRunLog;

        void RunTime(string sid, double add, bool leaderAsk)
        {
            lock (gate) foreach (var l in runSess.Values) { var r = l.FirstOrDefault(x => x.Sid == sid); if (r == null) continue; if (leaderAsk) r.Seconds += add; else if (!r.Leader) r.Seconds = (DateTime.UtcNow - r.Created).TotalSeconds; }
        }

        /// <summary>Writes one finished run (who did what, how long, tokens, estimated cost, whether agents had to be swapped) to the team run log.</summary>
        void RecordTeamRun(Dictionary<string, object> t, DateTime startedUtc, string outcome)
        {
            string tid = Str(t, "Id"); LoadVerifiedOnce(); var pr = Prices(); var members = new List<object>(); var counts = new Dictionary<string, int>();
            lock (gate)
            {
                List<RunSess> rs; if (!runSess.TryGetValue(tid, out rs)) return; runSess.Remove(tid);
                var tasks = Arr(t, "Tasks") ?? new object[0];
                foreach (var r in rs)
                {
                    Session s; if (!sessions.TryGetValue(r.Sid, out s)) continue;
                    string name = r.Leader ? "Leader" : r.Name; int n; counts.TryGetValue(name, out n); counts[name] = n + 1;
                    string model = !String.IsNullOrEmpty(s.Model) ? s.Model : (r.Model != "" ? r.Model : r.Harness);
                    double cost = CostOf(PriceFor(model), new long[] { 0, s.TIn, s.TOut, s.TCr, s.TCw });
                    string status = "done"; if (!r.Leader) foreach (object o in tasks) { var d = o as Dictionary<string, object>; if (d != null && Str(d, "SessionId") == r.Sid && Str(d, "Status") == "failed") status = "failed"; }
                    members.Add(new Dictionary<string, object> { { "Name", name }, { "Role", r.Role }, { "Harness", r.Harness }, { "Model", model }, { "Seconds", Math.Round(r.Seconds) }, { "Tokens", s.TIn + s.TOut + s.TCr + s.TCw }, { "Output", s.TOut }, { "Cost", Math.Round(cost, 4) }, { "Status", status } });
                }
            }
            if (members.Count == 0) return;
            int swaps = counts.Values.Sum(c => Math.Max(0, c - 1));
            var rec = new Dictionary<string, object> { { "TeamId", tid }, { "Title", Str(t, "Title") }, { "Goal", Str(t, "Goal") }, { "StartUtc", startedUtc.ToString("o") }, { "Seconds", Math.Round((DateTime.UtcNow - startedUtc).TotalSeconds) }, { "Outcome", outcome }, { "Swaps", swaps }, { "Members", members.ToArray() } };
            lock (gate) { if (teamRunLog == null) teamRunLog = LoadList("teamruns.json"); teamRunLog.Add(rec); if (teamRunLog.Count > 500) teamRunLog.RemoveRange(0, teamRunLog.Count - 500); SaveList("teamruns.json", teamRunLog); }
        }

        public object TeamAnalytics(string range)
        {
            DateTime cut = range == "7d" ? DateTime.UtcNow.AddDays(-7) : range == "30d" ? DateTime.UtcNow.AddDays(-30) : DateTime.MinValue; List<Dictionary<string, object>> runs;
            lock (gate) { if (teamRunLog == null) teamRunLog = LoadList("teamruns.json"); runs = teamRunLog.Where(r => { DateTime d; return !DateTime.TryParse(Str(r, "StartUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out d) || d >= cut; }).ToList(); }
            Func<object, double> num = o => { try { return Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { return 0; } };
            var teamsOut = new List<object>();
            foreach (var g in runs.GroupBy(r => Str(r, "TeamId")))
            {
                var list = g.ToList(); int ok = list.Count(r => Str(r, "Outcome") == "done"), failed = list.Count(r => Str(r, "Outcome") == "failed"), stopped = list.Count(r => Str(r, "Outcome") == "stopped");
                var all = list.SelectMany(r => Arr(r, "Members").Cast<Dictionary<string, object>>().Select(m => new { Run = r, M = m })).ToList();
                double cost = all.Sum(x => num(x.M["Cost"])), tokens = all.Sum(x => num(x.M["Tokens"])), wall = list.Sum(r => num(r["Seconds"])), busy = all.Sum(x => num(x.M["Seconds"]));
                double leaderCost = all.Where(x => Str(x.M, "Name") == "Leader").Sum(x => num(x.M["Cost"])), leaderTok = all.Where(x => Str(x.M, "Name") == "Leader").Sum(x => num(x.M["Tokens"]));
                int tasksDone = all.Count(x => Str(x.M, "Name") != "Leader" && Str(x.M, "Status") == "done"), tasksAll = all.Count(x => Str(x.M, "Name") != "Leader");
                var mem = all.GroupBy(x => Str(x.M, "Name")).Select(mg => { var l = mg.ToList(); double mc = l.Sum(x => num(x.M["Cost"])); return (object)new Dictionary<string, object> {
                    { "Name", mg.Key }, { "Role", Str(l[0].M, "Role") }, { "Harness", Str(l[l.Count - 1].M, "Harness") }, { "Model", Str(l[l.Count - 1].M, "Model") }, { "Runs", l.Count }, { "Seconds", Math.Round(l.Average(x => num(x.M["Seconds"]))) },
                    { "Tokens", l.Sum(x => num(x.M["Tokens"])) }, { "Cost", Math.Round(mc, 2) }, { "Share", cost > 0 ? Math.Round(mc * 100 / cost, 1) : 0 }, { "Failed", l.Count(x => Str(x.M, "Status") == "failed") } }; }).OrderByDescending(m => num(((Dictionary<string, object>)m)["Cost"])).ToArray();
                var latest = list.OrderByDescending(r => Str(r, "StartUtc")).First();
                teamsOut.Add(new Dictionary<string, object> {
                    { "TeamId", g.Key }, { "Title", Str(latest, "Title") }, { "Runs", list.Count }, { "Succeeded", ok }, { "Failed", failed }, { "Stopped", stopped }, { "SuccessRate", list.Count - stopped > 0 ? Math.Round(ok * 100.0 / (list.Count - stopped)) : 0 },
                    { "AvgSeconds", Math.Round(wall / list.Count) }, { "AvgCost", Math.Round(cost / list.Count, 2) }, { "TotalCost", Math.Round(cost, 2) }, { "TotalTokens", tokens },
                    { "Speedup", wall > 0 ? Math.Round(busy / wall, 2) : 0 }, { "LeaderShare", cost > 0 ? Math.Round(leaderCost * 100 / cost, 1) : (tokens > 0 ? Math.Round(leaderTok * 100 / tokens, 1) : 0) },
                    { "CostPerTask", tasksDone > 0 ? Math.Round(cost / tasksDone, 2) : 0 }, { "TaskSuccess", tasksAll > 0 ? Math.Round(tasksDone * 100.0 / tasksAll) : 0 }, { "Swaps", list.Sum(r => (int)num(r["Swaps"])) },
                    { "Vendors", all.Select(x => Str(x.M, "Harness")).Where(h => h != "").Distinct().ToArray() }, { "Members", mem },
                    { "Recent", list.OrderByDescending(r => Str(r, "StartUtc")).Take(6).Select(r => (object)new Dictionary<string, object> { { "Goal", Str(r, "Goal") }, { "StartUtc", Str(r, "StartUtc") }, { "Seconds", num(r["Seconds"]) }, { "Outcome", Str(r, "Outcome") }, { "Cost", Math.Round(Arr(r, "Members").Cast<Dictionary<string, object>>().Sum(m => num(m["Cost"])), 2) }, { "Swaps", num(r["Swaps"]) } }).ToArray() } });
            }
            var sorted = teamsOut.Cast<Dictionary<string, object>>().OrderByDescending(x => num(x["Runs"])).ToArray();
            return new Dictionary<string, object> { { "Range", range }, { "Runs", runs.Count }, { "Cost", Math.Round(runs.Sum(r => Arr(r, "Members").Cast<Dictionary<string, object>>().Sum(m => num(m["Cost"]))), 2) }, { "Teams", sorted } };
        }

        public object SaveTeam(Dictionary<string, object> t)
        {
            string title = Str(t, "Title").Trim(), cwd = Str(t, "Cwd").Trim();
            if (title == "") throw new ArgumentException("Give the team a name.");
            if (cwd == "" || !System.IO.Directory.Exists(cwd)) throw new ArgumentException("Choose an existing working folder.");
            var leader = Obj(t, "Leader"); if (leader == null || Str(leader, "Harness") == "") throw new ArgumentException("Choose an agent for the leader.");
            var members = Arr(t, "Members"); var list = new List<object>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var all = Harnesses();
            if (all.All(h => h.Id != Str(leader, "Harness"))) throw new ArgumentException("The leader's agent isn't available.");
            if (members != null) foreach (object o in members)
                {
                    var m = o as Dictionary<string, object>; if (m == null) continue;
                    string name = Str(m, "Name").Trim(); if (name == "") throw new ArgumentException("Every teammate needs a name.");
                    if (!names.Add(name)) throw new ArgumentException("Teammate names must be different: " + name);
                    if (all.All(h => h.Id != Str(m, "Harness"))) throw new ArgumentException(name + "'s agent isn't available.");
                    list.Add(new Dictionary<string, object> { { "Name", name }, { "Role", Str(m, "Role").Trim() }, { "Harness", Str(m, "Harness") }, { "Mode", Str(m, "Mode") }, { "ServiceId", Str(m, "ServiceId") }, { "Model", Str(m, "Model") } });
                }
            if (list.Count == 0) throw new ArgumentException("Add at least one teammate.");
            if (list.Count > 8) throw new ArgumentException("A team can have up to 8 teammates.");
            string id = Str(t, "Id"); if (id == "") id = Guid.NewGuid().ToString("N");
            Dictionary<string, object> old; lock (gate) old = teams.FirstOrDefault(x => Str(x, "Id") == id);
            if (old != null && teamRuns.ContainsKey(id)) throw new InvalidOperationException("Stop the team before editing it.");
            var rec = new Dictionary<string, object> {
                { "Id", id }, { "Title", title }, { "Cwd", System.IO.Path.GetFullPath(cwd) }, { "Isolate", Str(t, "Isolate") == "True" },
                { "Leader", new Dictionary<string, object> { { "Harness", Str(leader, "Harness") }, { "Mode", Str(leader, "Mode") }, { "ServiceId", Str(leader, "ServiceId") }, { "Model", Str(leader, "Model") } } },
                { "Members", list.ToArray() }, { "Handoff", t.ContainsKey("Handoff") && t["Handoff"] is Dictionary<string, object> ? CleanHandoff((Dictionary<string, object>)t["Handoff"], true) : (old != null && old.ContainsKey("Handoff") ? old["Handoff"] : null) }, { "Goal", old != null ? Str(old, "Goal") : "" }, { "Phase", old != null ? Str(old, "Phase") : "idle" }, { "Result", old != null ? Str(old, "Result") : "" },
                { "Tasks", old != null && old.ContainsKey("Tasks") ? old["Tasks"] : new object[0] }, { "LeaderSessionId", old != null ? Str(old, "LeaderSessionId") : "" }, { "UpdatedUtc", DateTime.UtcNow.ToString("o") } };
            lock (gate) { teams.RemoveAll(x => Str(x, "Id") == id); teams.Add(rec); SaveTeams(); }
            Raise(); return TeamDto(rec);
        }

        public void DeleteTeam(string id) { try { StopTeam(id); } catch (Exception) { } lock (gate) { teams.RemoveAll(x => Str(x, "Id") == id); SaveTeams(); } Raise(); }

        public void StopTeam(string id)
        {
            CancellationTokenSource c; List<string> ids = new List<string>();
            lock (gate)
            {
                teamRuns.TryGetValue(id, out c); var t = teams.FirstOrDefault(x => Str(x, "Id") == id);
                if (t != null) { if (Str(t, "LeaderSessionId") != "") ids.Add(Str(t, "LeaderSessionId")); var tasks = Arr(t, "Tasks"); if (tasks != null) foreach (object o in tasks) { var d = o as Dictionary<string, object>; if (d != null && Str(d, "SessionId") != "") ids.Add(Str(d, "SessionId")); } }
            }
            if (c != null) c.Cancel();
            foreach (string sid in ids) { try { Stop(sid); } catch (Exception) { } }
        }

        public void RunTeam(string id, string goal)
        {
            if (String.IsNullOrWhiteSpace(goal)) throw new ArgumentException("Describe the goal for the team.");
            Dictionary<string, object> t;
            lock (gate) { t = teams.FirstOrDefault(x => Str(x, "Id") == id); if (t == null) throw new ArgumentException("That team no longer exists."); if (teamRuns.ContainsKey(id)) throw new InvalidOperationException("This team is already working."); }
            var cts = new CancellationTokenSource(); var startedUtc = DateTime.UtcNow; lock (gate) { teamRuns[id] = cts; runSess[id] = new List<RunSess>(); }
            lock (gate) { t["Goal"] = goal.Trim(); t["Result"] = ""; t["Note"] = ""; t["HandoffTo"] = ""; t["Paused"] = false; t["Tasks"] = new object[0]; t["Phase"] = "planning"; t["UpdatedUtc"] = DateTime.UtcNow.ToString("o"); SaveTeams(); }
            Raise();
            new Thread(() =>
            {
                string error = null;
                try { TeamWork(t, goal.Trim(), cts.Token); }
                catch (OperationCanceledException) { error = "Stopped."; }
                catch (Exception ex) { error = Unwrap(ex); }
                var finished = Completed;
                try { RecordTeamRun(t, startedUtc, error == null ? "done" : (error == "Stopped." ? "stopped" : "failed")); } catch (Exception) { }
                lock (gate) { teamRuns.Remove(id); t["Paused"] = false; t["Phase"] = error == null ? "done" : (error == "Stopped." ? "stopped" : "failed"); if (error != null && Str(t, "Result") == "") t["Result"] = error == "Stopped." ? "" : "The team stopped: " + error; t["UpdatedUtc"] = DateTime.UtcNow.ToString("o"); SaveTeams(); }
                if (error != null && error != "Stopped.") { try { TeamRanOut(t, error); } catch (Exception) { } }
                if (finished != null && error != "Stopped.") finished("Team finished: " + Str(t, "Title"), Str(t, "Result"));
                Raise();
            }) { IsBackground = true, Name = "team-" + id }.Start();
        }

        string SessionFor(Dictionary<string, object> t, Dictionary<string, object> spec, string title, bool isolate)
        {
            var d = (Dictionary<string, object>)Create(Str(spec, "Harness"), Str(t, "Cwd"), Str(spec, "Mode") == "" ? null : Str(spec, "Mode"), null, null, title, isolate, Str(spec, "ServiceId") == "" ? null : Str(spec, "ServiceId"), Str(spec, "Model") == "" ? null : Str(spec, "Model"));
            string newId = Str(d, "Id"); lock (gate) { sessions[newId].TeamId = Str(t, "Id"); List<RunSess> rs; if (runSess.TryGetValue(Str(t, "Id"), out rs)) rs.Add(new RunSess { Sid = newId, Name = Str(spec, "Name"), Role = Str(spec, "Role"), Harness = Str(spec, "Harness"), Model = Str(spec, "Model"), Created = DateTime.UtcNow }); } SaveSession(sessions[newId]); return newId;
        }

        /// <summary>Sends a prompt, waits for the turn to finish and returns the agent's final text for that turn.</summary>
        string AskAndWait(string sessionId, string prompt, CancellationToken token)
        {
            int start; lock (gate) start = Get(sessionId).Events.Count;
            var began = DateTime.UtcNow; Send(sessionId, prompt); HonourTeamPause(sessionId);
            try { WaitIdle(sessionId, token); } finally { RunTime(sessionId, (DateTime.UtcNow - began).TotalSeconds, true); }
            return TurnText(sessionId, start);
        }
        void WaitIdle(string sessionId, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                bool busy; lock (gate) { Session s; busy = sessions.TryGetValue(sessionId, out s) && s.Busy; }
                if (!busy) return; Thread.Sleep(150);
            }
        }
        string TurnText(string sessionId, int start)
        {
            var sb = new StringBuilder(); string error = null;
            lock (gate)
            {
                var ev = Get(sessionId).Events; Dictionary<string, object> prev = null;
                for (int i = start; i < ev.Count; i++)
                {
                    var e = ev[i]; string kind = Str(e, "Kind");
                    if (kind == "assistant") { if (Str(e, "Detail") == "append" && prev != null && Str(prev, "Kind") == "assistant") sb.Append("\n"); else if (sb.Length > 0) sb.Append("\n\n"); sb.Append(Str(e, "Text")); }
                    else if (kind == "error") error = Str(e, "Text");
                    prev = e;
                }
            }
            string text = sb.ToString().Trim();
            if (error != null && (text == "" || IsLimitText(error))) throw new InvalidOperationException(error);
            return text;
        }

        void SetTeam(Dictionary<string, object> t, string phase)
        {
            lock (gate) { if (phase != null) t["Phase"] = phase; t["UpdatedUtc"] = DateTime.UtcNow.ToString("o"); SaveTeams(); }
            Raise();
        }

        void TeamWork(Dictionary<string, object> t, string goal, CancellationToken token)
        {
            var leaderSpec = Obj(t, "Leader"); var members = Arr(t, "Members").Cast<Dictionary<string, object>>().ToList();
            string title = Str(t, "Title");
            string leaderId; lock (gate) { leaderId = Str(t, "LeaderSessionId"); if (leaderId == "" || !sessions.ContainsKey(leaderId)) leaderId = ""; }
            if (leaderId == "") { leaderId = SessionFor(t, leaderSpec, "Team · " + title + " · Leader", false); lock (gate) t["LeaderSessionId"] = leaderId; }
            var roster = String.Join("\n", members.Select(m => "- " + Str(m, "Name") + (Str(m, "Role") != "" ? " — " + Str(m, "Role") : "")));
            string plan = AskLeader(t, ref leaderSpec, ref leaderId,
                "You are the leader of a small team of AI agents working on a shared project folder.\n\nGOAL:\n" + goal + "\n\nTEAMMATES:\n" + roster +
                "\n\nSplit the goal into independent tasks, normally one per teammate. Teammates work at the same time and cannot see each other's work, so each task must be complete and self-contained. " +
                "Reply with ONLY a JSON object, no other text, shaped like: {\"tasks\":[{\"assignee\":\"<exact teammate name>\",\"title\":\"short title\",\"prompt\":\"complete instructions\"}]}. Do not change any files in this step.", token);
            var tasks = ParsePlan(plan, members);
            if (tasks.Count == 0) { lock (gate) t["Result"] = "The leader did not produce a usable plan, so no work was started. Its reply:\n\n" + plan; return; }
            var records = tasks.Select(x => (object)new Dictionary<string, object> { { "Assignee", Str(x, "Assignee") }, { "Title", Str(x, "Title") }, { "SessionId", "" }, { "Status", "waiting" }, { "Output", "" } }).ToArray();
            lock (gate) t["Tasks"] = records; SetTeam(t, "working");
            bool isolate = Str(t, "Isolate") == "True" && GitTools.Root(Str(t, "Cwd")) != null;
            var starts = new List<KeyValuePair<Dictionary<string, object>, int>>();
            var intros = new Dictionary<Dictionary<string, object>, string>(); var specs = new Dictionary<Dictionary<string, object>, Dictionary<string, object>>();
            for (int i = 0; i < tasks.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var spec = members.First(m => String.Equals(Str(m, "Name"), Str(tasks[i], "Assignee"), StringComparison.OrdinalIgnoreCase));
                string origKey = KeyOfSpec(spec); spec = PickAvailable(t, spec); string sid = SessionFor(t, spec, "Team · " + title + " · " + Str(spec, "Name"), isolate);
                var rec = (Dictionary<string, object>)records[i]; int start; lock (gate) { rec["SessionId"] = sid; rec["Status"] = "working"; start = Get(sid).Events.Count; }
                starts.Add(new KeyValuePair<Dictionary<string, object>, int>(rec, start)); SetTeam(t, null);
                string intro = "You are " + Str(spec, "Name") + (Str(spec, "Role") != "" ? ", the team's " + Str(spec, "Role") : "") + ", one member of a team working toward this goal:\n" + goal + "\n\nYOUR TASK:\n" + Str(tasks[i], "Prompt") + "\n\nWork only on your task. When done, reply with a short report of what you did, which files you changed and anything the leader should check.";
                intros[rec] = intro; specs[rec] = spec; if (KeyOfSpec(spec) != origKey) lock (gate) rec["Note"] = "Started on " + SpecName(spec) + " because the planned agent is out of usage."; Send(sid, intro); HonourTeamPause(sid);
            }
            foreach (var kv in starts)
            {
                string sid = Str(kv.Key, "SessionId");
                try { MemberWait(t, kv.Key, kv.Value, specs, intros, isolate, token); }
                finally { SetTeam(t, null); }
            }
            SetTeam(t, "reviewing");
            var report = new StringBuilder("Your teammates have finished. Their reports:\n");
            foreach (var kv in starts) report.Append("\n### " + Str(kv.Key, "Assignee") + " — " + Str(kv.Key, "Title") + " (" + Str(kv.Key, "Status") + ")\n" + Str(kv.Key, "Output") + "\n");
            report.Append("\nReview the results against the goal. Check the work if you need to, resolve conflicts, and write the final answer for the user: what was done, what changed, and anything unresolved.");
            string final = AskLeader(t, ref leaderSpec, ref leaderId, report.ToString(), token);
            lock (gate) t["Result"] = final;
        }

        /// <summary>Waits for one teammate. If its agent runs out of usage the same task is handed to another agent that still has some.</summary>
        void MemberWait(Dictionary<string, object> t, Dictionary<string, object> rec, int start, Dictionary<Dictionary<string, object>, Dictionary<string, object>> specs, Dictionary<Dictionary<string, object>, string> intros, bool isolate, CancellationToken token)
        {
            var spec = specs[rec]; var tried = new HashSet<string> { KeyOfSpec(spec) }; string sid = Str(rec, "SessionId"), text = "", status = "done";
            while (true)
            {
                WaitIdle(sid, token); RunTime(sid, 0, false);
                try { text = TurnText(sid, start); status = "done"; break; }
                catch (InvalidOperationException ex)
                {
                    text = "Failed: " + ex.Message; status = "failed";
                    if (!IsLimitText(ex.Message)) break;
                    var next = Fallback(t, spec, tried);
                    if (next == null) { text += " (no other agent has usage left)"; break; }
                    tried.Add(KeyOfSpec(next));
                    string note = "Switched from " + SpecName(spec) + " to " + SpecName(next) + " because it ran out of usage.";
                    sid = SessionFor(t, next, "Team - " + Str(t, "Title") + " - " + Str(next, "Name"), isolate);
                    lock (gate) { rec["SessionId"] = sid; rec["Note"] = note; rec["Status"] = "working"; start = Get(sid).Events.Count; }
                    spec = next; SetTeam(t, null);
                    Send(sid, intros[rec] + "\n\nNOTE: a previous teammate started this task but ran out of usage. Inspect the project folder for work already done and continue it rather than starting over."); HonourTeamPause(sid);
                }
            }
            lock (gate) { rec["Output"] = text; rec["Status"] = status; }
        }

        /// <summary>Asks the team leader. If the leader's agent is out of usage a different agent takes over as leader.</summary>
        string AskLeader(Dictionary<string, object> t, ref Dictionary<string, object> spec, ref string leaderId, string prompt, CancellationToken token)
        {
            var tried = new HashSet<string> { KeyOfSpec(spec) }; string ask = prompt;
            if (IsLimited(KeyOfSpec(spec)))
            {
                var pre = Fallback(t, spec, tried);
                if (pre != null) { tried.Add(KeyOfSpec(pre)); spec = pre; leaderId = SessionFor(t, spec, "Team - " + Str(t, "Title") + " - Leader", false); lock (gate) { t["LeaderSessionId"] = leaderId; t["Note"] = "Leader is " + SpecName(spec) + " because the planned leader is out of usage."; } ask = LeaderHandover(t, prompt); }
            }
            while (true)
            {
                try { return AskAndWait(leaderId, ask, token); }
                catch (InvalidOperationException ex)
                {
                    if (!IsLimitText(ex.Message)) throw;
                    var next = Fallback(t, spec, tried);
                    if (next == null) throw new InvalidOperationException("Every agent available to this team is out of usage. Try again after the limits reset, or run the goal on a different team. (" + ex.Message + ")");
                    tried.Add(KeyOfSpec(next)); string from = SpecName(spec); spec = next;
                    leaderId = SessionFor(t, spec, "Team - " + Str(t, "Title") + " - Leader", false);
                    lock (gate) { t["LeaderSessionId"] = leaderId; t["Note"] = "Leader switched from " + from + " to " + SpecName(spec) + " because it ran out of usage."; }
                    SetTeam(t, null); ask = LeaderHandover(t, prompt);
                }
            }
        }
        static string LeaderHandover(Dictionary<string, object> t, string prompt) { return "You are taking over as leader of this team from an agent that ran out of usage.\n\nGOAL:\n" + Str(t, "Goal") + "\n\n" + prompt; }
        List<Dictionary<string, object>> ParsePlan(string text, List<Dictionary<string, object>> members)
        {
            var result = new List<Dictionary<string, object>>();
            int a = text.IndexOf('{'), b = text.LastIndexOf('}'); if (a < 0 || b <= a) return result;
            try
            {
                var o = json.Deserialize<Dictionary<string, object>>(text.Substring(a, b - a + 1)); var arr = Arr(o, "tasks"); if (arr == null) return result;
                foreach (object x in arr)
                {
                    var d = x as Dictionary<string, object>; if (d == null) continue;
                    var m = members.FirstOrDefault(mm => String.Equals(Str(mm, "Name"), Str(d, "assignee").Trim(), StringComparison.OrdinalIgnoreCase)); if (m == null || Str(d, "prompt").Trim() == "") continue;
                    var existing = result.FirstOrDefault(r => String.Equals(Str(r, "Assignee"), Str(m, "Name"), StringComparison.OrdinalIgnoreCase));
                    if (existing != null) { existing["Prompt"] = Str(existing, "Prompt") + "\n\nAlso: " + Str(d, "prompt"); existing["Title"] = Str(existing, "Title") + " + " + Str(d, "title"); }
                    else result.Add(new Dictionary<string, object> { { "Assignee", Str(m, "Name") }, { "Title", Str(d, "title") == "" ? "Task" : Str(d, "title") }, { "Prompt", Str(d, "prompt") } });
                }
            }
            catch (Exception) { }
            return result;
        }
    }
}
