using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Laica
{
    /// <summary>
    /// Settings side of LAICA computer use. The agent-facing server is LAICA.Computer.exe; this switch (computer-use.json) is what lets it act.
    /// Turning it on registers the server with Claude Code and Codex; Esc, pressed anywhere, stops the agents that were using it.
    /// </summary>
    public sealed partial class HarnessManager
    {
        const string ComputerServerName = "laica-computer";
        Timer computerTimer; int computerSeenStop = -1;

        string ComputerFile() { return Path.Combine(dir, "computer-use.json"); }
        static string ComputerExe() { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LAICA.Computer.exe"); }
        Dictionary<string, object> ComputerRead() { try { return json.Deserialize<Dictionary<string, object>>(File.ReadAllText(ComputerFile())) ?? new Dictionary<string, object>(); } catch (Exception) { return new Dictionary<string, object>(); } }
        static int Int(Dictionary<string, object> d, string k) { int v; return Int32.TryParse(Str(d, k), out v) ? v : 0; }
        void ComputerWrite(Dictionary<string, object> d) { Directory.CreateDirectory(dir); string p = ComputerFile(), t = p + ".tmp"; File.WriteAllText(t, json.Serialize(d)); if (File.Exists(p)) File.Delete(p); File.Move(t, p); }

        public object ComputerGet()
        {
            var g = ComputerRead(); bool on = Str(g, "Enabled") == "True", stopped = on && Int(g, "Epoch") > 0 && Int(g, "StoppedEpoch") == Int(g, "Epoch");
            var installed = new List<string>(); try { foreach (var m in ((object[])((Dictionary<string, object>)Tools())["Mcp"]).Cast<Dictionary<string, object>>()) if (Str(m, "Name") == ComputerServerName) installed.Add(Str(m, "Target")); } catch (Exception) { }
            var all = Harnesses();
            return new Dictionary<string, object> { { "Enabled", on }, { "Stopped", stopped }, { "StoppedUtc", Str(g, "StoppedUtc") }, { "ServerFound", File.Exists(ComputerExe()) }, { "InstalledOn", installed.ToArray() },
                { "Claude", all.Any(h => h.Id == "claude" && h.Available) }, { "Codex", all.Any(h => h.Id == "codex" && h.Available) } };
        }

        /// <summary>Switches computer use on or off. Switching on starts a fresh session (so an earlier Esc no longer blocks it) and registers the server with the installed agents.</summary>
        public object ComputerSet(Dictionary<string, object> d)
        {
            bool enable = d != null && d.ContainsKey("Enabled") && Convert.ToBoolean(d["Enabled"]); bool install = !(d != null && d.ContainsKey("Install") && !Convert.ToBoolean(d["Install"]));
            var g = ComputerRead(); string problems = "";
            if (enable) { if (install && !File.Exists(ComputerExe())) throw new InvalidOperationException("LAICA.Computer.exe is missing from the LAICA folder. Reinstall LAICA."); g["Enabled"] = true; g["Epoch"] = Int(g, "Epoch") + 1; g["EnabledUtc"] = DateTime.UtcNow.ToString("o"); }
            else g["Enabled"] = false;
            ComputerWrite(g);
            if (install)
            {
                foreach (string t in new[] { "claude", "codex" })
                {
                    var h = Harnesses().FirstOrDefault(x => x.Id == t && x.Available); if (h == null) continue;
                    try { try { McpRemove(t, ComputerServerName); } catch (Exception) { } if (enable) McpAdd(t, ComputerServerName, "\"" + ComputerExe() + "\"", "", ""); }
                    catch (Exception ex) { if (enable) problems += (problems == "" ? "" : " ") + h.Name + ": " + ex.Message; }
                }
            }
            Raise(); var r = (Dictionary<string, object>)ComputerGet(); r["Problems"] = problems; return r;
        }

        /// <summary>Called by the desktop app only. Notices an Esc press (the server writes it to computer-use.json) and stops the agents that were driving the computer.</summary>
        void StartComputerWatch()
        {
            if (computerTimer != null) return; computerSeenStop = Int(ComputerRead(), "StoppedEpoch");
            computerTimer = new Timer(_ =>
            {
                try
                {
                    var g = ComputerRead(); int stopEpoch = Int(g, "StoppedEpoch"); if (stopEpoch == computerSeenStop) return; computerSeenStop = stopEpoch;
                    if (stopEpoch <= 0 || stopEpoch != Int(g, "Epoch")) return;
                    List<Session> victims; lock (gate) victims = sessions.Values.Where(s => s.Busy && s.Events.Skip(Math.Max(0, s.Events.Count - 400)).Any(e => Str(e, "Kind") == "tool" && Str(e, "Text").IndexOf(ComputerServerName, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                    foreach (var s in victims) { try { Stop(s.Id); Emit(s, "log", "Stopped: you pressed Esc, so computer use is off until you switch it on again in Settings > Agents.", null); } catch (Exception) { } }
                    Raise();
                }
                catch (Exception) { }
            }, null, 1000, 1000);
        }
    }
}
