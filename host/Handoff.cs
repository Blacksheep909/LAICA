using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Laica
{
    /// <summary>
    /// Workflow handoff: what happens when a vendor runs out of usage.
    ///   manual : nothing moves by itself; the chat or team shows the choice.
    ///   team   : the work moves to a backup team that does not rely on the vendor that ran out.
    ///   agent  : the work moves to one agent.
    /// A setting applies to everything; a team can override it. Work never bounces more than twice, except in a tag-team chat (see TagTeam.cs),
    /// which swaps back and forth for as long as the thrash guard sees real progress. The "continuity" settings for that live in the same handoff.json.
    /// </summary>
    public sealed partial class HarnessManager
    {
        Dictionary<string, object> handoff = new Dictionary<string, object> { { "Mode", "manual" }, { "TeamId", "" }, { "Harness", "" }, { "ServiceId", "" }, { "Model", "" } };
        readonly Dictionary<string, int> teamHandoffDepth = new Dictionary<string, int>();
        static readonly string[] HandoffModes = { "manual", "team", "agent" };

        void LoadHandoff() { var l = LoadList("handoff.json"); if (l.Count > 0 && HandoffModes.Contains(Str(l[0], "Mode"))) handoff = l[0]; }

        public object HandoffGet() { lock (gate) return new Dictionary<string, object>(handoff); }

        Dictionary<string, object> CleanHandoff(Dictionary<string, object> d, bool allowInherit)
        {
            string mode = Str(d, "Mode"); if (allowInherit && mode == "inherit") return new Dictionary<string, object> { { "Mode", "inherit" } };
            if (!HandoffModes.Contains(mode)) throw new ArgumentException("Choose manual, a backup team, or a single agent.");
            var r = new Dictionary<string, object> { { "Mode", mode }, { "TeamId", "" }, { "Harness", "" }, { "ServiceId", "" }, { "Model", "" } };
            if (mode == "team") { string id = Str(d, "TeamId"); lock (gate) if (teams.All(t => Str(t, "Id") != id)) throw new ArgumentException("That backup team no longer exists."); r["TeamId"] = id; }
            if (mode == "agent") { string h = Str(d, "Harness"); if (!Harnesses().Any(x => x.Id == h && x.Available)) throw new ArgumentException("That agent isn't available."); r["Harness"] = h; r["ServiceId"] = Str(d, "ServiceId"); r["Model"] = Str(d, "Model"); }
            return r;
        }

        public object HandoffSet(Dictionary<string, object> d)
        {
            var clean = CleanHandoff(d, false); lock (gate) { if (handoff.ContainsKey("Continuity")) clean["Continuity"] = handoff["Continuity"]; handoff = clean; SaveList("handoff.json", new List<Dictionary<string, object>> { clean }); }
            Raise(); return HandoffGet();
        }

        Dictionary<string, object> EffectiveHandoff(string teamId)
        {
            lock (gate)
            {
                if (!String.IsNullOrEmpty(teamId)) { var t = teams.FirstOrDefault(x => Str(x, "Id") == teamId); var h = t != null ? Obj(t, "Handoff") : null; if (h != null && HandoffModes.Contains(Str(h, "Mode"))) return h; }
                return handoff;
            }
        }

        HashSet<string> TeamVendors(Dictionary<string, object> t)
        {
            var set = new HashSet<string>(); var leader = Obj(t, "Leader"); if (leader != null) set.Add(KeyOfSpec(leader));
            var mem = Arr(t, "Members"); if (mem != null) foreach (object o in mem) { var d = o as Dictionary<string, object>; if (d != null) set.Add(KeyOfSpec(d)); }
            return set;
        }

        /// <summary>A backup team can take over only if none of its agents is the vendor that ran out, or any other vendor that is out right now.</summary>
        bool TeamUsable(Dictionary<string, object> team, string exhaustedKey) { return TeamVendors(team).All(k => k != exhaustedKey && !IsLimited(k)); }

        string TakeoverText(Session s, string from)
        {
            var turns = new List<string>(); string lastUser = "";
            lock (gate) foreach (var e in s.Events)
                {
                    string k = Str(e, "Kind"); if (k == "user") { lastUser = Str(e, "Text"); turns.Add("User: " + Clip(Str(e, "Text"), 2500)); }
                    else if (k == "assistant") turns.Add("Assistant: " + Clip(Str(e, "Text"), 2500));
                }
            var sb = new StringBuilder(); int budget = 12000; for (int i = turns.Count - 1; i >= 0 && budget > 0; i--) { budget -= turns[i].Length; sb.Insert(0, turns[i] + "\n\n"); }
            return "You are taking over work from " + from + ", which ran out of usage. The working folder may already contain partial changes: inspect it before editing and do not redo finished work.\n\nCONVERSATION SO FAR:\n" + sb.ToString().Trim() + "\n\n---\n\nFinish this request: " + lastUser;
        }

        /// <summary>A chat just hit a usage limit: move it on by itself if the setting says so.</summary>
        void AutoHandoffChat(Session s, string exhaustedKey)
        {
            if (!String.IsNullOrEmpty(s.TeamId)) return;
            var cfg = EffectiveHandoff(""); string mode = Str(cfg, "Mode"); if (mode == "manual") return;
            lock (gate) { if (s.HandoffDone) return; s.HandoffDone = true; }
            if (s.HandoffDepth >= 2) { Emit(s, "notice", "Automatic handoff paused: this work has already moved twice. Choose where it should continue.", null); return; }
            string from = VendorName(exhaustedKey);
            if (mode == "agent")
            {
                string h = Str(cfg, "Harness"), svc = Str(cfg, "ServiceId"); string key = h == "laica" && svc != "" ? "api:" + svc : h;
                if (key == exhaustedKey || IsLimited(key) || !Harnesses().Any(x => x.Id == h && x.Available)) { Emit(s, "notice", "Automatic handoff skipped: the agent you chose is not available right now (it may be out of usage too).", null); return; }
                var d = (Dictionary<string, object>)ContinueElsewhere(s.Id, h, svc, Str(cfg, "Model")); string newId = Str(d, "Id");
                lock (gate) { Session ns; if (sessions.TryGetValue(newId, out ns)) ns.HandoffDepth = s.HandoffDepth + 1; }
                Emit(s, "handoff", "Continued automatically on " + VendorName(key) + " because " + from + " ran out of usage.", newId);
            }
            else
            {
                Dictionary<string, object> team; lock (gate) team = teams.FirstOrDefault(x => Str(x, "Id") == Str(cfg, "TeamId"));
                if (team == null || !TeamUsable(team, exhaustedKey)) { Emit(s, "notice", "Automatic handoff skipped: the backup team uses a vendor that is out of usage. Pick a team that doesn't.", null); return; }
                string tid = Str(team, "Id"); lock (gate) { int dd; teamHandoffDepth.TryGetValue(tid, out dd); teamHandoffDepth[tid] = Math.Max(dd, s.HandoffDepth + 1); }
                try { RunTeam(tid, TakeoverText(s, from)); }
                catch (Exception ex) { Emit(s, "notice", "Automatic handoff couldn't start the backup team: " + ex.Message, null); return; }
                Emit(s, "handoff", "Handed to the team " + Str(team, "Title") + " because " + from + " ran out of usage.", "team:" + tid);
            }
            Raise();
        }

        static string TaskProgress(Dictionary<string, object> t)
        {
            var sb = new StringBuilder(); var tasks = Arr(t, "Tasks");
            if (tasks != null) foreach (object o in tasks) { var d = o as Dictionary<string, object>; if (d == null) continue; if (Str(d, "Output") != "" && Str(d, "Status") == "done") sb.AppendLine("- " + Str(d, "Assignee") + " (" + Str(d, "Title") + "): " + Clip(Str(d, "Output"), 900)); }
            return sb.Length == 0 ? "(nothing finished yet)" : sb.ToString();
        }

        /// <summary>Moves a team's goal, with what has been done so far, to a backup team or one agent. Used automatically and by hand.</summary>
        public object HandoffTeam(string teamId, Dictionary<string, object> choice)
        {
            Dictionary<string, object> t; lock (gate) { t = teams.FirstOrDefault(x => Str(x, "Id") == teamId); if (t == null) throw new ArgumentException("That team no longer exists."); if (teamRuns.ContainsKey(teamId)) throw new InvalidOperationException("Stop the team before moving its work."); }
            var cfg = choice != null ? CleanHandoff(choice, false) : EffectiveHandoff(teamId); string mode = Str(cfg, "Mode");
            if (mode == "manual") throw new InvalidOperationException("Choose a backup team or an agent to continue with.");
            int depth; lock (gate) teamHandoffDepth.TryGetValue(teamId, out depth);
            if (depth >= 2 && choice == null) { lock (gate) t["Note"] = "Automatic handoff paused: this work has already moved twice."; SetTeam(t, null); return false; }
            string goal = Str(t, "Goal"); if (goal == "") throw new InvalidOperationException("This team has no goal to move.");
            var exhausted = TeamVendors(t).Where(IsLimited).ToList(); string fromText = exhausted.Count > 0 ? String.Join(" and ", exhausted.Select(VendorName)) : "an agent";
            string moved = goal + "\n\nThe team \"" + Str(t, "Title") + "\" ran out of usage part-way (" + fromText + "). Work reported so far:\n" + TaskProgress(t) + "\nThe project folder may contain partial changes: inspect it before editing, and do not redo finished work.";
            if (mode == "team")
            {
                Dictionary<string, object> target; lock (gate) target = teams.FirstOrDefault(x => Str(x, "Id") == Str(cfg, "TeamId"));
                if (target == null || Str(target, "Id") == teamId) throw new ArgumentException("Choose a different team to continue with.");
                if (exhausted.Count > 0 && TeamVendors(target).Any(k => exhausted.Contains(k) || IsLimited(k))) throw new InvalidOperationException("That team also uses a vendor that is out of usage. Choose one that doesn't.");
                string tid = Str(target, "Id"); lock (gate) teamHandoffDepth[tid] = depth + 1;
                RunTeam(tid, moved); lock (gate) { t["Note"] = "Handed to the team " + Str(target, "Title") + " because " + fromText + " ran out of usage."; t["HandoffTo"] = "team:" + tid; }
            }
            else
            {
                string h = Str(cfg, "Harness"), svc = Str(cfg, "ServiceId"); string key = h == "laica" && svc != "" ? "api:" + svc : h; var info = Harnesses().FirstOrDefault(x => x.Id == h && x.Available);
                if (info == null) throw new ArgumentException("That agent isn't available."); if (exhausted.Contains(key) || IsLimited(key)) throw new InvalidOperationException("That agent is out of usage too. Choose another.");
                var d = (Dictionary<string, object>)Create(h, Str(t, "Cwd"), info.DefaultMode, null, null, "Handoff: " + Clip(Str(t, "Title"), 30), false, String.IsNullOrEmpty(svc) ? null : svc, String.IsNullOrEmpty(Str(cfg, "Model")) ? null : Str(cfg, "Model"));
                string sid = Str(d, "Id"); lock (gate) { Session ns; if (sessions.TryGetValue(sid, out ns)) ns.HandoffDepth = depth + 1; }
                Send(sid, moved); lock (gate) { t["Note"] = "Continued by " + VendorName(key) + " because " + fromText + " ran out of usage."; t["HandoffTo"] = "chat:" + sid; }
            }
            SetTeam(t, null); return true;
        }

        /// <summary>Called when a team run ends in failure: if it ran out of usage, apply the handoff setting.</summary>
        void TeamRanOut(Dictionary<string, object> t, string error)
        {
            if (!IsLimitText(error) && error.IndexOf("out of usage", StringComparison.OrdinalIgnoreCase) < 0) return;
            string id = Str(t, "Id");
            if (Str(EffectiveHandoff(id), "Mode") == "manual") { lock (gate) t["Note"] = "A vendor ran out of usage. Choose where to continue."; SetTeam(t, null); return; }
            try { HandoffTeam(id, null); }
            catch (Exception ex) { lock (gate) t["Note"] = "Automatic handoff didn't happen: " + ex.Message; SetTeam(t, null); }
        }
    }
}
