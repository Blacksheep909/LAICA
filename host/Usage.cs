using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Laica
{
    /// <summary>
    /// Usage across vendors: turns and tokens per vendor, usage-limit detection (so one vendor running dry never breaks a chat or a team),
    /// soft daily budgets that warn before a limit, and "continue elsewhere" which moves a chat onto another vendor with its transcript.
    /// </summary>
    public sealed partial class HarnessManager
    {
        sealed class VendorUse
        {
            public string Key, Name = "", Day = "", LimitText = "";
            public int Turns, TurnsToday, Limits; public long In, Out, TokensToday, Budget, WeekBudget; public Dictionary<string, long> Days = new Dictionary<string, long>();
            public DateTime LastUtc, LimitedUntil;
        }

        readonly Dictionary<string, VendorUse> usage = new Dictionary<string, VendorUse>();
        static readonly Regex LimitRx = new Regex(@"usage limit|rate.?limit|quota|credit balance|too many requests|\b429\b|limit reached|reached (your|the|its) .{0,30}limit|exceeded (your|the|its) .{0,40}(limit|quota)|insufficient[_ ]quota|out of (credits|usage|tokens)|resource[_ ]exhausted|usage cap|billing", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ResetRx = new Regex(@"(?:in|after)\s+(\d+(?:\.\d+)?)\s*(second|sec|minute|min|hour|hr|day)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsLimitText(string text) { return !String.IsNullOrEmpty(text) && LimitRx.IsMatch(text); }

        static string TodayKey() { return DateTime.Now.ToString("yyyy-MM-dd"); }

        static string VendorKey(Session s) { return s.Harness == "laica" && !String.IsNullOrEmpty(s.ServiceId) ? "api:" + s.ServiceId : s.Harness; }
        static string KeyOfSpec(Dictionary<string, object> sp) { string h = Str(sp, "Harness"); return h == "laica" && Str(sp, "ServiceId") != "" ? "api:" + Str(sp, "ServiceId") : h; }

        string VendorName(string key)
        {
            try
            {
                if (key.StartsWith("api:")) { var svc = services().FirstOrDefault(x => x.Id == key.Substring(4)); return svc != null ? svc.Name : "API service"; }
                var h = Harnesses().FirstOrDefault(x => x.Id == key); if (h != null) return key == "codex" ? "Codex (GPT)" : h.Name;
            }
            catch (Exception) { }
            return key;
        }
        string SpecName(Dictionary<string, object> sp) { return VendorName(KeyOfSpec(sp)); }

        VendorUse Use(string key)
        {
            VendorUse u; if (!usage.TryGetValue(key, out u)) { u = new VendorUse { Key = key }; usage[key] = u; }
            string today = TodayKey(); if (u.Day != today) { u.Day = today; u.TurnsToday = 0; u.TokensToday = 0; }
            return u;
        }

        public bool IsLimited(string key) { lock (gate) { VendorUse u; return usage.TryGetValue(key, out u) && u.LimitedUntil > DateTime.UtcNow; } }

        void MarkLimited(string key, string text)
        {
            DateTime until = DateTime.UtcNow.AddMinutes(30);
            var m = ResetRx.Match(text ?? "");
            if (m.Success)
            {
                double n; if (Double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n))
                {
                    string unit = m.Groups[2].Value.ToLowerInvariant(); double mins = unit.StartsWith("sec") ? n / 60 : unit.StartsWith("min") ? n : unit.StartsWith("h") ? n * 60 : n * 1440;
                    until = DateTime.UtcNow.AddMinutes(Math.Max(1, Math.Min(mins + 1, 7 * 1440)));
                }
            }
            lock (gate) { var u = Use(key); u.Limits++; u.LimitedUntil = until; u.LimitText = (text ?? "").Length > 300 ? text.Substring(0, 300) : (text ?? ""); u.LastUtc = DateTime.UtcNow; SaveUsage(); }
        }

        /// <summary>Called when a turn ends. A turn that raised no error counts as usage and proves the vendor works again.</summary>
        void CountTurn(Session s)
        {
            if (s.Harness == "workflow") return;
            bool failed = false;
            lock (gate) { for (int i = s.Events.Count - 1; i >= 0; i--) { string k = Str(s.Events[i], "Kind"); if (k == "user") break; if (k == "error") { failed = true; break; } } }
            lock (gate)
            {
                var u = Use(VendorKey(s)); u.LastUtc = DateTime.UtcNow;
                if (!failed) { u.Turns++; u.TurnsToday++; u.LimitedUntil = DateTime.MinValue; u.LimitText = ""; }
                SaveUsage();
            }
        }

        void AddTokens(Session s, Dictionary<string, object> u)
        {
            if (u == null) return;
            long a = Num(u, "input_tokens") + Num(u, "prompt_tokens") + Num(u, "cache_creation_input_tokens"), b = Num(u, "output_tokens") + Num(u, "completion_tokens");
            if (a == 0 && b == 0) return;
            long cached = Num(u, "cached_input_tokens"), plain = Num(u, "input_tokens") + Num(u, "prompt_tokens");
            lock (gate) { s.TIn += Math.Max(0, cached > 0 ? plain - cached : plain); s.TOut += b; s.TCr += Num(u, "cache_read_input_tokens") + cached; s.TCw += Num(u, "cache_creation_input_tokens"); }
            lock (gate) { var v = Use(VendorKey(s)); v.In += a; v.Out += b; v.TokensToday += a + b; long d; v.Days.TryGetValue(TodayKey(), out d); v.Days[TodayKey()] = d + a + b; if (v.Days.Count > 21) foreach (string k in v.Days.Keys.OrderBy(x => x).Take(v.Days.Count - 21).ToList()) v.Days.Remove(k); }
        }
        static long Num(Dictionary<string, object> d, string k) { object v; if (d == null || !d.TryGetValue(k, out v) || v == null) return 0; try { return Convert.ToInt64(v); } catch (Exception) { return 0; } }

        /// <summary>Raised from Emit for every error: a usage-limit error parks the vendor and tells the UI.</summary>
        void NoteError(Session s, string text)
        {
            if (!IsLimitText(text)) return;
            string key = VendorKey(s); MarkLimited(key, text);
            Emit(s, "limit", VendorName(key) + " has run out of usage.", key);
            Raise();
            var chat = s; string exhausted = key;
            System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { AutoHandoffChat(chat, exhausted); } catch (Exception ex) { try { Emit(chat, "notice", "Automatic handoff failed: " + ex.Message, null); } catch (Exception) { } } });
        }

        public object Usage()
        {
            var all = Harnesses(); var rows = new List<Dictionary<string, object>>(); var seen = new HashSet<string>();
            lock (gate)
            {
                Func<string, string, bool, Dictionary<string, object>> row = (key, name, available) =>
                {
                    VendorUse u; usage.TryGetValue(key, out u); if (u != null) Use(key);
                    bool limited = u != null && u.LimitedUntil > DateTime.UtcNow;
                    string warn = limited ? "limited" : (u != null && u.Budget > 0 && u.TokensToday >= u.Budget * 0.8 ? (u.TokensToday >= u.Budget ? "over" : "near") : "");
                    return new Dictionary<string, object> {
                        { "Key", key }, { "Name", name }, { "Available", available }, { "Turns", u != null ? u.Turns : 0 }, { "TurnsToday", u != null ? u.TurnsToday : 0 },
                        { "TokensIn", u != null ? u.In : 0 }, { "TokensOut", u != null ? u.Out : 0 }, { "TokensToday", u != null ? u.TokensToday : 0 }, { "Budget", u != null ? u.Budget : 0 },
                        { "Limited", limited }, { "LimitedUntilUtc", limited ? u.LimitedUntil.ToString("o") : "" }, { "LimitText", limited ? u.LimitText : "" }, { "Warn", warn },
                        { "LastUtc", u != null && u.LastUtc != DateTime.MinValue ? u.LastUtc.ToString("o") : "" } };
                };
                foreach (var h in all.Where(x => x.Id != "workflow" && x.Id != "laica")) { if (!h.Available) continue; seen.Add(h.Id); rows.Add(row(h.Id, h.Id == "codex" ? "Codex (GPT)" : h.Name, true)); }
                foreach (var svc in services()) { string k = "api:" + svc.Id; seen.Add(k); rows.Add(row(k, svc.Name, true)); }
                foreach (var k in usage.Keys.Where(x => !seen.Contains(x)).ToList()) rows.Add(row(k, VendorName(k), false));
            }
            ApplyPlanUsage(rows);
            return rows.ToArray();
        }

        static long WeekTokens(VendorUse u) { long sum = 0; for (int i = 0; i < 7; i++) { long v; if (u.Days.TryGetValue(DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd"), out v)) sum += v; } return sum; }

        public object SetUsageBudget(string key, long tokens) { return SetUsageBudget(key, tokens, -1); }
        public object SetUsageBudget(string key, long tokens, long weekTokens)
        {
            if (String.IsNullOrWhiteSpace(key)) throw new ArgumentException("Choose a vendor.");
            lock (gate) { var u = Use(key); if (tokens >= 0) u.Budget = Math.Max(0, Math.Min(tokens, 100000000000L)); if (weekTokens >= 0) u.WeekBudget = Math.Min(weekTokens, 1000000000000L); SaveUsage(); }
            Raise(); return Usage();
        }

        public object ClearUsageLimit(string key) { lock (gate) { VendorUse u; if (usage.TryGetValue(key, out u)) { u.LimitedUntil = DateTime.MinValue; u.LimitText = ""; SaveUsage(); } } Raise(); return Usage(); }

        void SaveUsage()
        {
            var list = usage.Values.Select(u => new Dictionary<string, object> { { "Key", u.Key }, { "Day", u.Day }, { "Turns", u.Turns }, { "TurnsToday", u.TurnsToday }, { "In", u.In }, { "Out", u.Out }, { "TokensToday", u.TokensToday }, { "Budget", u.Budget }, { "WeekBudget", u.WeekBudget }, { "Days", u.Days.Select(kv => kv.Key + ":" + kv.Value).ToArray() }, { "Limits", u.Limits }, { "LimitedUntil", u.LimitedUntil.ToString("o") }, { "LimitText", u.LimitText }, { "LastUtc", u.LastUtc.ToString("o") } }).ToList();
            SaveList("usage.json", list);
        }
        void LoadUsage()
        {
            foreach (var d in LoadList("usage.json"))
            {
                string key = Str(d, "Key"); if (key == "") continue;
                var u = new VendorUse { Key = key, Day = Str(d, "Day"), Turns = (int)Num(d, "Turns"), TurnsToday = (int)Num(d, "TurnsToday"), In = Num(d, "In"), Out = Num(d, "Out"), TokensToday = Num(d, "TokensToday"), Budget = Num(d, "Budget"), WeekBudget = Num(d, "WeekBudget"), Limits = (int)Num(d, "Limits"), LimitText = Str(d, "LimitText") };
                var days = Arr(d, "Days"); if (days != null) foreach (object o in days) { string[] kv = Convert.ToString(o).Split(':'); long n; if (kv.Length == 2 && Int64.TryParse(kv[1], out n)) u.Days[kv[0]] = n; }
                DateTime t; if (DateTime.TryParse(Str(d, "LimitedUntil"), null, System.Globalization.DateTimeStyles.RoundtripKind, out t)) u.LimitedUntil = t;
                if (DateTime.TryParse(Str(d, "LastUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out t)) u.LastUtc = t;
                usage[key] = u;
            }
        }

        // ---------- picking another vendor ----------
        /// <summary>The first agent that still has usage left: the team's own leader and teammates first, then any other detected agent.</summary>
        Dictionary<string, object> Fallback(Dictionary<string, object> t, Dictionary<string, object> failed, HashSet<string> tried)
        {
            var all = Harnesses(); var cands = new List<Dictionary<string, object>>();
            var leader = Obj(t, "Leader"); if (leader != null) cands.Add(leader);
            var mem = Arr(t, "Members"); if (mem != null) foreach (object o in mem) { var d = o as Dictionary<string, object>; if (d != null) cands.Add(d); }
            foreach (var h in all.Where(x => x.Available && x.Id != "workflow" && x.Id != "laica")) cands.Add(new Dictionary<string, object> { { "Harness", h.Id }, { "Mode", h.DefaultMode }, { "ServiceId", "" }, { "Model", "" } });
            foreach (var c in cands)
            {
                string key = KeyOfSpec(c); if (tried.Contains(key) || IsLimited(key)) continue;
                var info = all.FirstOrDefault(h => h.Id == Str(c, "Harness")); if (info == null || !info.Available) continue;
                bool same = KeyOfSpec(failed) == key;
                return new Dictionary<string, object> { { "Name", Str(failed, "Name") }, { "Role", Str(failed, "Role") }, { "Harness", Str(c, "Harness") }, { "Mode", Str(c, "Mode") != "" ? Str(c, "Mode") : info.DefaultMode }, { "ServiceId", Str(c, "ServiceId") }, { "Model", same ? Str(failed, "Model") : Str(c, "Model") } };
            }
            return null;
        }
        Dictionary<string, object> PickAvailable(Dictionary<string, object> t, Dictionary<string, object> spec)
        {
            if (!IsLimited(KeyOfSpec(spec))) return spec;
            var next = Fallback(t, spec, new HashSet<string> { KeyOfSpec(spec) });
            if (next == null) throw new InvalidOperationException("Every agent available to this team is out of usage right now. Try again after the limits reset, or run the goal on a different team.");
            return next;
        }

        /// <summary>Starts a new chat on another vendor that carries on from where a chat stopped, with the transcript so far.</summary>
        public object ContinueElsewhere(string sessionId, string harness, string serviceId, string model)
        {
            Session s; string cwd, mode, rules, assistant; var turns = new List<string>(); string lastUser = "";
            lock (gate)
            {
                s = Get(sessionId); cwd = s.Cwd; rules = s.Rules; assistant = s.AssistantId;
                foreach (var e in s.Events)
                {
                    string k = Str(e, "Kind"); if (k == "user") { lastUser = Str(e, "Text"); turns.Add("User: " + Clip(Str(e, "Text"), 2500)); }
                    else if (k == "assistant") { if (turns.Count > 0 && turns[turns.Count - 1].StartsWith("Assistant: ") && Str(e, "Detail") == "append") turns[turns.Count - 1] += "\n" + Clip(Str(e, "Text"), 2500); else turns.Add("Assistant: " + Clip(Str(e, "Text"), 2500)); }
                }
            }
            if (lastUser == "") throw new InvalidOperationException("There is nothing to continue yet.");
            var info = Harnesses().FirstOrDefault(h => h.Id == harness && h.Available); if (info == null) throw new ArgumentException("That agent isn't available.");
            mode = info.DefaultMode;
            var sb = new StringBuilder(); int budget = 14000;
            for (int i = turns.Count - 1; i >= 0 && budget > 0; i--) { budget -= turns[i].Length; sb.Insert(0, turns[i] + "\n\n"); }
            string from = VendorName(VendorKey(s));
            string prompt = "You are taking over a conversation from another assistant (" + from + ") that could not continue because it ran out of usage. " +
                "Its working folder is the current directory and may already contain partial changes: inspect it before editing, and do not redo finished work.\n\nTRANSCRIPT SO FAR (most recent last):\n" + sb.ToString().Trim() +
                "\n\n---\n\nContinue and finish the latest request from the user: " + lastUser;
            var d = (Dictionary<string, object>)Create(harness, cwd, mode, rules, assistant, "Continued: " + Clip(lastUser.Replace("\n", " "), 36), false, String.IsNullOrEmpty(serviceId) ? null : serviceId, String.IsNullOrEmpty(model) ? null : model);
            Send(Str(d, "Id"), prompt);
            return d;
        }
        static string Clip(string text, int max) { text = text ?? ""; return text.Length <= max ? text : text.Substring(0, max) + "..."; }
    }
}
