using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Laica
{
    /// <summary>
    /// Real plan usage, read from what the agents already write to disk.
    /// Codex records its rate-limit windows (a 5-hour window and a weekly window, with percent used and reset time) in every session log.
    /// Claude Code does not publish plan percentages locally, so LAICA (a) totals the tokens in every Claude transcript, whichever app wrote it,
    /// for today and the last seven days, and (b) keeps any rate_limit_event Claude reports during a chat. Nothing here touches credentials.
    /// </summary>
    public sealed partial class HarnessManager
    {
        sealed class PlanWin { public string Label = "", Source = ""; public double Percent; public DateTime ResetsUtc, SeenUtc; public int Minutes; public bool Stale; }
        readonly Dictionary<string, PlanWin> liveWindows = new Dictionary<string, PlanWin>();      // "claude|five_hour" -> last event Claude reported
        readonly object planGate = new object(); DateTime planAt; List<PlanWin> codexCache = new List<PlanWin>(); string codexPlan = "";
        Dictionary<string, long> claudeDays = new Dictionary<string, long>(), claudeReplies = new Dictionary<string, long>();
        readonly Dictionary<string, KeyValuePair<string, Dictionary<string, KeyValuePair<string, long>>>> claudeFiles = new Dictionary<string, KeyValuePair<string, Dictionary<string, KeyValuePair<string, long>>>>(StringComparer.OrdinalIgnoreCase);

        static string WindowLabel(int minutes) { return minutes == 300 ? "5-hour" : minutes == 10080 ? "Weekly" : minutes % 1440 == 0 ? (minutes / 1440) + "-day" : minutes % 60 == 0 ? (minutes / 60) + "-hour" : minutes + "-minute"; }
        static readonly Regex RateRx = new Regex("\"rate_limits\"\\s*:\\s*\\{[^{}]*?\"primary\"\\s*:\\s*(null|\\{[^{}]*\\})\\s*,\\s*\"secondary\"\\s*:\\s*(null|\\{[^{}]*\\})(?:[^{}]*?\"plan_type\"\\s*:\\s*(?:null|\"([^\"]*)\"))?", RegexOptions.Compiled);
        static readonly Regex NumRx = new Regex("\"(used_percent|window_minutes|resets_at)\"\\s*:\\s*([0-9.eE+-]+)", RegexOptions.Compiled);

        static PlanWin ParseWindow(string obj, string source)
        {
            if (String.IsNullOrEmpty(obj) || obj == "null") return null;
            double used = -1, resets = 0; int minutes = 0;
            foreach (Match m in NumRx.Matches(obj))
            {
                double v; if (!Double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                if (m.Groups[1].Value == "used_percent") used = v; else if (m.Groups[1].Value == "window_minutes") minutes = (int)v; else resets = v;
            }
            if (used < 0) return null;
            var w = new PlanWin { Percent = Math.Max(0, Math.Min(100, used)), Minutes = minutes, Source = source, Label = WindowLabel(minutes) };
            if (resets > 0) w.ResetsUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(resets);
            return w;
        }

        /// <summary>The newest rate-limit reading in the Codex session logs.</summary>
        void RefreshCodex()
        {
            var best = new List<PlanWin>(); DateTime bestSeen = DateTime.MinValue; string plan = "";
            try
            {
                string root = Path.Combine(CodexHomeDir(), "sessions");
                if (Directory.Exists(root))
                {
                    var files = Directory.GetFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories).Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).Take(5).ToList();
                    foreach (var fi in files)
                    {
                        string tail;
                        try
                        {
                            using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            {
                                long take = Math.Min(fs.Length, 3L * 1024 * 1024); fs.Seek(-take, SeekOrigin.End); var buf = new byte[take]; int n = 0; while (n < take) { int r = fs.Read(buf, n, (int)take - n); if (r <= 0) break; n += r; }
                                tail = Encoding.UTF8.GetString(buf, 0, n);
                            }
                        }
                        catch (Exception) { continue; }
                        Match last = null; foreach (Match m in RateRx.Matches(tail)) last = m;
                        if (last == null) continue;
                        DateTime seen = fi.LastWriteTimeUtc; int ts = tail.LastIndexOf("\"timestamp\":\"", last.Index, StringComparison.Ordinal);
                        if (ts >= 0) { DateTime t; int end = tail.IndexOf('"', ts + 13); if (end > ts && DateTime.TryParse(tail.Substring(ts + 13, end - ts - 13), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) seen = t; }
                        if (seen <= bestSeen) continue;
                        var list = new List<PlanWin>(); var a = ParseWindow(last.Groups[1].Value, "Codex"); var b = ParseWindow(last.Groups[2].Value, "Codex");
                        if (a != null) { a.SeenUtc = seen; list.Add(a); } if (b != null) { b.SeenUtc = seen; list.Add(b); }
                        if (list.Count == 0) continue; best = list; bestSeen = seen; plan = last.Groups[3].Success ? last.Groups[3].Value : "";
                    }
                }
            }
            catch (Exception) { }
            foreach (var w in best) if (w.ResetsUtc != DateTime.MinValue && w.ResetsUtc <= DateTime.UtcNow) { w.Percent = 0; w.Stale = true; }   // the window has renewed since that reading
            lock (planGate) { codexCache = best; codexPlan = plan; }
        }

        static readonly Regex TsRx = new Regex("\"timestamp\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled), IdRx = new Regex("\"id\"\\s*:\\s*\"(msg_[A-Za-z0-9_]+)\"", RegexOptions.Compiled);
        static readonly Regex InRx = new Regex("\"input_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled), OutRx = new Regex("\"output_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled), CcRx = new Regex("\"cache_creation_input_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);

        /// <summary>Tokens per local day from every Claude Code transcript touched in the last eight days (any app that wrote them).</summary>
        void RefreshClaude()
        {
            var days = new Dictionary<string, long>(); var replies = new Dictionary<string, long>(); string stamp;
            try
            {
                string root = Path.Combine(ClaudeDir(), "projects");
                if (Directory.Exists(root))
                {
                    var cutoff = DateTime.UtcNow.AddDays(-8);
                    var files = Directory.GetDirectories(root).SelectMany(d => { try { return Directory.GetFiles(d, "*.jsonl"); } catch (Exception) { return new string[0]; } }).Select(f => new FileInfo(f)).Where(f => f.LastWriteTimeUtc > cutoff).OrderByDescending(f => f.LastWriteTimeUtc).Take(60).ToList();
                    foreach (var fi in files)
                    {
                        stamp = fi.Length + "|" + fi.LastWriteTimeUtc.Ticks; KeyValuePair<string, Dictionary<string, KeyValuePair<string, long>>> cached;
                        Dictionary<string, KeyValuePair<string, long>> byId;
                        if (claudeFiles.TryGetValue(fi.FullName, out cached) && cached.Key == stamp) byId = cached.Value;
                        else
                        {
                            byId = new Dictionary<string, KeyValuePair<string, long>>();
                            try
                            {
                                foreach (string line in SharedLines(fi.FullName, 2000000, long.MaxValue))
                                {
                                    if (line.Length > 3000000 || line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0 || line.IndexOf("\"type\":\"assistant\"", StringComparison.Ordinal) < 0) continue;
                                    var tm = TsRx.Match(line); var im = IdRx.Match(line); if (!tm.Success || !im.Success) continue;
                                    DateTime t; if (!DateTime.TryParse(tm.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) continue;
                                    long tokens = 0; foreach (var rx in new[] { InRx, OutRx, CcRx }) { var m = rx.Match(line); if (m.Success) tokens += Int64.Parse(m.Groups[1].Value); }
                                    byId[im.Groups[1].Value] = new KeyValuePair<string, long>(t.ToLocalTime().ToString("yyyy-MM-dd"), tokens);   // streaming repeats an id with growing counts: keep the last
                                }
                            }
                            catch (Exception) { }
                            claudeFiles[fi.FullName] = new KeyValuePair<string, Dictionary<string, KeyValuePair<string, long>>>(stamp, byId);
                        }
                        foreach (var kv in byId.Values) { long cur, rc; days.TryGetValue(kv.Key, out cur); days[kv.Key] = cur + kv.Value; replies.TryGetValue(kv.Key, out rc); replies[kv.Key] = rc + 1; }
                    }
                }
            }
            catch (Exception) { }
            lock (planGate) { claudeDays = days; claudeReplies = replies; }
        }

        void RefreshPlanUsage()
        {
            lock (planGate) { if ((DateTime.UtcNow - planAt).TotalSeconds < 20) return; planAt = DateTime.UtcNow; }
            RefreshCodex(); RefreshClaude();
        }

        long ClaudeTokens(int daysBack)
        {
            lock (planGate) { long sum = 0; for (int i = 0; i < daysBack; i++) { long v; if (claudeDays.TryGetValue(DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd"), out v)) sum += v; } return sum; }
        }

        /// <summary>Called for a Claude "rate_limit_event" so a percentage appears as soon as Claude reports one.</summary>
        void NoteClaudeRateLimit(Dictionary<string, object> o)
        {
            try
            {
                var info = Obj(o, "rate_limit_info"); if (info == null) return;
                string type = Str(info, "rateLimitType"); if (type == "") type = "five_hour";
                double u; bool has = Double.TryParse(Str(info, "utilization"), NumberStyles.Float, CultureInfo.InvariantCulture, out u);
                string status = Str(info, "status"); if (!has) u = status == "rejected" ? 1 : -1; if (u < 0) return;
                var w = new PlanWin { Source = "Claude", Percent = Math.Max(0, Math.Min(100, u <= 1.0 ? u * 100 : u)), SeenUtc = DateTime.UtcNow, Label = type == "five_hour" ? "5-hour" : type == "seven_day" ? "Weekly" : type.Replace('_', ' ') };
                double r; if (Double.TryParse(Str(info, "resetsAt"), NumberStyles.Float, CultureInfo.InvariantCulture, out r) && r > 0) w.ResetsUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(r > 1e12 ? r / 1000 : r);
                lock (planGate) liveWindows["claude|" + type] = w;
            }
            catch (Exception) { }
        }

        static Dictionary<string, object> WinDto(PlanWin w)
        {
            return new Dictionary<string, object> { { "Label", w.Label }, { "Percent", Math.Round(w.Percent, 1) }, { "ResetsUtc", w.ResetsUtc == DateTime.MinValue ? "" : w.ResetsUtc.ToString("o") }, { "SeenUtc", w.SeenUtc.ToString("o") }, { "Source", w.Source }, { "Stale", w.Stale } };
        }

        /// <summary>Adds real plan windows and week totals to the usage rows.</summary>
        void ApplyPlanUsage(List<Dictionary<string, object>> rows)
        {
            RefreshPlanUsage();
            foreach (var r in rows)
            {
                string key = Str(r, "Key"); var wins = new List<PlanWin>(); string source = ""; long week = 0, today = Convert.ToInt64(r["TokensToday"]);
                lock (planGate)
                {
                    if (key == "codex") { wins.AddRange(codexCache); source = "Read from your Codex session logs" + (codexPlan != "" ? " (" + codexPlan + " plan)" : ""); }
                    foreach (var kv in liveWindows) if (kv.Key.StartsWith(key + "|")) wins.Add(kv.Value);
                }
                if (key == "claude") { today = Math.Max(today, ClaudeTokens(1)); week = Math.Max(Convert.ToInt64(r["TokensToday"]), ClaudeTokens(7)); if (source == "") source = "Tokens counted from all your Claude Code transcripts"; r["TokensToday"] = today; }
                else { lock (gate) { VendorUse u; if (usage.TryGetValue(key, out u)) week = WeekTokens(u); } }
                long weekBudget = 0; lock (gate) { VendorUse u2; if (usage.TryGetValue(key, out u2)) weekBudget = u2.WeekBudget; }
                if (key == "claude") { long rep; lock (planGate) claudeReplies.TryGetValue(DateTime.Now.ToString("yyyy-MM-dd"), out rep); r["Replies"] = rep; } else r["Replies"] = 0L;
                r["Windows"] = wins.OrderBy(w => w.Minutes == 0 ? 99999 : w.Minutes).Select(WinDto).ToArray(); r["TokensWeek"] = week; r["WeekBudget"] = weekBudget; r["Source"] = source;
                double top = wins.Count == 0 ? 0 : wins.Max(w => w.Percent); long dBud = Convert.ToInt64(r["Budget"]);
                if (dBud > 0) top = Math.Max(top, today * 100.0 / dBud); if (weekBudget > 0) top = Math.Max(top, week * 100.0 / weekBudget);
                r["TopPercent"] = Math.Round(top, 1);
                if (!(bool)r["Limited"]) r["Warn"] = top >= 100 ? "over" : top >= 80 ? "near" : Str(r, "Warn") == "near" || Str(r, "Warn") == "over" ? Str(r, "Warn") : "";
            }
        }
    }
}
