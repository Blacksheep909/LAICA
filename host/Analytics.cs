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
    /// Analytics over everything you have done with Codex and Claude Code on this computer: sessions, messages, tokens, an estimated cost, active days,
    /// peak hour, favourite model, a heatmap and a per-model breakdown. The logs are large (gigabytes), so they are read once in the background,
    /// summarised per file, cached on disk, and a file is only read again when it changes.
    /// </summary>
    public sealed partial class HarnessManager
    {
        const int AnaVersion = 2;
        sealed class FileStat
        {
            public string Source = "", Stamp = ""; public DateTime First = DateTime.MaxValue, Last = DateTime.MinValue;
            public Dictionary<string, long[]> Days = new Dictionary<string, long[]>();        // local day -> [messages, tokens]
            public Dictionary<string, long[]> DM = new Dictionary<string, long[]>();          // "day|model" -> [messages, input, output, cache read, cache write]
            public long[] Hours = new long[24];                                                // messages by local hour
            public Dictionary<string, long[]> Models = new Dictionary<string, long[]>();      // model -> [messages, tokens]
            public Dictionary<string, long> Tools = new Dictionary<string, long>();
        }

        readonly Dictionary<string, FileStat> anaFiles = new Dictionary<string, FileStat>(StringComparer.OrdinalIgnoreCase);
        readonly object anaGate = new object(); Thread anaThread; int anaTotal, anaDone; bool anaLoaded, anaEverRan; DateTime anaFinishedAt = DateTime.MinValue;
        static readonly Regex AnaModelRx = new Regex("\"model\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled), AnaTokObjRx = new Regex("\"last_token_usage\"\\s*:\\s*(\\{[^{}]*\\})", RegexOptions.Compiled);
        static readonly Regex AnaToolRx = new Regex("\"type\"\\s*:\\s*\"function_call\"[^{}]*?\"name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled), AnaClaudeToolRx = new Regex("\"type\"\\s*:\\s*\"tool_use\"[^{}]*?\"name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        static readonly Regex AnaMsgIdRx = new Regex("\"id\"\\s*:\\s*\"(msg_[A-Za-z0-9_]+)\"", RegexOptions.Compiled), CrRx = new Regex("\"cache_read_input_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
        static readonly Regex TotRx = new Regex("\"total_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled), CachedRx = new Regex("\"cached_input_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);

        string AnaCachePath() { return Path.Combine(dir, "analytics.json"); }

        static long[] Arr5(object o) { var a = o as System.Collections.IList; var r = new long[5]; if (a != null) for (int i = 0; i < 5 && i < a.Count; i++) r[i] = Convert.ToInt64(a[i]); return r; }

        void LoadAnalyticsCache()
        {
            if (anaLoaded) return; anaLoaded = true;
            try
            {
                string p = AnaCachePath(); if (!File.Exists(p)) return;
                var d = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(p)); if (Convert.ToInt32(d.ContainsKey("Version") ? d["Version"] : 0) != AnaVersion) return;
                var files = Obj(d, "Files"); if (files == null) return;
                foreach (var kv in files)
                {
                    var f = kv.Value as Dictionary<string, object>; if (f == null) continue; var s = new FileStat { Source = Str(f, "S"), Stamp = Str(f, "T") };
                    DateTime t; if (DateTime.TryParse(Str(f, "F"), null, DateTimeStyles.RoundtripKind, out t)) s.First = t; if (DateTime.TryParse(Str(f, "L"), null, DateTimeStyles.RoundtripKind, out t)) s.Last = t;
                    var days = Obj(f, "D"); if (days != null) foreach (var dk in days) { var a = dk.Value as System.Collections.IList; if (a != null && a.Count >= 2) s.Days[dk.Key] = new[] { Convert.ToInt64(a[0]), Convert.ToInt64(a[1]) }; }
                    var dm = Obj(f, "X"); if (dm != null) foreach (var dk in dm) s.DM[dk.Key] = Arr5(dk.Value);
                    var hours = f.ContainsKey("H") ? f["H"] as System.Collections.IList : null; if (hours != null) for (int i = 0; i < 24 && i < hours.Count; i++) s.Hours[i] = Convert.ToInt64(hours[i]);
                    var models = Obj(f, "M"); if (models != null) foreach (var mk in models) { var a = mk.Value as System.Collections.IList; if (a != null && a.Count >= 2) s.Models[mk.Key] = new[] { Convert.ToInt64(a[0]), Convert.ToInt64(a[1]) }; }
                    var tools = Obj(f, "O"); if (tools != null) foreach (var tk in tools) s.Tools[tk.Key] = Convert.ToInt64(tk.Value);
                    anaFiles[kv.Key] = s;
                }
            }
            catch (Exception) { anaFiles.Clear(); }
        }

        void SaveAnalyticsCache()
        {
            try
            {
                Dictionary<string, object> files;
                lock (anaGate) files = anaFiles.ToDictionary(kv => kv.Key, kv => (object)new Dictionary<string, object> {
                    { "S", kv.Value.Source }, { "T", kv.Value.Stamp }, { "F", kv.Value.First.ToString("o") }, { "L", kv.Value.Last.ToString("o") },
                    { "D", kv.Value.Days.ToDictionary(d => d.Key, d => (object)d.Value) }, { "X", kv.Value.DM.ToDictionary(d => d.Key, d => (object)d.Value) }, { "H", kv.Value.Hours.Cast<object>().ToArray() },
                    { "M", kv.Value.Models.ToDictionary(d => d.Key, d => (object)d.Value) }, { "O", kv.Value.Tools.ToDictionary(d => d.Key, d => (object)d.Value) } });
                string p = AnaCachePath(), tmp = p + ".tmp"; File.WriteAllText(tmp, json.Serialize(new Dictionary<string, object> { { "Version", AnaVersion }, { "Files", files } })); if (File.Exists(p)) File.Delete(p); File.Move(tmp, p);
            }
            catch (Exception) { }
        }

        static string StampOf(FileInfo f) { return f.Length + "|" + f.LastWriteTimeUtc.Ticks; }

        /// <summary>Starts reading the logs in the background, unless it just finished (the index needs no re-check on every request).</summary>
        void EnsureAnalytics()
        {
            lock (anaGate)
            {
                LoadAnalyticsCache();
                if (anaThread != null && anaThread.IsAlive) return;
                if (anaEverRan && (DateTime.UtcNow - anaFinishedAt).TotalSeconds < 45) return;
                anaEverRan = true; anaThread = new Thread(AnalyticsWorker) { IsBackground = true, Name = "analytics", Priority = ThreadPriority.BelowNormal }; anaThread.Start();
            }
        }

        void AnalyticsWorker()
        {
            try
            {
                var work = new List<KeyValuePair<FileInfo, string>>();
                try { string r = Path.Combine(CodexHomeDir(), "sessions"); if (Directory.Exists(r)) foreach (string f in Directory.GetFiles(r, "rollout-*.jsonl", SearchOption.AllDirectories)) work.Add(new KeyValuePair<FileInfo, string>(new FileInfo(f), "codex")); } catch (Exception) { }
                try { string r = Path.Combine(ClaudeDir(), "projects"); if (Directory.Exists(r)) foreach (string d in Directory.GetDirectories(r)) foreach (string f in Directory.GetFiles(d, "*.jsonl")) work.Add(new KeyValuePair<FileInfo, string>(new FileInfo(f), "claude")); } catch (Exception) { }
                work = work.OrderByDescending(w => w.Key.LastWriteTimeUtc).ToList();
                lock (anaGate) { anaTotal = work.Count; anaDone = 0; }
                int sinceSave = 0; bool changed = false;
                foreach (var w in work)
                {
                    string stamp = StampOf(w.Key); bool fresh;
                    lock (anaGate) { FileStat have; fresh = anaFiles.TryGetValue(w.Key.FullName, out have) && have.Stamp == stamp; }
                    if (!fresh)
                    {
                        FileStat s = null; try { s = w.Value == "codex" ? ScanCodex(w.Key, stamp) : ScanClaude(w.Key, stamp); } catch (Exception) { }
                        if (s != null) { lock (anaGate) anaFiles[w.Key.FullName] = s; changed = true; if (++sinceSave >= 12) { sinceSave = 0; SaveAnalyticsCache(); } }
                    }
                    lock (anaGate) anaDone++;
                }
                lock (anaGate) { var live = new HashSet<string>(work.Select(x => x.Key.FullName), StringComparer.OrdinalIgnoreCase); foreach (string k in anaFiles.Keys.Where(k => !live.Contains(k)).ToList()) { anaFiles.Remove(k); changed = true; } }
                if (changed) SaveAnalyticsCache();
            }
            catch (Exception) { }
            finally { lock (anaGate) anaFinishedAt = DateTime.UtcNow; }
        }

        static readonly Dictionary<string, KeyValuePair<string, int>> localTimeCache = new Dictionary<string, KeyValuePair<string, int>>();
        static bool LocalOf(string line, out string day, out int hour, out DateTime utc)
        {
            day = null; hour = 0; utc = DateTime.MinValue;
            int i = line.IndexOf("\"timestamp\":\"", StringComparison.Ordinal); if (i < 0 || i + 13 + 20 > line.Length) return false;
            string key = line.Substring(i + 13, 16);   // to the minute
            KeyValuePair<string, int> hit;
            lock (localTimeCache) { if (localTimeCache.TryGetValue(key, out hit)) { day = hit.Key; hour = hit.Value; DateTime.TryParse(key + ":00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc); return true; } }
            if (!DateTime.TryParse(key + ":00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc)) return false;
            var local = utc.ToLocalTime(); day = local.ToString("yyyy-MM-dd"); hour = local.Hour;
            lock (localTimeCache) { if (localTimeCache.Count > 400000) localTimeCache.Clear(); localTimeCache[key] = new KeyValuePair<string, int>(day, hour); }
            return true;
        }

        static void Bump(FileStat s, string day, int hour, DateTime utc, long msgs, long tokens, string model, long inT = 0, long outT = 0, long cacheRead = 0, long cacheWrite = 0)
        {
            long[] d; if (!s.Days.TryGetValue(day, out d)) s.Days[day] = d = new long[2]; d[0] += msgs; d[1] += tokens;
            if (msgs > 0) s.Hours[hour] += msgs;
            if (!String.IsNullOrEmpty(model))
            {
                long[] m; if (!s.Models.TryGetValue(model, out m)) s.Models[model] = m = new long[2]; m[0] += msgs; m[1] += tokens;
                string k = day + "|" + model; long[] x; if (!s.DM.TryGetValue(k, out x)) s.DM[k] = x = new long[5]; x[0] += msgs; x[1] += inT; x[2] += outT; x[3] += cacheRead; x[4] += cacheWrite;
            }
            if (utc < s.First) s.First = utc; if (utc > s.Last) s.Last = utc;
        }

        static long Num1(Regex rx, string text) { var m = rx.Match(text); long v; return m.Success && Int64.TryParse(m.Groups[1].Value, out v) ? v : 0; }

        FileStat ScanCodex(FileInfo fi, string stamp)
        {
            var s = new FileStat { Source = "codex", Stamp = stamp }; string model = "";
            using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var sr = new StreamReader(fs, Encoding.UTF8, false, 1 << 16))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length < 40) continue;
                    if (line.IndexOf("\"type\":\"turn_context\"", StringComparison.Ordinal) >= 0 && line.Length < 200000) { var m = AnaModelRx.Match(line); if (m.Success) model = m.Groups[1].Value; continue; }
                    string day; int hour; DateTime utc;
                    if (line.IndexOf("\"type\":\"message\"", StringComparison.Ordinal) >= 0 && (line.IndexOf("\"role\":\"user\"", StringComparison.Ordinal) >= 0 || line.IndexOf("\"role\":\"assistant\"", StringComparison.Ordinal) >= 0) && line.IndexOf("\"type\":\"response_item\"", StringComparison.Ordinal) >= 0)
                    { if (LocalOf(line, out day, out hour, out utc)) Bump(s, day, hour, utc, 1, 0, model == "" ? "unknown" : model); continue; }
                    if (line.IndexOf("\"type\":\"token_count\"", StringComparison.Ordinal) >= 0 && line.IndexOf("last_token_usage", StringComparison.Ordinal) >= 0)
                    {
                        var om = AnaTokObjRx.Match(line);
                        if (om.Success && LocalOf(line, out day, out hour, out utc))
                        {
                            string o = om.Groups[1].Value; long total = Num1(TotRx, o), input = Num1(InRx, o), output = Num1(OutRx, o), cached = Num1(CachedRx, o);
                            Bump(s, day, hour, utc, 0, total, model == "" ? "unknown" : model, Math.Max(0, input - cached), output, cached, 0);
                        }
                        continue;
                    }
                    if (line.Length < 400000 && line.IndexOf("\"type\":\"function_call\"", StringComparison.Ordinal) >= 0) { var m = AnaToolRx.Match(line); if (m.Success) { long c; s.Tools.TryGetValue(m.Groups[1].Value, out c); s.Tools[m.Groups[1].Value] = c + 1; } }
                    else if (line.IndexOf("\"type\":\"local_shell_call\"", StringComparison.Ordinal) >= 0) { long c; s.Tools.TryGetValue("shell", out c); s.Tools["shell"] = c + 1; }
                }
            }
            return s.Days.Count == 0 ? new FileStat { Source = "codex", Stamp = stamp } : s;
        }

        FileStat ScanClaude(FileInfo fi, string stamp)
        {
            var s = new FileStat { Source = "claude", Stamp = stamp };
            var byId = new Dictionary<string, object[]>();   // streaming repeats an assistant message id with growing counts: keep the last
            using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var sr = new StreamReader(fs, Encoding.UTF8, false, 1 << 16))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length < 40 || line.Length > 3000000) continue; string day; int hour; DateTime utc;
                    if (line.IndexOf("\"type\":\"assistant\"", StringComparison.Ordinal) >= 0 && line.IndexOf("\"usage\"", StringComparison.Ordinal) >= 0)
                    {
                        var im = AnaMsgIdRx.Match(line); if (!im.Success || !LocalOf(line, out day, out hour, out utc)) continue;
                        long input = Num1(InRx, line), output = Num1(OutRx, line), cc = Num1(CcRx, line), cr = Num1(CrRx, line);
                        var mm = AnaModelRx.Match(line); byId[im.Groups[1].Value] = new object[] { day, hour, utc, input + output + cc, mm.Success ? mm.Groups[1].Value : "claude", input, output, cr, cc };
                        if (line.IndexOf("\"type\":\"tool_use\"", StringComparison.Ordinal) >= 0) foreach (Match t in AnaClaudeToolRx.Matches(line)) { long c; s.Tools.TryGetValue(t.Groups[1].Value, out c); s.Tools[t.Groups[1].Value] = c + 1; }
                    }
                    else if (line.IndexOf("\"type\":\"user\"", StringComparison.Ordinal) >= 0 && line.IndexOf("\"tool_result\"", StringComparison.Ordinal) < 0 && line.IndexOf("\"isSidechain\":true", StringComparison.Ordinal) < 0)
                    { if (LocalOf(line, out day, out hour, out utc)) Bump(s, day, hour, utc, 1, 0, null); }
                }
            }
            foreach (var v in byId.Values) Bump(s, (string)v[0], (int)v[1], (DateTime)v[2], 1, (long)v[3], (string)v[4], (long)v[5], (long)v[6], (long)v[7], (long)v[8]);
            return s.Days.Count == 0 ? new FileStat { Source = "claude", Stamp = stamp } : s;
        }

        // ---------- estimated cost ----------
        // US dollars per million tokens: input, output, cache read, cache write. Public list prices by model family; editable in the analytics panel.
        static readonly string[] PriceOrder = { "opus", "sonnet", "haiku", "claude", "mini", "gpt", "codex", "other" };
        static readonly Dictionary<string, double[]> DefaultPrices = new Dictionary<string, double[]> {
            { "opus", new[] { 15.0, 75.0, 1.5, 18.75 } }, { "sonnet", new[] { 3.0, 15.0, 0.3, 3.75 } }, { "haiku", new[] { 1.0, 5.0, 0.1, 1.25 } }, { "claude", new[] { 3.0, 15.0, 0.3, 3.75 } },
            { "mini", new[] { 0.25, 2.0, 0.025, 0.25 } }, { "gpt", new[] { 1.25, 10.0, 0.125, 1.25 } }, { "codex", new[] { 1.25, 10.0, 0.125, 1.25 } }, { "other", new[] { 1.25, 10.0, 0.125, 1.25 } } };
        Dictionary<string, double[]> prices;

        Dictionary<string, double[]> Prices()
        {
            lock (anaGate)
            {
                if (prices != null) return prices;
                var p = DefaultPrices.ToDictionary(kv => kv.Key, kv => (double[])kv.Value.Clone());
                try { var l = LoadList("prices.json"); if (l.Count > 0) foreach (var kv in l[0]) { var a = kv.Value as System.Collections.IList; if (a != null && a.Count >= 4 && p.ContainsKey(kv.Key)) p[kv.Key] = new[] { Convert.ToDouble(a[0], CultureInfo.InvariantCulture), Convert.ToDouble(a[1], CultureInfo.InvariantCulture), Convert.ToDouble(a[2], CultureInfo.InvariantCulture), Convert.ToDouble(a[3], CultureInfo.InvariantCulture) }; } } catch (Exception) { }
                return prices = p;
            }
        }
        static string PriceClass(string model)
        {
            string m = (model ?? "").ToLowerInvariant();
            foreach (string k in PriceOrder) if (k != "other" && m.Contains(k)) return k;
            return "other";
        }
        public object PricesGet() { var p = Prices(); return PriceOrder.Select(k => (object)new Dictionary<string, object> { { "Class", k }, { "Input", p[k][0] }, { "Output", p[k][1] }, { "CacheRead", p[k][2] }, { "CacheWrite", p[k][3] }, { "Default", DefaultPrices[k] } }).ToArray(); }
        public object PricesSet(Dictionary<string, object> d)
        {
            var next = DefaultPrices.ToDictionary(kv => kv.Key, kv => (double[])kv.Value.Clone()); var rows = d != null && d.ContainsKey("Rows") ? d["Rows"] as System.Collections.IEnumerable : null;
            if (rows != null) foreach (object o in rows)
                {
                    var r = o as Dictionary<string, object>; if (r == null || !next.ContainsKey(Str(r, "Class"))) continue; double a, b, c, e; var inv = CultureInfo.InvariantCulture;
                    if (Double.TryParse(Str(r, "Input"), NumberStyles.Float, inv, out a) && Double.TryParse(Str(r, "Output"), NumberStyles.Float, inv, out b) && Double.TryParse(Str(r, "CacheRead"), NumberStyles.Float, inv, out c) && Double.TryParse(Str(r, "CacheWrite"), NumberStyles.Float, inv, out e) && a >= 0 && b >= 0 && c >= 0 && e >= 0 && a < 10000 && b < 10000 && c < 10000 && e < 10000) next[Str(r, "Class")] = new[] { a, b, c, e };
                }
            lock (anaGate) { prices = next; SaveList("prices.json", new List<Dictionary<string, object>> { next.ToDictionary(kv => kv.Key, kv => (object)kv.Value) }); }
            return PricesGet();
        }
        static double CostOf(double[] p, long[] x) { return (x[1] * p[0] + x[2] * p[1] + x[3] * p[2] + x[4] * p[3]) / 1e6; }

        const double HobbitTokens = 123000;

        public object Analytics(string range) { return Analytics(range, ""); }
        public object Analytics(string range, string vendor)
        {
            EnsureAnalytics(); vendor = vendor == "codex" || vendor == "claude" ? vendor : ""; var pr = Prices();
            DateTime cutoff = range == "7d" ? DateTime.Now.Date.AddDays(-6) : range == "30d" ? DateTime.Now.Date.AddDays(-29) : DateTime.MinValue; string cutDay = cutoff == DateTime.MinValue ? "" : cutoff.ToString("yyyy-MM-dd");
            long msgs = 0, tokens = 0; int sessions = 0; double cost = 0; var days = new Dictionary<string, long[]>(); var hours = new long[24]; var models = new Dictionary<string, long[]>(); var modelCost = new Dictionary<string, double>(); var vendors = new Dictionary<string, long[]>(); var vendorCost = new Dictionary<string, double>(); var tools = new Dictionary<string, long>(); double longest = 0;
            var modelVendor = new Dictionary<string, string>(); var heat = new Dictionary<string, long[]>(); var dayVendor = new Dictionary<string, long[]>(); var dayCost = new Dictionary<string, double>();
            int done, total;
            lock (anaGate)
            {
                done = anaDone; total = anaTotal;
                foreach (var s in anaFiles.Values)
                {
                    if (vendor != "" && s.Source != vendor) continue;
                    bool any = false; long fm = 0, ft = 0; double fc = 0;
                    foreach (var d in s.Days)
                    {
                        long[] h; if (!heat.TryGetValue(d.Key, out h)) heat[d.Key] = h = new long[2]; h[0] += d.Value[0]; h[1] += d.Value[1];
                        if (cutDay != "" && String.CompareOrdinal(d.Key, cutDay) < 0) continue;
                        any = true; fm += d.Value[0]; ft += d.Value[1]; long[] dvv; if (!dayVendor.TryGetValue(d.Key, out dvv)) dayVendor[d.Key] = dvv = new long[3]; dvv[0] += d.Value[0]; if (s.Source == "codex") dvv[1] += d.Value[1]; else dvv[2] += d.Value[1];
                        long[] a; if (!days.TryGetValue(d.Key, out a)) days[d.Key] = a = new long[2]; a[0] += d.Value[0]; a[1] += d.Value[1];
                    }
                    foreach (var x in s.DM)
                    {
                        int bar = x.Key.IndexOf('|'); string day = x.Key.Substring(0, bar), model = x.Key.Substring(bar + 1);
                        if (cutDay != "" && String.CompareOrdinal(day, cutDay) < 0) continue;
                        double c = CostOf(pr[PriceClass(model)], x.Value); fc += c; double mc; modelCost.TryGetValue(model, out mc); modelCost[model] = mc + c; double dc; dayCost.TryGetValue(day, out dc); dayCost[day] = dc + c;
                    }
                    if (!any) continue;
                    sessions++; msgs += fm; tokens += ft; cost += fc; string vname = s.Source == "claude" ? "Claude" : "Codex (GPT)"; long[] v; if (!vendors.TryGetValue(vname, out v)) vendors[vname] = v = new long[3]; v[0] += fm; v[1] += ft; v[2]++; double vc; vendorCost.TryGetValue(vname, out vc); vendorCost[vname] = vc + fc;
                    if (cutDay == "") { for (int i = 0; i < 24; i++) hours[i] += s.Hours[i]; foreach (var m in s.Models) { long[] a; if (!models.TryGetValue(m.Key, out a)) models[m.Key] = a = new long[2]; a[0] += m.Value[0]; a[1] += m.Value[1]; modelVendor[m.Key] = vname; } foreach (var t in s.Tools) { long c; tools.TryGetValue(t.Key, out c); tools[t.Key] = c + t.Value; } }
                    else
                    {   // for a window, approximate hours, models and tools by the share of the file's activity that fell in it
                        long allM = s.Days.Values.Sum(x => x[0]), allT = s.Days.Values.Sum(x => x[1]); double shareM = allM == 0 ? 0 : (double)fm / allM, shareT = allT == 0 ? 0 : (double)ft / allT;
                        for (int i = 0; i < 24; i++) hours[i] += (long)Math.Round(s.Hours[i] * shareM);
                        foreach (var m in s.Models) { long[] a; if (!models.TryGetValue(m.Key, out a)) models[m.Key] = a = new long[2]; a[0] += (long)Math.Round(m.Value[0] * shareM); a[1] += (long)Math.Round(m.Value[1] * shareT); modelVendor[m.Key] = vname; }
                        foreach (var t in s.Tools) { long c; tools.TryGetValue(t.Key, out c); tools[t.Key] = c + (long)Math.Round(t.Value * shareM); }
                    }
                    if (s.Last > s.First && s.First != DateTime.MaxValue) longest = Math.Max(longest, (s.Last - s.First).TotalMinutes);
                }
            }
            // streaks over the whole history
            var active = new SortedSet<string>(heat.Where(h => h.Value[0] > 0 || h.Value[1] > 0).Select(h => h.Key), StringComparer.Ordinal);
            int cur = 0, best = 0, run = 0; DateTime? prev = null;
            foreach (string k in active) { DateTime dt = DateTime.ParseExact(k, "yyyy-MM-dd", CultureInfo.InvariantCulture); run = prev.HasValue && (dt - prev.Value).TotalDays == 1 ? run + 1 : 1; best = Math.Max(best, run); prev = dt; }
            { DateTime d = DateTime.Now.Date; if (!active.Contains(d.ToString("yyyy-MM-dd"))) d = d.AddDays(-1); while (active.Contains(d.ToString("yyyy-MM-dd"))) { cur++; d = d.AddDays(-1); } }
            int peak = -1; long peakV = 0; for (int i = 0; i < 24; i++) if (hours[i] > peakV) { peakV = hours[i]; peak = i; }
            var fav = models.Where(m => m.Key != "unknown" && m.Key.IndexOf("auto-review", StringComparison.OrdinalIgnoreCase) < 0).OrderByDescending(m => m.Value[0]).FirstOrDefault();
            var busiest = days.OrderByDescending(d => d.Value[1]).FirstOrDefault();
            var grid = new List<object>(); DateTime start = DateTime.Now.Date.AddDays(-(26 * 7 - 1) - (int)DateTime.Now.DayOfWeek);
            for (DateTime d = start; d <= DateTime.Now.Date; d = d.AddDays(1)) { string k = d.ToString("yyyy-MM-dd"); long[] h; heat.TryGetValue(k, out h); grid.Add(new object[] { k, h != null ? h[0] : 0, h != null ? h[1] : 0 }); }
            string firstDay = dayVendor.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
            DateTime seriesStart = range == "7d" ? DateTime.Now.Date.AddDays(-6) : range == "30d" ? DateTime.Now.Date.AddDays(-29) : (firstDay != null ? DateTime.ParseExact(firstDay, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateTime.Now.Date);
            if (range != "7d" && range != "30d" && (DateTime.Now.Date - seriesStart).TotalDays > 119) seriesStart = DateTime.Now.Date.AddDays(-119);
            var daily = new List<object>(); for (DateTime d = seriesStart; d <= DateTime.Now.Date; d = d.AddDays(1)) { string k = d.ToString("yyyy-MM-dd"); long[] v; dayVendor.TryGetValue(k, out v); double dc; dayCost.TryGetValue(k, out dc); daily.Add(new object[] { k, v != null ? v[0] : 0, v != null ? v[1] : 0, v != null ? v[2] : 0, Math.Round(dc, 2) }); }
            var weekdays = new long[7]; foreach (var kv in dayVendor) weekdays[(int)DateTime.ParseExact(kv.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayOfWeek] += kv.Value[0];
            string fun = tokens <= 0 ? "" : tokens >= HobbitTokens ? "You've used ~" + Math.Round(tokens / HobbitTokens).ToString("N0", CultureInfo.InvariantCulture) + "x more tokens than The Hobbit." : "That is about " + Math.Round(tokens * 100 / HobbitTokens) + "% of The Hobbit.";
            long totalModelM = Math.Max(1, models.Values.Sum(m => m[0]));
            return new Dictionary<string, object> {
                { "Range", range }, { "Vendor", vendor }, { "Daily", daily.ToArray() }, { "Weekdays", weekdays.Cast<object>().ToArray() }, { "Indexing", new Dictionary<string, object> { { "Done", done }, { "Total", total }, { "Running", anaThread != null && anaThread.IsAlive } } },
                { "Sessions", sessions }, { "Messages", msgs }, { "Tokens", tokens }, { "Cost", Math.Round(cost, 2) }, { "ActiveDays", days.Count(d => d.Value[0] > 0 || d.Value[1] > 0) }, { "PeakHour", peak },
                { "FavoriteModel", fav.Key ?? "" }, { "CurrentStreak", cur }, { "LongestStreak", best }, { "BusiestDay", busiest.Key ?? "" }, { "BusiestTokens", busiest.Key != null ? busiest.Value[1] : 0L },
                { "AvgTokensPerMessage", msgs > 0 ? tokens / msgs : 0L }, { "LongestSessionMinutes", Math.Round(longest) }, { "Grid", grid.ToArray() }, { "Hours", hours.Cast<object>().ToArray() },
                { "Models", models.OrderByDescending(m => m.Value[0]).Take(30).Select(m => (object)new Dictionary<string, object> { { "Name", m.Key }, { "Vendor", modelVendor.ContainsKey(m.Key) ? modelVendor[m.Key] : "" }, { "Messages", m.Value[0] }, { "Tokens", m.Value[1] }, { "Cost", Math.Round(modelCost.ContainsKey(m.Key) ? modelCost[m.Key] : 0, 2) }, { "Share", Math.Round(m.Value[0] * 100.0 / totalModelM, 1) } }).ToArray() },
                { "Vendors", vendors.OrderByDescending(v => v.Value[1]).Select(v => (object)new Dictionary<string, object> { { "Name", v.Key }, { "Messages", v.Value[0] }, { "Tokens", v.Value[1] }, { "Sessions", v.Value[2] }, { "Cost", Math.Round(vendorCost.ContainsKey(v.Key) ? vendorCost[v.Key] : 0, 2) } }).ToArray() },
                { "Tools", tools.OrderByDescending(t => t.Value).Take(10).Select(t => (object)new Dictionary<string, object> { { "Name", t.Key }, { "Count", t.Value } }).ToArray() },
                { "Fun", fun } };
        }
    }
}
