using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Laica
{
    /// <summary>
    /// Tag-team continuity. One chat is worked by two agents from different vendors, one at a time, over the same project folder. When the one holding the work runs out of usage
    /// (or you click Switch now) LAICA hands over to the other, and hands back when the first has reset. Each agent keeps its own session and resumes it with its own CLI, so a
    /// switch only sends the other agent what it has not seen: LAICA builds that itself, from the event log and the folder, without asking any model.
    /// The visible chat is the primary session; the partner runs as a hidden child session whose events are mirrored into the visible one.
    /// </summary>
    public sealed partial class HarnessManager
    {
        sealed class PairState
        {
            public string Mode = "", PHarness = "", PService = "", PModel = "", PEffort = "", PMode = "", AutoSwitch = "", SwitchBack = "";
            public string Blocked = "", BlockedWhy = "", ChildId = "", Active = "primary", Halted = "", Pending = "", LogId = "", Note = "";
            public long SeenA, SeenB, DeliverSeq; public bool RanA, RanB, Switching, PendingUnfinished;
            public DateTime LastSwitch, WaitUntil; public List<DateTime> Times = new List<DateTime>(); public List<string> Stamps = new List<string>();
            public string DeliverReason = "", DeliverRequest = ""; public bool DeliverUnfinished; public long[] Before = new long[4];
        }

        static readonly Dictionary<string, object> ContDefaults = new Dictionary<string, object> {
            { "Mode", "assisted" }, { "PartnerHarness", "" }, { "PartnerService", "" }, { "PartnerModel", "" }, { "AutoSwitch", true }, { "SwitchBack", true },
            { "MinGapMinutes", 5 }, { "ThrashSwitches", 4 }, { "ThrashMinutes", 30 }, { "ReturnThreshold", 80 }, { "ReturnAtBoundary", false }, { "HandoffPath", "HANDOFF.md" }, { "AgentNote", false } };
        static readonly string[] ContModes = { "off", "assisted", "automatic" };
        static readonly Regex WriterRx = new Regex(@"already has an active writer|thread-store conflict", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex AuthRx = new Regex(@"not logged in|please run /login|not signed in|please (log|sign) ?in|login required|invalid (api key|credentials|x-api-key)|authentication (failed|error|required)|unauthorized|\b401\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ResumeFailRx = new Regex(@"no conversation found|could not (resume|find)|session .{0,40}(not found|does not exist|expired)|thread .{0,40}not found|unknown (session|thread)|invalid session|failed to resume|no rollout found|resume.{0,30}(fail|error)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        readonly object handoffIo = new object(), switchGate = new object();
        List<Dictionary<string, object>> switchLog;

        // ---------- settings ----------
        Dictionary<string, object> ContGlobal()
        {
            var r = new Dictionary<string, object>(ContDefaults);
            lock (gate) { var c = Obj(handoff, "Continuity"); if (c != null) foreach (var kv in c) if (r.ContainsKey(kv.Key) && kv.Value != null) r[kv.Key] = kv.Value; }
            return r;
        }
        string ContStr(string k) { return Convert.ToString(ContGlobal()[k], CultureInfo.InvariantCulture) ?? ""; }
        int ContInt(string k) { try { return Convert.ToInt32(ContGlobal()[k], CultureInfo.InvariantCulture); } catch (Exception) { return Convert.ToInt32(ContDefaults[k]); } }
        bool ContBool(string k) { object v = ContGlobal()[k]; return v is bool ? (bool)v : String.Equals(Convert.ToString(v), "true", StringComparison.OrdinalIgnoreCase); }
        string ContMode(Session root)
        {
            string m = root.Pair.Mode; if (m == "" || !ContModes.Contains(m)) m = ContStr("Mode"); return ContModes.Contains(m) ? m : "assisted";
        }
        bool AutoOn(Session root) { return root.Pair.AutoSwitch != "" ? root.Pair.AutoSwitch == "True" : ContBool("AutoSwitch"); }
        bool BackOn(Session root) { return root.Pair.SwitchBack != "" ? root.Pair.SwitchBack == "True" : ContBool("SwitchBack"); }

        public object ContinuityGet() { var r = ContGlobal(); r["Harnesses"] = Harnesses().Where(h => h.Available && h.Id != "workflow").Select(h => (object)new Dictionary<string, object> { { "Id", h.Id }, { "Name", h.Name } }).ToArray(); return r; }
        public object ContinuitySet(Dictionary<string, object> d)
        {
            var cur = ContGlobal(); string mode = Str(d, "Mode");
            if (mode != "") { if (!ContModes.Contains(mode)) throw new ArgumentException("Choose off, assisted or automatic."); cur["Mode"] = mode; }
            if (d.ContainsKey("PartnerHarness")) { string h = Str(d, "PartnerHarness"); if (h != "" && !Harnesses().Any(x => x.Id == h && x.Available)) throw new ArgumentException("That partner agent isn't available."); cur["PartnerHarness"] = h; cur["PartnerService"] = Str(d, "PartnerService"); cur["PartnerModel"] = Str(d, "PartnerModel"); }
            if (d.ContainsKey("AutoSwitch")) cur["AutoSwitch"] = Str(d, "AutoSwitch") == "True";
            if (d.ContainsKey("SwitchBack")) cur["SwitchBack"] = Str(d, "SwitchBack") == "True";
            if (d.ContainsKey("ReturnAtBoundary")) cur["ReturnAtBoundary"] = Str(d, "ReturnAtBoundary") == "True";
            if (d.ContainsKey("AgentNote")) cur["AgentNote"] = Str(d, "AgentNote") == "True";
            Action<string, int, int> clamp = (k, lo, hi) => { int v; if (d.ContainsKey(k) && Int32.TryParse(Str(d, k), out v)) cur[k] = Math.Max(lo, Math.Min(hi, v)); };
            clamp("MinGapMinutes", 0, 240); clamp("ThrashSwitches", 2, 20); clamp("ThrashMinutes", 5, 720); clamp("ReturnThreshold", 30, 99);
            if (d.ContainsKey("HandoffPath")) { string p = Str(d, "HandoffPath").Trim(); cur["HandoffPath"] = SafeRel(p) ? p : "HANDOFF.md"; }
            lock (gate) { handoff["Continuity"] = cur; SaveList("handoff.json", new List<Dictionary<string, object>> { handoff }); }
            Raise(); return ContinuityGet();
        }
        static bool SafeRel(string p) { if (p == "" || Path.IsPathRooted(p) || p.Contains("..") || p.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || p.EndsWith("/") || p.EndsWith("\\")) return false; return true; }

        // ---------- the two sides ----------
        Dictionary<string, object> PartnerSpec(Session root)
        {
            string h = root.Pair.PHarness, svc = root.Pair.PService, model = root.Pair.PModel;
            if (h == "") { h = ContStr("PartnerHarness"); svc = ContStr("PartnerService"); model = ContStr("PartnerModel"); }
            string own = VendorKey(root); var all = Harnesses();
            if (h != "") { string k = h == "laica" && svc != "" ? "api:" + svc : h; if (k == own || !all.Any(x => x.Id == h && x.Available)) h = ""; }
            if (h == "")
            {
                var pick = all.FirstOrDefault(x => x.Available && (x.Id == "codex" || x.Id == "claude") && x.Id != own) ?? all.FirstOrDefault(x => x.Available && x.Id != "workflow" && x.Id != "laica" && x.Id != own);
                if (pick == null) return null; h = pick.Id; svc = ""; model = "";
            }
            var info = all.First(x => x.Id == h); string mode = root.Pair.PMode != "" && info.Modes.Contains(root.Pair.PMode) ? root.Pair.PMode : info.DefaultMode;
            return new Dictionary<string, object> { { "Harness", h }, { "ServiceId", svc ?? "" }, { "Model", model ?? "" }, { "Effort", root.Pair.PEffort }, { "Mode", mode } };
        }
        string SideKey(Session root, bool partner) { if (!partner) return VendorKey(root); var sp = PartnerSpec(root); return sp == null ? "" : KeyOfSpec(sp); }
        string ActiveKey(Session root) { return SideKey(root, root.Pair.Active == "partner"); }
        /// <summary>The partner agent to show next to a tag-team chat's name ("" when the chat is not a tag-team).</summary>
        static string PartnerShown(Session s)
        {
            if (s.Parent != null) return "";
            if (s.Child != null) return s.Child.Harness;
            if (s.Pair.Mode != "automatic") return "";
            if (s.Pair.PHarness != "") return s.Pair.PHarness;
            return s.Harness == "codex" ? "claude" : s.Harness == "claude" ? "codex" : "";
        }
        static bool IsBusy(Session s) { return s.Busy || (s.Child != null && s.Child.Busy); }
        /// <summary>The session that is really doing the work right now: the partner while it holds the work, else the chat itself.</summary>
        Session Live(Session s) { if (s.Child != null && (s.Child.Busy || (s.Pair.Active == "partner" && !s.Busy))) return s.Child; return s; }

        Session EnsureChild(Session root)
        {
            lock (gate) { if (root.Child != null) return root.Child; }
            var sp = PartnerSpec(root); if (sp == null) throw new InvalidOperationException("Choose a partner agent first: there is no second agent installed.");
            var c = new Session { Id = Guid.NewGuid().ToString("N"), Harness = Str(sp, "Harness"), Cwd = root.Cwd, Mode = Str(sp, "Mode"), Title = "Tag-team partner", Rules = root.Rules, AssistantId = root.AssistantId, ServiceId = Str(sp, "ServiceId") == "" ? null : Str(sp, "ServiceId"), Model = Str(sp, "Model") == "" ? null : Str(sp, "Model"), Effort = ValidEffort(Str(sp, "Effort")), Parent = root, ParentId = root.Id };
            lock (gate) { sessions[c.Id] = c; root.Child = c; root.Pair.ChildId = c.Id; }
            SaveSession(c); return c;
        }

        // ---------- usage knowledge ----------
        List<PlanWin> WindowsFor(string key)
        {
            var wins = new List<PlanWin>();
            lock (planGate) { if (key == "codex") wins.AddRange(codexCache); foreach (var kv in liveWindows) if (kv.Key.StartsWith(key + "|")) wins.Add(kv.Value); }
            return wins;
        }
        double PairPercent(string key)
        {
            RefreshPlanUsage(); double top = 0; foreach (var w in WindowsFor(key)) top = Math.Max(top, w.Percent);
            lock (gate) { VendorUse u; if (usage.TryGetValue(key, out u)) { Use(key); if (u.Budget > 0) top = Math.Max(top, u.TokensToday * 100.0 / u.Budget); if (u.WeekBudget > 0) top = Math.Max(top, WeekTokens(u) * 100.0 / u.WeekBudget); } }
            return Math.Round(Math.Min(100, top), 1);
        }
        /// <summary>When a vendor is usable again: the end of its limit if it is parked, else the reset of its fullest window, else nothing known.</summary>
        DateTime ResetOf(string key)
        {
            lock (gate) { VendorUse u; if (usage.TryGetValue(key, out u) && u.LimitedUntil > DateTime.UtcNow) return u.LimitedUntil; }
            return NextWindowReset(key);
        }
        DateTime NextWindowReset(string key)
        {
            var wins = WindowsFor(key).Where(w => w.ResetsUtc > DateTime.UtcNow).OrderBy(w => w.Minutes == 0 ? 99999 : w.Minutes).ToList();
            return wins.Count > 0 ? wins[0].ResetsUtc : DateTime.MinValue;
        }
        /// <summary>The reset time to trust when a limit message arrives: the message's own words if it has any, else the fullest plan window.</summary>
        DateTime RefineReset(string key, string text, DateTime fallback, bool parsed)
        {
            DateTime tryAt = ParseTryAgain(text); if (tryAt != DateTime.MinValue && tryAt > DateTime.UtcNow) return tryAt.AddMinutes(1);
            DateTime clock = ParseResetClock(text, DateTime.Now); if (clock != DateTime.MinValue) return clock.ToUniversalTime().AddMinutes(1);
            if (parsed) return fallback;
            try
            {
                if (key == "codex") RefreshCodex();
                var full = WindowsFor(key).Where(w => w.Percent >= 90 && w.ResetsUtc > DateTime.UtcNow).OrderByDescending(w => w.Percent).ThenByDescending(w => w.Minutes).FirstOrDefault();
                if (full != null && full.ResetsUtc < DateTime.UtcNow.AddDays(8)) return full.ResetsUtc.AddMinutes(1);
            }
            catch (Exception) { }
            return fallback;
        }
        static readonly Regex ClockRx = new Regex(@"resets?\s+(?:at\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        /// <summary>"... limit reached, resets 3pm" -> the next 3 pm after nowLocal. DateTime.MinValue when the text has no clock time.</summary>
        public static DateTime ParseResetClock(string text, DateTime nowLocal)
        {
            var m = ClockRx.Match(text ?? ""); if (!m.Success) return DateTime.MinValue;
            int h = Int32.Parse(m.Groups[1].Value), mi = m.Groups[2].Success ? Int32.Parse(m.Groups[2].Value) : 0; if (h < 1 || h > 12 || mi > 59) return DateTime.MinValue;
            bool pm = m.Groups[3].Value.ToLowerInvariant() == "pm"; h = h % 12 + (pm ? 12 : 0);
            var t = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, h, mi, 0, DateTimeKind.Local); if (t <= nowLocal) t = t.AddDays(1);
            return t;
        }
        static string ClockText(DateTime utc) { return utc.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant(); }

        // ---------- HANDOFF.md ----------
        string HandoffFilePath(string cwd)
        {
            if (String.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) return "";
            string rel = ContStr("HandoffPath"); if (!SafeRel(rel)) rel = "HANDOFF.md";
            try { string full = Path.GetFullPath(Path.Combine(cwd, rel)); string root = Path.GetFullPath(cwd).TrimEnd('\\') + "\\"; if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) full = Path.Combine(cwd, "HANDOFF.md"); string dirp = Path.GetDirectoryName(full); if (!Directory.Exists(dirp)) full = Path.Combine(cwd, "HANDOFF.md"); return full; }
            catch (Exception) { return Path.Combine(cwd, "HANDOFF.md"); }
        }
        string HandoffName(Session root) { string p = HandoffFilePath(root.Cwd); return p == "" ? "HANDOFF.md" : Path.GetFileName(p); }
        string AgentLabel(Session s) { return VendorName(VendorKey(s)) + (String.IsNullOrEmpty(s.Model) ? "" : " (" + s.Model + ")"); }
        List<Dictionary<string, object>> EventsOf(Session root) { lock (gate) return new List<Dictionary<string, object>>(root.Events); }

        /// <summary>Rewrites the managed block of the project's HANDOFF.md (keeping the Pinned part and the previous copy). Never throws: a read-only folder just means no file.</summary>
        string WriteHandoffFile(Session root, Session by)
        {
            try
            {
                if (root.Harness == "workflow" || !String.IsNullOrEmpty(root.TeamId)) return "";
                string path = HandoffFilePath(root.Cwd); if (path == "") return "";
                var info = HandoffBuilder.Build(EventsOf(root), root.Cwd, root.Rules, AgentLabel(by ?? root), DateTime.UtcNow); info.NextSteps = root.Pair.Note ?? "";
                string managed = HandoffBuilder.RenderManaged(info);
                lock (handoffIo)
                {
                    string existing = File.Exists(path) ? File.ReadAllText(path) : "", merged = HandoffBuilder.Merge(existing, managed);
                    if (existing != "") { string prev = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + ".prev" + Path.GetExtension(path)); File.WriteAllText(prev, existing); }
                    string tmp = path + ".tmp"; File.WriteAllText(tmp, merged); if (File.Exists(path)) File.Delete(path); File.Move(tmp, path);
                }
                return path;
            }
            catch (Exception) { return ""; }
        }

        public object HandoffFileGet(string cwd)
        {
            if (String.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd)) throw new ArgumentException("Choose an existing project folder.");
            string path = HandoffFilePath(cwd), text = ""; lock (handoffIo) { if (path != "" && File.Exists(path)) text = File.ReadAllText(path); }
            string by, utc; HandoffBuilder.ParseUpdated(text, out by, out utc);
            return new Dictionary<string, object> { { "Path", path }, { "Exists", text != "" }, { "Managed", HandoffBuilder.ManagedOf(text).Replace(HandoffBuilder.StartMark, "").Replace(HandoffBuilder.EndMark, "").Trim() }, { "Pinned", HandoffBuilder.PinnedOf(text) }, { "UpdatedBy", by }, { "UpdatedUtc", utc } };
        }
        public object HandoffFileSet(string cwd, string pinned)
        {
            if (String.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd)) throw new ArgumentException("Choose an existing project folder.");
            if ((pinned ?? "").Length > 60000) throw new ArgumentException("The pinned notes are too long.");
            string path = HandoffFilePath(cwd); if (path == "") throw new InvalidOperationException("That folder can't hold a handoff file.");
            lock (handoffIo) { string existing = File.Exists(path) ? File.ReadAllText(path) : ""; string next = HandoffBuilder.WithPinned(existing, pinned); string tmp = path + ".tmp"; File.WriteAllText(tmp, next); if (File.Exists(path)) File.Delete(path); File.Move(tmp, path); }
            return HandoffFileGet(cwd);
        }

        // ---------- switch log (Analytics) ----------
        List<Dictionary<string, object>> SwitchLog() { lock (switchGate) { if (switchLog == null) switchLog = LoadList("switches.json"); return switchLog; } }
        string LogSwitch(Session root, string from, string to, string reason, long tokens, double cost)
        {
            string id = Guid.NewGuid().ToString("N");
            lock (switchGate) { var l = SwitchLog(); l.Add(new Dictionary<string, object> { { "Id", id }, { "TimeUtc", DateTime.UtcNow.ToString("o") }, { "ChatId", root.Id }, { "Chat", root.Title }, { "From", from }, { "To", to }, { "Reason", reason }, { "EstTokens", tokens }, { "EstCost", Math.Round(cost, 4) }, { "ActualTokens", 0L }, { "ActualCost", 0.0 } }); if (l.Count > 500) l.RemoveRange(0, l.Count - 500); SaveList("switches.json", l); }
            return id;
        }
        public object SwitchesGet()
        {
            lock (switchGate)
            {
                var l = SwitchLog(); double est = 0, act = 0; foreach (var d in l) { est += Dbl(d, "EstCost"); act += Dbl(d, "ActualCost"); }
                return new Dictionary<string, object> { { "Count", l.Count }, { "EstCost", Math.Round(est, 4) }, { "ActualCost", Math.Round(act, 4) }, { "Recent", l.Skip(Math.Max(0, l.Count - 30)).Reverse().Select(d => (object)new Dictionary<string, object>(d)).ToArray() } };
            }
        }
        static double Dbl(Dictionary<string, object> d, string k) { double v; return Double.TryParse(Str(d, k), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0; }
        double CostFor(Session target, long tokens, bool output)
        {
            string model = String.IsNullOrEmpty(target.Model) ? (target.Harness == "claude" ? "sonnet" : target.Harness == "codex" ? "gpt-5" : "") : target.Model;
            var p = PriceFor(model); return tokens * (output ? p[1] : p[0]) / 1e6;
        }

        // ---------- building what the other agent is told ----------
        string BuildDelivery(Session root, bool toPartner, string reason, string request, bool unfinished, bool forceFresh)
        {
            string fromKey = SideKey(root, !toPartner), fromName = VendorName(fromKey); Session target = toPartner ? root.Child : root;
            long seen = toPartner ? root.Pair.SeenB : root.Pair.SeenA; bool ran = toPartner ? root.Pair.RanB : root.Pair.RanA;
            bool fresh = forceFresh || !ran || target == null || target.ExternalId == null;
            var events = EventsOf(root); string hp = HandoffName(root); var sb = new StringBuilder();
            if (fresh)
            {
                string text = ""; try { string p = HandoffFilePath(root.Cwd); if (p != "" && File.Exists(p)) lock (handoffIo) text = File.ReadAllText(p); } catch (Exception) { }
                string state = HandoffBuilder.ManagedOf(text); string pinned = HandoffBuilder.PinnedOf(text);
                if (state == "") state = HandoffBuilder.RenderManaged(HandoffBuilder.Build(events, root.Cwd, root.Rules, AgentLabel(root), DateTime.UtcNow));
                sb.Append("You are joining a project that is already under way, as one half of a two-agent tag team: ").Append(fromName).Append(" and you take turns on this same working folder and hand over when one runs out of usage. The user does not want to explain anything again, so carry on from the notes below.\n\n");
                sb.Append("PROJECT STATE (kept up to date by LAICA):\n").Append(HandoffBuilder.Clip(state.Replace(HandoffBuilder.StartMark, "").Replace(HandoffBuilder.EndMark, ""), 16000)).Append("\n");
                if (pinned != "") sb.Append("\nPINNED NOTES FROM THE USER:\n").Append(HandoffBuilder.Clip(pinned, 6000)).Append("\n");
                sb.Append("\n").Append(HandoffBuilder.Delta(events, 0, fromName, root.Cwd, hp, 9000));
            }
            else
            {
                sb.Append("You are taking the work back from ").Append(fromName).Append(". You already know everything up to your own last turn; this is only what is new.\n\n");
                sb.Append(HandoffBuilder.Delta(events, seen, fromName, root.Cwd, hp, 14000));
            }
            sb.Append("\n---\n\n");
            if (unfinished) sb.Append("The previous agent stopped before finishing. Carry on with the request that was in progress and do not ask the user to repeat it:\n").Append(HandoffBuilder.Redact(HandoffBuilder.Clip(request, 4000))).Append("\n\nInspect the working folder first and read any files listed under VERIFY FIRST before building on them. Do not redo finished work.");
            else sb.Append("The user now writes:\n").Append(HandoffBuilder.Redact(HandoffBuilder.Clip(request, 6000)));
            return sb.ToString();
        }

        string LastUserRequest(Session root) { lock (gate) { for (int i = root.Events.Count - 1; i >= 0; i--) if (Str(root.Events[i], "Kind") == "user") return Str(root.Events[i], "Text"); } return ""; }
        /// <summary>True when the last request ended on a usage limit or error instead of finishing.</summary>
        bool LastTurnUnfinished(Session root)
        {
            lock (gate)
            {
                bool bad = false;
                for (int i = root.Events.Count - 1; i >= 0; i--) { string k = Str(root.Events[i], "Kind"); if (k == "user") return bad; if (k == "limit" || (k == "pairnote" && Str(root.Events[i], "Text").StartsWith("LAICA was closed"))) bad = true; }
            }
            return false;
        }

        // ---------- sending ----------
        bool PairRoutes(Session s) { return s.Parent == null && (ContMode(s) == "automatic" || s.Pair.Active == "partner" || s.Pair.WaitUntil != DateTime.MinValue); }

        void PairSend(Session root, string prompt)
        {
            if (String.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Write a message first.");
            if (prompt.Length > 100000) throw new ArgumentException("Message is too long.");
            if (IsBusy(root)) throw new InvalidOperationException("This chat is still working. Stop it or wait.");
            if (root.Pair.WaitUntil != DateTime.MinValue)
            {
                Emit(root, "user", prompt, null); lock (gate) { root.Pair.Pending = prompt; root.Pair.PendingUnfinished = false; }
                Emit(root, "pairnote", "Queued. Both agents are out of usage; this will run when the first one resets (" + ClockText(root.Pair.WaitUntil) + ").", null); SaveSession(root); Raise(); return;
            }
            if (ContMode(root) == "automatic" && AutoOn(root) && root.Pair.Halted == "")
            {
                string holder = ActiveKey(root); bool onPartner = root.Pair.Active == "partner"; string other = SideKey(root, !onPartner);
                string reason = "";
                if (IsLimited(holder)) reason = "limit";
                else if (other != "" && !IsLimited(other))
                {
                    int threshold = ContInt("ReturnThreshold"); double hp = PairPercent(holder), op = PairPercent(other);
                    if (BackOn(root) && onPartner && ContBool("ReturnAtBoundary")) reason = "return";
                    else if (hp >= threshold && op < threshold) reason = onPartner && BackOn(root) ? "return" : (onPartner ? "" : "threshold");
                }
                if (reason != "")
                {
                    Emit(root, "user", prompt, null);
                    if (PairSwitch(root, reason, prompt, false)) return;
                    if (IsLimited(holder)) { if (root.Pair.WaitUntil == DateTime.MinValue) Emit(root, "pairnote", VendorName(holder) + " is out of usage and the other agent can't take over right now. Your message wasn't sent.", null); return; }
                    PairDeliverPlain(root, prompt, false); return;
                }
            }
            PairDeliverPlain(root, prompt, true);
        }

        void PairDeliverPlain(Session root, string prompt, bool showUser)
        {
            if (root.Pair.Active == "partner" && root.Child != null)
            {
                if (showUser) Emit(root, "user", prompt, null);
                SendCore(root.Child.Id, prompt, null, false);
            }
            else SendCore(root.Id, prompt, null, showUser);
        }

        // ---------- switching ----------
        /// <summary>Hands the work to the other agent. Returns false when it did not switch (and says why in the chat).</summary>
        bool PairSwitch(Session root, string reason, string request, bool unfinished)
        {
            lock (gate) { if (root.Pair.Switching) return false; root.Pair.Switching = true; }
            try
            {
                bool manual = reason == "manual";
                if (root.TeamId != null && root.TeamId != "") return false;
                if (ContMode(root) == "off") { if (manual) throw new InvalidOperationException("Continuity is off for this chat. Turn it on under Tag-team first."); return false; }
                if (root.Pair.Halted != "" && !manual) { Emit(root, "pairnote", "Automatic switching is paused (" + root.Pair.Halted + "). Use Switch now to continue.", null); return false; }
                if (manual) { lock (gate) { root.Pair.Halted = ""; root.Pair.Times.Clear(); root.Pair.Stamps.Clear(); } }
                if (IsBusy(root)) { if (manual) throw new InvalidOperationException("Wait for the agent to finish this turn, then switch."); return false; }
                if (PartnerSpec(root) == null) { if (manual) throw new InvalidOperationException("There is no second agent to switch to. Install another agent CLI first."); return false; }
                bool toPartner = root.Pair.Active != "partner";
                string sideName = toPartner ? "partner" : "primary";
                if (root.Pair.Blocked == sideName)
                {
                    if (manual) lock (gate) { root.Pair.Blocked = ""; root.Pair.BlockedWhy = ""; }
                    else { if (reason == "limit") Emit(root, "pairnote", "Not switching to " + VendorName(SideKey(root, toPartner)) + ": it " + root.Pair.BlockedWhy + ". Fix that, then press Switch now.", null); return false; }
                }
                string holderKey = SideKey(root, !toPartner), targetKey = SideKey(root, toPartner), toName = VendorName(targetKey), fromName = VendorName(holderKey);
                if (IsLimited(targetKey))
                {
                    DateTime tr = ResetOf(targetKey), hr = ResetOf(holderKey);
                    if (manual) throw new InvalidOperationException(toName + " is out of usage" + (tr != DateTime.MinValue ? " until about " + ClockText(tr) : "") + ".");
                    if (IsLimited(holderKey))
                    {
                        DateTime until = tr == DateTime.MinValue ? hr : (hr == DateTime.MinValue ? tr : (tr < hr ? tr : hr)); if (until == DateTime.MinValue) until = DateTime.UtcNow.AddMinutes(30);
                        lock (gate) { root.Pair.WaitUntil = until; root.Pair.Pending = request ?? ""; root.Pair.PendingUnfinished = unfinished; }
                        string who = until == tr ? toName : fromName;
                        string msg = "Both " + fromName + " and " + toName + " are out of usage. Waiting for " + who + " to reset at " + ClockText(until) + "; this will carry on by itself.";
                        Emit(root, "pairwait", msg, until.ToString("o")); SaveSession(root); Raise(); var done = Completed; if (done != null) done("LAICA is waiting", msg);
                        return false;
                    }
                    Emit(root, "pairnote", "Not switching to " + toName + ": it is out of usage" + (tr != DateTime.MinValue ? " until about " + ClockText(tr) : "") + ".", null);
                    return false;
                }
                string targetHarness = toPartner ? Str(PartnerSpec(root), "Harness") : root.Harness;
                if (AuthState(targetHarness) == false)
                {
                    string msg = toName + " isn't signed in on this computer. Open a terminal, run it once and sign in (for Claude Code: run claude and use /login), or press Sign in in the Tag-team panel.";
                    if (manual) throw new InvalidOperationException(msg);
                    lock (gate) { root.Pair.Blocked = sideName; root.Pair.BlockedWhy = "isn't signed in"; }
                    if (reason == "limit" || reason == "resume") Emit(root, "pairnote", "Not switching to " + toName + ": " + msg, null);
                    return false;
                }                bool optional = reason == "return" || reason == "threshold";
                if (optional && root.Pair.LastSwitch != DateTime.MinValue && (DateTime.UtcNow - root.Pair.LastSwitch).TotalMinutes < ContInt("MinGapMinutes")) return false;
                if (!manual && Thrashing(root))
                {
                    lock (gate) root.Pair.Halted = "the agents keep handing over without any file changing";
                    Emit(root, "pairnote", "Automatic switching stopped: the agents have handed over " + ContInt("ThrashSwitches") + " times in " + ContInt("ThrashMinutes") + " minutes without changing any file. Look at the chat, then use Switch now to carry on.", null); SaveSession(root); Raise(); return false;
                }
                // checkpoint, then compose what the other side needs
                Session fin = toPartner ? root : root.Child; WriteHandoffFile(root, fin ?? root);
                string req = request; if (String.IsNullOrEmpty(req)) req = LastUserRequest(root);
                string text = BuildDelivery(root, toPartner, reason, req, unfinished, false);
                Session target = toPartner ? EnsureChild(root) : root;
                long tokens = text.Length / 4 + 200; double cost = CostFor(target, tokens, false);
                string why = reason == "limit" ? fromName + " ran out of usage" : reason == "manual" ? "you asked for it" : reason == "return" ? toName + " has reset" : reason == "resume" ? toName + " is available again" : fromName + " is nearly out of usage";
                string stamp = HandoffBuilder.FolderStamp(root.Cwd);
                lock (gate) { root.Pair.Times.Add(DateTime.UtcNow); root.Pair.Stamps.Add(stamp); while (root.Pair.Times.Count > 50) { root.Pair.Times.RemoveAt(0); root.Pair.Stamps.RemoveAt(0); } root.Pair.LastSwitch = DateTime.UtcNow; root.Pair.Active = toPartner ? "partner" : "primary"; root.Pair.WaitUntil = DateTime.MinValue; root.Pair.Pending = ""; }
                string logId = LogSwitch(root, fromName, toName, reason, tokens, cost);
                lock (gate) { root.Pair.LogId = logId; root.Pair.Before = new[] { target.TIn, target.TOut, target.TCr, target.TCw }; root.Pair.DeliverSeq = root.Seq; root.Pair.DeliverReason = reason; root.Pair.DeliverRequest = req; root.Pair.DeliverUnfinished = unfinished; target.Probe = target.ExternalId != null; }
                Emit(root, "switch", "Switched to " + toName + " because " + why + ".", new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "From", fromName }, { "To", toName }, { "Reason", reason }, { "EstTokens", tokens }, { "EstCost", Math.Round(cost, 4) }, { "Unfinished", unfinished } }));
                SaveSession(root); Raise();
                SendCore(target.Id, req, text, false);
                return true;
            }
            finally { lock (gate) root.Pair.Switching = false; }
        }

        bool Thrashing(Session root)
        {
            int n = ContInt("ThrashSwitches"), m = ContInt("ThrashMinutes"); DateTime cutoff = DateTime.UtcNow.AddMinutes(-m); int idx;
            lock (gate)
            {
                idx = root.Pair.Times.FindIndex(t => t >= cutoff); if (idx < 0) return false;
                if (root.Pair.Times.Count - idx < n) return false;
                return root.Pair.Stamps[idx] == HandoffBuilder.FolderStamp(root.Cwd);
            }
        }

        /// <summary>A usage limit hit while the pair is in automatic mode: wait for the turn to wind down, then hand over.</summary>
        void PairOnLimit(Session root, Session limited, string key)
        {
            for (int i = 0; i < 1200 && limited.Busy; i++) Thread.Sleep(100);
            if (key != ActiveKey(root)) return;
            bool unfinished = LastTurnUnfinished(root);
            PairSwitch(root, "limit", unfinished ? LastUserRequest(root) : "", unfinished);
        }

        public object PairSwitchNow(string id)
        {
            Session root; lock (gate) { root = Get(id); if (root.Parent != null) root = root.Parent; }
            bool unfinished = LastTurnUnfinished(root);
            PairSwitch(root, "manual", unfinished ? LastUserRequest(root) : "", unfinished);
            return PairInfo(root.Id);
        }

        // ---------- turn boundaries ----------
        void PairAfterTurn(Session fin)
        {
            try
            {
                Session root = fin.Parent ?? fin; if (root.Harness == "workflow" || !String.IsNullOrEmpty(root.TeamId)) return;
                bool child = fin.Parent != null;
                if (ContMode(root) == "off" && root.Child == null) return;
                lock (gate) { if (child) { root.Pair.SeenB = root.Seq; root.Pair.RanB = true; } else { root.Pair.SeenA = root.Seq; root.Pair.RanA = true; } }
                // what the last switch really cost
                string logId; long[] before; lock (gate) { logId = root.Pair.LogId; before = root.Pair.Before; }
                if (logId != "" && fin == (root.Pair.Active == "partner" ? root.Child : root))
                {
                    long dIn = fin.TIn - before[0], dOut = fin.TOut - before[1], dCr = fin.TCr - before[2], dCw = fin.TCw - before[3];
                    if (dIn + dOut + dCr + dCw > 0)
                    {
                        var p = PriceFor(String.IsNullOrEmpty(fin.Model) ? (fin.Harness == "claude" ? "sonnet" : "gpt-5") : fin.Model); double c = CostOf(p, new long[] { 0, dIn, dOut, dCr, dCw });
                        lock (switchGate) { var l = SwitchLog(); var d = l.FirstOrDefault(x => Str(x, "Id") == logId); if (d != null) { d["ActualTokens"] = dIn + dOut + dCr + dCw; d["ActualCost"] = Math.Round(c, 4); SaveList("switches.json", l); } }
                        lock (gate) root.Pair.LogId = "";
                    }
                }
                // the agent could not pick its own session up again: start it fresh from the notes
                if (fin.Probe)
                {
                    fin.Probe = false; bool failed = false, said = false; long from = root.Pair.DeliverSeq;
                    lock (gate) foreach (var e in root.Events) { if (Num(e, "N") <= from) continue; string k = Str(e, "Kind"); if (k == "assistant") said = true; if (k == "error" && ResumeFailRx.IsMatch(Str(e, "Text")) && !WriterRx.IsMatch(Str(e, "Text"))) failed = true; }
                    if (failed && !said && root.Pair.DeliverRequest != "")
                    {
                        lock (gate) { fin.ExternalId = null; if (child) root.Pair.RanB = false; else root.Pair.RanA = false; }
                        Emit(root, "pairnote", "Couldn't pick " + VendorName(VendorKey(fin)) + "'s earlier session back up. Starting it again from the project notes (" + HandoffName(root) + ").", null);
                        string text = BuildDelivery(root, child, "fallback", root.Pair.DeliverRequest, root.Pair.DeliverUnfinished, true);
                        lock (gate) root.Pair.DeliverSeq = root.Seq;
                        SendCore(fin.Id, root.Pair.DeliverRequest, text, false);
                        return;
                    }
                }
                // the agent that was handed the work could not even start (not signed in, crashed): give the work back
                if (root.Pair.DeliverReason != "" && fin == (root.Pair.Active == "partner" ? root.Child : root))
                {
                    bool said = false, limitSeen = false; string err = "", errAll = ""; long from = root.Pair.DeliverSeq;
                    lock (gate) foreach (var e in root.Events) { if (Num(e, "N") <= from) continue; string k = Str(e, "Kind"); if (k == "assistant" || k == "tool") said = true; if (k == "error") { if (IsLimitText(Str(e, "Text"))) limitSeen = true; else { err = Str(e, "Text"); errAll += Str(e, "Text") + "\n"; } } }
                    lock (gate) root.Pair.DeliverReason = "";
                    // a usage limit is handled by its own path (wait, or hand on): only a real failure to start is undone here
                    if (!said && !limitSeen && err != "") { PairUndoSwitch(root, child, err, errAll); return; }
                }
                if (ContMode(root) != "off") WriteHandoffFile(root, fin);
                SaveSession(root);
            }
            catch (Exception) { }
        }

        /// <summary>Checks waiting tag-team chats now instead of at the next timer tick.</summary>
        public void PairPoll() { PairTick(); }
        // ---------- is the agent signed in? ----------
        readonly Dictionary<string, KeyValuePair<DateTime, bool?>> authCache = new Dictionary<string, KeyValuePair<DateTime, bool?>>();

        /// <summary>Reads the result of an agent's own "am I signed in" command. true/false, or null when it can't tell.</summary>
        public static bool? ParseAuth(string harness, int exitCode, string output)
        {
            output = output ?? "";
            if (harness == "claude")
            {
                if (Regex.IsMatch(output, "\"loggedIn\"\\s*:\\s*true", RegexOptions.IgnoreCase)) return true;
                if (Regex.IsMatch(output, "\"loggedIn\"\\s*:\\s*false", RegexOptions.IgnoreCase)) return false;
                return null;
            }
            if (harness == "codex") { if (Regex.IsMatch(output, "not logged in|logged out", RegexOptions.IgnoreCase)) return false; if (Regex.IsMatch(output, "logged in", RegexOptions.IgnoreCase)) return true; return exitCode == 0 ? (bool?)true : null; }
            return null;
        }

        static string QuickRun(string exe, string args, int ms, out int exit)
        {
            exit = -1;
            try
            {
                string file = exe, arguments = args;
                if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) { file = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"; arguments = "/c \"\"" + exe + "\" " + args + "\""; }
                var psi = new System.Diagnostics.ProcessStartInfo { FileName = file, Arguments = arguments, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    try { p.StandardInput.Close(); } catch (Exception) { }
                    var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(ms)) { try { p.Kill(); } catch (Exception) { } return ""; }
                    exit = p.ExitCode; return o.Result + e.Result;
                }
            }
            catch (Exception) { return ""; }
        }

        /// <summary>Whether Claude Code or Codex is signed in on this computer (null: unknown or not one of those). Asks the agent itself, never reads its credentials. Cached for a minute.</summary>
        public bool? AuthState(string harness)
        {
            if (harness != "claude" && harness != "codex") return null;
            lock (authCache) { KeyValuePair<DateTime, bool?> c; if (authCache.TryGetValue(harness, out c) && (DateTime.UtcNow - c.Key).TotalSeconds < 60) return c.Value; }
            var info = Harnesses().FirstOrDefault(h => h.Id == harness && h.Available); if (info == null) return null;
            int exit; string output = QuickRun(ResolveExe(info.Path), harness == "claude" ? "auth status" : "login status", 8000, out exit);
            bool? v = ParseAuth(harness, exit, output);
            lock (authCache) authCache[harness] = new KeyValuePair<DateTime, bool?>(DateTime.UtcNow, v);
            return v;
        }

        public object AgentAuth()
        {
            var list = new List<object>();
            foreach (var h in Harnesses().Where(x => x.Available && (x.Id == "claude" || x.Id == "codex")))
                list.Add(new Dictionary<string, object> { { "Id", h.Id }, { "Name", h.Name }, { "SignedIn", AuthState(h.Id) } });
            return list.ToArray();
        }

        /// <summary>Starts the agent's own sign-in (it opens your browser). LAICA never sees the credentials.</summary>
        public void AgentSignIn(string harness)
        {
            var info = Harnesses().FirstOrDefault(h => h.Id == harness && h.Available && (h.Id == "claude" || h.Id == "codex")); if (info == null) throw new ArgumentException("That agent can't be signed in from here.");
            string exe = ResolveExe(info.Path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exe, Arguments = harness == "claude" ? "auth login" : "login", UseShellExecute = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) });
            lock (authCache) authCache.Remove(harness);
        }
        /// <summary>The agent handed the work could not start. Give the work back to the one that had it, say why, and stop trying that agent until the user fixes it and presses Switch now.</summary>
        void PairUndoSwitch(Session root, bool failedIsChild, string err, string errAll)
        {
            bool writer = WriterRx.IsMatch(errAll ?? err ?? ""); bool auth = !writer && AuthRx.IsMatch(err ?? ""); string failedName = VendorName(SideKey(root, failedIsChild)), prevKey = SideKey(root, !failedIsChild), prevName = VendorName(prevKey);
            lock (gate)
            {
                root.Pair.Active = failedIsChild ? "primary" : "partner";
                if (root.Pair.Times.Count > 0) { root.Pair.Times.RemoveAt(root.Pair.Times.Count - 1); root.Pair.Stamps.RemoveAt(root.Pair.Stamps.Count - 1); }
                root.Pair.LastSwitch = DateTime.MinValue;
                if (!writer) { root.Pair.Blocked = failedIsChild ? "partner" : "primary"; root.Pair.BlockedWhy = auth ? "isn't signed in" : "stopped with an error"; }
            }
            string request = root.Pair.DeliverRequest; bool prevUsable = !IsLimited(prevKey);
            string hint = writer ? failedName + "'s conversation is open somewhere else (for example in the " + failedName + " app), and only one place can write to it at a time. Close that conversation or the app there, then press Switch now. Nothing was lost: LAICA kept this chat linked to it." : auth ? failedName + " isn't signed in. Open a terminal, run it once and sign in (for Claude Code: run claude and use /login), then press Switch now." : failedName + " stopped before it could start: " + HandoffBuilder.Clip(err, 200) + " Fix that, then press Switch now.";
            Emit(root, "pairnote", "The hand-over to " + failedName + " didn't happen. " + hint + (prevUsable && request != "" ? " " + prevName + " keeps the work for now." : (prevUsable ? "" : " " + prevName + " is out of usage too, so the chat is waiting on you.")), null);
            SaveSession(root); Raise();
            if (prevUsable && request != "")
            {
                try { Session ps = failedIsChild ? root : EnsureChild(root); SendCore(ps.Id, request, null, false); }
                catch (Exception ex) { Emit(root, "pairnote", "Couldn't hand the work back to " + prevName + ": " + ex.Message, null); }
            }
        }
        /// <summary>Called every few seconds: when both agents were out of usage, carry on by itself as soon as one is back.</summary>
        void PairTick()
        {
            List<Session> waiting; lock (gate) waiting = sessions.Values.Where(s => s.Parent == null && s.Pair.WaitUntil != DateTime.MinValue).ToList();
            foreach (var root in waiting)
            {
                string a = SideKey(root, false), b = SideKey(root, true); bool aOk = a != "" && !IsLimited(a), bOk = b != "" && !IsLimited(b);
                if (!aOk && !bOk) { DateTime next = new[] { ResetOf(a), ResetOf(b) }.Where(t => t != DateTime.MinValue).OrderBy(t => t).FirstOrDefault(); if (next != DateTime.MinValue) lock (gate) root.Pair.WaitUntil = next; continue; }
                if (IsBusy(root)) continue;
                PairResume(root);
            }
        }

        void PairResume(Session root)
        {
            string request; bool unfinished;
            lock (gate) { request = root.Pair.Pending; unfinished = root.Pair.PendingUnfinished; root.Pair.WaitUntil = DateTime.MinValue; root.Pair.Pending = ""; root.Pair.PendingUnfinished = false; }
            if (request == "") { request = LastUserRequest(root); unfinished = true; }
            string holder = ActiveKey(root); string msg = "Back in business: carrying on.";
            Emit(root, "pairnote", msg, null); var done = Completed; if (done != null) done("LAICA is working again", "An agent has reset; the paused work is carrying on.");
            if (!IsLimited(holder))
            {
                string text = "Your usage limit has reset. " + (unfinished ? "Carry on with the request you were working on, without asking the user to repeat it:\n" : "The user writes:\n") + HandoffBuilder.Redact(HandoffBuilder.Clip(request, 6000));
                Session live = root.Pair.Active == "partner" ? EnsureChild(root) : root;
                lock (gate) { root.Pair.DeliverReason = ""; root.Pair.LogId = ""; }
                SendCore(live.Id, request, text, false); return;
            }
            PairSwitch(root, "resume", request, unfinished);
        }

        // ---------- what the screen shows ----------
        Dictionary<string, object> SideDto(Session root, bool partner)
        {
            string key = SideKey(root, partner); if (key == "") return new Dictionary<string, object> { { "Key", "" }, { "Name", "" } };
            DateTime reset = ResetOf(key); bool limited = IsLimited(key); Session s = partner ? root.Child : root; var sp = partner ? PartnerSpec(root) : null;
            return new Dictionary<string, object> { { "Key", key }, { "Name", VendorName(key) }, { "Model", partner ? (sp != null ? Str(sp, "Model") : "") : (root.Model ?? "") }, { "Effort", partner ? root.Pair.PEffort : (root.Effort ?? "") }, { "Percent", PairPercent(key) }, { "Limited", limited }, { "ResetsUtc", reset == DateTime.MinValue ? "" : reset.ToString("o") }, { "Mode", partner ? (sp != null ? Str(sp, "Mode") : "") : root.Mode }, { "Writable", partner ? (sp != null && Str(sp, "Mode") != "plan" && Str(sp, "Mode") != "read-only") : (root.Mode != "plan" && root.Mode != "read-only") }, { "Ran", partner ? root.Pair.RanB : root.Pair.RanA }, { "Busy", s != null && s.Busy } };
        }

        public object PairInfo(string id)
        {
            Session root; lock (gate) { root = Get(id); if (root.Parent != null) root = root.Parent; }
            string mode = ContMode(root); bool partner = root.Pair.Active == "partner"; var spec = PartnerSpec(root);
            string preview = ""; long tokens = 0; double cost = 0;
            if (spec != null && mode != "off")
            {
                try { preview = BuildDelivery(root, !partner, "preview", LastUserRequest(root), LastTurnUnfinished(root), false); tokens = preview.Length / 4 + 200; Session t = partner ? root : (root.Child ?? new Session { Harness = Str(spec, "Harness"), Model = Str(spec, "Model") }); cost = CostFor(t, tokens, false); } catch (Exception) { }
            }
            // a plain-language rundown of what the next agent will take over, for the interface (the full text stays available)
            object summary = null;
            if (spec != null && mode != "off")
            {
                try
                {
                    var hi = HandoffBuilder.Build(EventsOf(root), root.Cwd, root.Rules, "", DateTime.UtcNow); Session tgt = partner ? root : root.Child; bool fresh = !(partner ? root.Pair.RanA : root.Pair.RanB) || tgt == null || tgt.ExternalId == null;
                    summary = new Dictionary<string, object> { { "Target", VendorName(SideKey(root, !partner)) }, { "From", VendorName(SideKey(root, partner)) }, { "Fresh", fresh }, { "Request", HandoffBuilder.Clip(hi.Latest, 400) }, { "Unfinished", hi.Unfinished }, { "LastReply", hi.LastReply }, { "Turns", hi.Done.Count },
                        { "Files", hi.Files.Take(8).ToArray() }, { "FilesTotal", hi.Files.Count }, { "Verify", hi.VerifyFirst.Take(6).ToArray() }, { "Commands", hi.Commands.Take(5).ToArray() }, { "Problems", hi.Problems.Take(3).ToArray() }, { "Stage", hi.Stage } };
                }
                catch (Exception) { }
            }            var switches = new List<object>(); lock (gate) foreach (var e in root.Events) if (Str(e, "Kind") == "switch") switches.Add(new Dictionary<string, object> { { "TimeUtc", Str(e, "TimeUtc") }, { "Text", Str(e, "Text") }, { "Detail", Str(e, "Detail") } });
            return new Dictionary<string, object> {
                { "Id", root.Id }, { "Mode", mode }, { "ChatMode", root.Pair.Mode }, { "Active", root.Pair.Active }, { "ActiveName", ActiveKey(root) == "" ? "" : VendorName(ActiveKey(root)) },
                { "Primary", SideDto(root, false) }, { "Partner", SideDto(root, true) }, { "HasPartner", spec != null }, { "AutoSwitch", AutoOn(root) }, { "SwitchBack", BackOn(root) },
                { "Halted", root.Pair.Halted }, { "WaitUntilUtc", root.Pair.WaitUntil == DateTime.MinValue ? "" : root.Pair.WaitUntil.ToString("o") },
                { "Preview", preview }, { "Summary", summary }, { "EstTokens", tokens }, { "EstCost", Math.Round(cost, 4) }, { "Currency", "USD" }, { "HandoffFile", HandoffName(root) },
                { "LastSwitchUtc", root.Pair.LastSwitch == DateTime.MinValue ? "" : root.Pair.LastSwitch.ToString("o") }, { "Switches", switches.Skip(Math.Max(0, switches.Count - 30)).ToArray() },
                { "ReturnThreshold", ContInt("ReturnThreshold") } };
        }

        /// <summary>Per-chat tag-team settings: mode, partner, auto-switch and switch-back. Empty values fall back to the Settings defaults.</summary>
        public object PairConfigure(string id, Dictionary<string, object> d)
        {
            Session root; lock (gate) { root = Get(id); if (root.Parent != null) throw new ArgumentException("Configure the main chat."); }
            if (d.ContainsKey("Mode")) { string m = Str(d, "Mode"); if (m != "" && !ContModes.Contains(m)) throw new ArgumentException("Choose off, assisted or automatic."); lock (gate) root.Pair.Mode = m; }
            if (d.ContainsKey("PartnerHarness"))
            {
                string h = Str(d, "PartnerHarness"), svc = Str(d, "PartnerService"); string k = h == "laica" && svc != "" ? "api:" + svc : h;
                if (h != "") { if (!Harnesses().Any(x => x.Id == h && x.Available)) throw new ArgumentException("That partner agent isn't available."); if (k == VendorKey(root)) throw new ArgumentException("The partner has to be a different agent from this chat's own."); }
                if (root.Child != null && (root.Child.Busy || root.Pair.Active == "partner") && (root.Pair.PHarness != h || root.Pair.PService != svc)) throw new InvalidOperationException("The partner is working right now. Change it after it finishes and the work is back with the first agent.");
                lock (gate) { if (root.Pair.PHarness != h || root.Pair.PService != svc) { root.Pair.PHarness = h; root.Pair.PService = svc; root.Pair.PModel = Str(d, "PartnerModel"); if (root.Child != null) { var old = root.Child; sessions.Remove(old.Id); root.Child = null; root.Pair.ChildId = ""; root.Pair.RanB = false; root.Pair.SeenB = 0; try { File.Delete(Path.Combine(dir, "sessions", Safe(old.Id) + ".json")); } catch (Exception) { } } else root.Pair.PModel = Str(d, "PartnerModel"); } }
            }
            if (d.ContainsKey("PartnerModel"))
            {
                string pm = Str(d, "PartnerModel"); if (pm == "default") pm = "";
                if (pm != "" && !Regex.IsMatch(pm, @"^[A-Za-z0-9._\-]{1,80}$")) throw new ArgumentException("That model name isn't valid.");
                lock (gate) { root.Pair.PModel = pm; if (root.Child != null && !root.Child.Busy) root.Child.Model = pm == "" ? null : pm; }
            }
            if (d.ContainsKey("PartnerEffort"))
            {
                string pe = Str(d, "PartnerEffort"); if (pe == "default") pe = "";
                if (pe != "" && ValidEffort(pe) == null) throw new ArgumentException("That reasoning level isn't valid.");
                lock (gate) { root.Pair.PEffort = pe; if (root.Child != null && !root.Child.Busy) root.Child.Effort = pe == "" ? null : pe; }
            }
            if (d.ContainsKey("PartnerMode")) lock (gate) root.Pair.PMode = Str(d, "PartnerMode");
            if (d.ContainsKey("AutoSwitch")) lock (gate) root.Pair.AutoSwitch = Str(d, "AutoSwitch") == "" ? "" : (Str(d, "AutoSwitch") == "True" ? "True" : "False");
            if (d.ContainsKey("SwitchBack")) lock (gate) root.Pair.SwitchBack = Str(d, "SwitchBack") == "" ? "" : (Str(d, "SwitchBack") == "True" ? "True" : "False");
            SaveSession(root); Raise(); return PairInfo(root.Id);
        }

        public object HandoffPreview(string id) { var d = (Dictionary<string, object>)PairInfo(id); return new Dictionary<string, object> { { "Text", d["Preview"] }, { "EstTokens", d["EstTokens"] }, { "EstCost", d["EstCost"] }, { "Currency", "USD" } }; }

        // ---------- saving the pair ----------
        Dictionary<string, object> PairToDict(PairState p)
        {
            return new Dictionary<string, object> { { "Mode", p.Mode }, { "PHarness", p.PHarness }, { "PService", p.PService }, { "PModel", p.PModel }, { "PEffort", p.PEffort }, { "PMode", p.PMode }, { "AutoSwitch", p.AutoSwitch }, { "SwitchBack", p.SwitchBack }, { "ChildId", p.ChildId }, { "Active", p.Active }, { "Halted", p.Halted }, { "Blocked", p.Blocked }, { "BlockedWhy", p.BlockedWhy }, { "Pending", p.Pending }, { "PendingUnfinished", p.PendingUnfinished }, { "Note", p.Note },
                { "SeenA", p.SeenA }, { "SeenB", p.SeenB }, { "RanA", p.RanA }, { "RanB", p.RanB }, { "LastSwitch", p.LastSwitch == DateTime.MinValue ? "" : p.LastSwitch.ToString("o") }, { "WaitUntil", p.WaitUntil == DateTime.MinValue ? "" : p.WaitUntil.ToString("o") },
                { "Times", p.Times.Select(t => t.ToString("o")).ToArray() }, { "Stamps", p.Stamps.ToArray() }, { "DeliverRequest", p.DeliverRequest }, { "DeliverReason", p.DeliverReason }, { "DeliverUnfinished", p.DeliverUnfinished }, { "DeliverSeq", p.DeliverSeq } };
        }
        PairState PairFromDict(Dictionary<string, object> d)
        {
            var p = new PairState(); if (d == null) return p;
            p.Mode = Str(d, "Mode"); p.PHarness = Str(d, "PHarness"); p.PService = Str(d, "PService"); p.PModel = Str(d, "PModel"); p.PEffort = Str(d, "PEffort"); p.PMode = Str(d, "PMode"); p.AutoSwitch = Str(d, "AutoSwitch"); p.SwitchBack = Str(d, "SwitchBack"); p.ChildId = Str(d, "ChildId");
            p.Active = Str(d, "Active") == "partner" ? "partner" : "primary"; p.Halted = Str(d, "Halted"); p.Blocked = Str(d, "Blocked"); p.BlockedWhy = Str(d, "BlockedWhy"); p.Pending = Str(d, "Pending"); p.PendingUnfinished = Str(d, "PendingUnfinished") == "True"; p.Note = Str(d, "Note");
            p.SeenA = Num(d, "SeenA"); p.SeenB = Num(d, "SeenB"); p.RanA = Str(d, "RanA") == "True"; p.RanB = Str(d, "RanB") == "True"; p.DeliverRequest = Str(d, "DeliverRequest"); p.DeliverReason = Str(d, "DeliverReason"); p.DeliverUnfinished = Str(d, "DeliverUnfinished") == "True"; p.DeliverSeq = Num(d, "DeliverSeq");
            DateTime t; if (DateTime.TryParse(Str(d, "LastSwitch"), null, DateTimeStyles.RoundtripKind, out t)) p.LastSwitch = t; if (DateTime.TryParse(Str(d, "WaitUntil"), null, DateTimeStyles.RoundtripKind, out t)) p.WaitUntil = t;
            var times = Arr(d, "Times"); if (times != null) foreach (object o in times) if (DateTime.TryParse(Convert.ToString(o), null, DateTimeStyles.RoundtripKind, out t)) p.Times.Add(t);
            var stamps = Arr(d, "Stamps"); if (stamps != null) foreach (object o in stamps) p.Stamps.Add(Convert.ToString(o));
            while (p.Stamps.Count < p.Times.Count) p.Stamps.Add(""); while (p.Stamps.Count > p.Times.Count) p.Stamps.RemoveAt(p.Stamps.Count - 1);
            return p;
        }
        /// <summary>After sessions are loaded: join each partner to its main chat, and number any events saved before events were numbered.</summary>
        void LinkPairs()
        {
            lock (gate)
            {
                foreach (var s in sessions.Values)
                {
                    long n = 0; foreach (var e in s.Events) { long cur = Num(e, "N"); if (cur > 0) n = Math.Max(n, cur); }
                    foreach (var e in s.Events) if (Num(e, "N") <= 0) e["N"] = ++n;
                    s.Seq = Math.Max(s.Seq, n);
                }
                foreach (var s in sessions.Values)
                {
                    // older versions threw the link to the vendor's own conversation away after "already has an active writer" and started an empty one: put the link back
                    if (s.Parent != null || (s.Harness != "codex" && s.Harness != "claude")) continue;
                    string lost = null; foreach (var e in s.Events) { if (Str(e, "Kind") != "error") continue; var m = Regex.Match(Str(e, "Text"), @"thread ([0-9a-f\-]{36}) already has an active writer"); if (m.Success) lost = m.Groups[1].Value; }
                    if (lost != null && s.ExternalId != lost && s.Events.Any(e => Str(e, "Kind") == "pairnote" && Str(e, "Text").StartsWith("Couldn't pick"))) s.ExternalId = lost;
                }
                // LAICA was closed (or crashed) while an agent was mid-turn: say so, so nobody wonders why the work stopped
                foreach (var s in sessions.Values)
                    if (s.WasBusy) { s.WasBusy = false; var ie = new Dictionary<string, object> { { "SessionId", s.Id }, { "Kind", "pairnote" }, { "Text", "LAICA was closed while this was working. What the agent had already done is in your files and in the chat above. Send continue and it will pick up from there; LAICA lists what changed since." }, { "Detail", null }, { "TimeUtc", DateTime.UtcNow.ToString("o") }, { "N", ++s.Seq } }; s.Events.Add(ie); }
                foreach (var s in sessions.Values.ToList()) if (!String.IsNullOrEmpty(s.ParentId)) { Session p; if (sessions.TryGetValue(s.ParentId, out p)) { s.Parent = p; p.Child = s; p.Pair.ChildId = s.Id; } else s.ParentId = ""; }
            }
        }
    }
}
