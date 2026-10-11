using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Laica
{
    /// <summary>
    /// Keeps the computer awake for a set time so long agent runs are not cut off by sleep ("Insomnia").
    /// Windows holds an execution-state request, macOS runs caffeinate, Linux holds a systemd inhibitor lock.
    /// Optionally it also keeps running with the lid closed. That changes a system setting, so every change is
    /// written to a recovery file first and put back when the timer ends, the app closes, or the app next starts.
    /// </summary>
    public sealed class KeepAwake : IDisposable
    {
        const uint EsContinuous = 0x80000000,
            EsSystemRequired = 0x00000001,
            EsDisplayRequired = 0x00000002;

        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint flags);

        [StructLayout(LayoutKind.Sequential)]
        struct PowerStatus
        {
            public byte AcLine,
                BatteryFlag,
                BatteryPercent,
                Reserved;
            public uint LifeTime,
                FullLifeTime;
        }

        [DllImport("kernel32.dll")]
        static extern bool GetSystemPowerStatus(out PowerStatus status);

        public const int MaxMinutes = 24 * 60;

        readonly object gate = new object();
        readonly string journal;
        readonly Action changed;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        Timer ticker;
        Thread winThread;
        ManualResetEvent winStop;
        Process child;
        bool active,
            display,
            lid,
            disposed;
        int minBattery;
        DateTime until;
        string error = "";

        public KeepAwake(string dataDir, Action changed)
        {
            string dir = Path.Combine(dataDir ?? Path.GetTempPath(), "harness");
            Directory.CreateDirectory(dir);
            journal = Path.Combine(dir, "keepawake-recovery.json");
            this.changed = changed;
            Recover();
        }

        public static string Platform
        {
            get
            {
                if (Path.DirectorySeparatorChar == '\\')
                    return "windows";
                return Directory.Exists("/System/Library/CoreServices") ? "mac" : "linux";
            }
        }

        public object Get()
        {
            lock (gate)
            {
                Power p = ReadPower();
                var r = new Dictionary<string, object>
                {
                    { "Platform", Platform },
                    { "Active", active },
                    { "Display", display },
                    { "Lid", lid },
                    { "MinBattery", minBattery },
                    { "Remaining", active ? Math.Max(0, (int)(until - DateTime.UtcNow).TotalSeconds) : 0 },
                    { "Until", active ? until.ToString("o") : "" },
                    { "Error", error },
                    { "Supported", Supported() },
                    { "HasBattery", p != null },
                    { "BatteryPercent", p != null ? p.Percent : -1 },
                    { "OnBattery", p != null && p.OnBattery },
                    { "LidNote", LidNote() },
                };
                return r;
            }
        }

        static bool Supported()
        {
            string p = Platform;
            if (p == "windows")
                return true;
            return Which(p == "mac" ? "caffeinate" : "systemd-inhibit") != null;
        }

        static string LidNote()
        {
            string p = Platform;
            if (p == "mac")
                return "Asks for your password once, because macOS only lets an administrator change this.";
            if (p == "linux")
                return "Blocks the lid-close action while it runs.";
            return "Sets \"closing the lid\" to do nothing while it runs, then puts your setting back.";
        }

        public object Start(Dictionary<string, object> d)
        {
            int minutes = Num(d, "Minutes", 60);
            minutes = Math.Max(1, Math.Min(MaxMinutes, minutes));
            bool wantDisplay = Flag(d, "Display");
            bool wantLid = Flag(d, "Lid");
            int battery = Math.Max(0, Math.Min(90, Num(d, "MinBattery", 0)));
            lock (gate)
            {
                if (disposed)
                    throw new ObjectDisposedException("KeepAwake");
                StopLocked();
                error = "";
                if (!Supported())
                    throw new InvalidOperationException(
                        Platform == "mac"
                            ? "caffeinate was not found on this Mac."
                            : "Keeping the computer awake needs systemd (systemd-inhibit), which was not found."
                    );
                display = wantDisplay;
                lid = wantLid;
                minBattery = battery;
                until = DateTime.UtcNow.AddMinutes(minutes);
                try
                {
                    Hold(minutes * 60);
                    if (lid)
                        LidOn();
                }
                catch (Exception)
                {
                    StopLocked();
                    throw;
                }
                active = true;
                ticker = new Timer(Tick, null, 15000, 15000);
            }
            Raise();
            return Get();
        }

        public object Stop()
        {
            lock (gate)
            {
                StopLocked();
                error = "";
            }
            Raise();
            return Get();
        }

        void Tick(object state)
        {
            string why = null;
            lock (gate)
            {
                if (!active || disposed)
                    return;
                if (DateTime.UtcNow >= until)
                    why = "";
                else if (minBattery > 0)
                {
                    Power p = ReadPower();
                    if (p != null && p.OnBattery && p.Percent <= minBattery)
                        why = "Stopped keeping the computer awake: the battery fell to " + p.Percent + "%.";
                }
                if (why == null && child != null && child.HasExited)
                    why = "The sleep block ended unexpectedly.";
                if (why != null)
                {
                    StopLocked();
                    error = why;
                }
            }
            if (why != null)
                Raise();
        }

        void StopLocked()
        {
            active = false;
            if (ticker != null)
            {
                ticker.Dispose();
                ticker = null;
            }
            if (winStop != null)
            {
                winStop.Set();
                if (winThread != null)
                    winThread.Join(2000);
                winStop.Close();
                winStop = null;
                winThread = null;
            }
            if (child != null)
            {
                try
                {
                    if (!child.HasExited)
                        child.Kill();
                }
                catch (Exception swallowed)
                {
                    Log.Swallowed(swallowed);
                }
                child.Dispose();
                child = null;
            }
            Recover();
        }

        // ---- the sleep block itself ----

        void Hold(int seconds)
        {
            string p = Platform;
            if (p == "windows")
            {
                uint flags = EsContinuous | EsSystemRequired | (display ? EsDisplayRequired : 0);
                winStop = new ManualResetEvent(false);
                var stop = winStop;
                // The request belongs to the thread that made it, so one thread keeps it for the whole time.
                winThread = new Thread(() =>
                {
                    SetThreadExecutionState(flags);
                    stop.WaitOne();
                    SetThreadExecutionState(EsContinuous);
                })
                {
                    IsBackground = true,
                    Name = "LAICA keep-awake",
                };
                winThread.Start();
                return;
            }
            var psi = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
            if (p == "mac")
            {
                psi.FileName = "caffeinate";
                psi.Arguments =
                    "-i -s " + (display ? "-d " : "") + "-t " + seconds + " -w " + Process.GetCurrentProcess().Id;
            }
            else
            {
                psi.FileName = "systemd-inhibit";
                psi.Arguments =
                    "--what=sleep:idle"
                    + (lid ? ":handle-lid-switch" : "")
                    + " --who=LAICA --why=\"Keeping your agents working\" --mode=block sleep "
                    + seconds;
            }
            child = Process.Start(psi);
            if (child.WaitForExit(500)) // a refusal (no session bus, no permission) shows up at once
                throw new InvalidOperationException(
                    p == "mac"
                        ? "macOS would not start caffeinate."
                        : "The system would not give LAICA a sleep block. A desktop session with systemd is needed."
                );
        }

        // ---- lid-closed (changes a system setting, so it is journalled and always put back) ----

        void LidOn()
        {
            string p = Platform;
            if (p == "linux")
                return; // already part of the inhibitor lock
            var entry = new Dictionary<string, object> { { "Kind", p } };
            if (p == "windows")
            {
                string q = Quiet("powercfg", "/query SCHEME_CURRENT SUB_BUTTONS LIDACTION");
                int ac = PowerIndex(q, "AC"),
                    dc = PowerIndex(q, "DC");
                if (ac < 0 || dc < 0)
                    throw new InvalidOperationException(
                        "Could not read the current lid setting, so it was left alone."
                    );
                entry["Ac"] = ac;
                entry["Dc"] = dc;
                Journal(entry);
                Quiet("powercfg", "/setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 0");
                Quiet("powercfg", "/setdcvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 0");
                Quiet("powercfg", "/setactive SCHEME_CURRENT");
            }
            else
            {
                Journal(entry);
                if (!Admin("pmset -a disablesleep 1"))
                {
                    ClearJournal();
                    throw new InvalidOperationException("Closed-lid mode needs your administrator password.");
                }
            }
        }

        // The last two hex numbers of the query are the AC and DC values (this keeps it independent of the Windows language).
        static int PowerIndex(string text, string which)
        {
            var all = Regex.Matches(text ?? "", @"0x([0-9a-fA-F]+)");
            if (all.Count < 2)
                return -1;
            return Convert.ToInt32(all[all.Count - (which == "AC" ? 2 : 1)].Groups[1].Value, 16);
        }

        /// <summary>Puts back anything an earlier run (or a crashed one) changed.</summary>
        void Recover()
        {
            try
            {
                if (!File.Exists(journal))
                    return;
                var entry = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(journal));
                string kind = entry.ContainsKey("Kind") ? Convert.ToString(entry["Kind"]) : "";
                bool ok = true;
                if (kind == "windows")
                {
                    ok &= Run(
                        "powercfg",
                        "/setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION " + Convert.ToInt32(entry["Ac"])
                    );
                    ok &= Run(
                        "powercfg",
                        "/setdcvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION " + Convert.ToInt32(entry["Dc"])
                    );
                    ok &= Run("powercfg", "/setactive SCHEME_CURRENT");
                }
                else if (kind == "mac")
                    ok = Admin("pmset -a disablesleep 0");
                if (ok)
                    ClearJournal();
                else
                    error =
                        "LAICA could not put your lid-close setting back. Run the keep-awake timer again, or reset it in power settings.";
            }
            catch (Exception swallowed)
            {
                Log.Swallowed(swallowed);
            }
        }

        void Journal(Dictionary<string, object> entry)
        {
            Disk.WriteText(journal, json.Serialize(entry));
        }

        void ClearJournal()
        {
            try
            {
                File.Delete(journal);
            }
            catch (Exception swallowed)
            {
                Log.Swallowed(swallowed);
            }
        }

        // ---- battery ----

        sealed class Power
        {
            public int Percent;
            public bool OnBattery;
        }

        static Power ReadPower()
        {
            try
            {
                string p = Platform;
                if (p == "windows")
                {
                    PowerStatus s;
                    if (!GetSystemPowerStatus(out s) || s.BatteryPercent > 100 || (s.BatteryFlag & 128) != 0)
                        return null; // no battery
                    return new Power { Percent = s.BatteryPercent, OnBattery = s.AcLine == 0 };
                }
                if (p == "mac")
                {
                    string o = Quiet("pmset", "-g batt");
                    var m = Regex.Match(o, @"(\d+)%");
                    return m.Success
                        ? new Power { Percent = int.Parse(m.Groups[1].Value), OnBattery = o.Contains("Battery Power") }
                        : null;
                }
                string root = "/sys/class/power_supply";
                if (!Directory.Exists(root))
                    return null;
                foreach (string bat in Directory.GetDirectories(root, "BAT*"))
                {
                    string cap = Path.Combine(bat, "capacity"),
                        st = Path.Combine(bat, "status");
                    if (!File.Exists(cap))
                        continue;
                    return new Power
                    {
                        Percent = int.Parse(File.ReadAllText(cap).Trim()),
                        OnBattery = File.Exists(st) && File.ReadAllText(st).Trim() == "Discharging",
                    };
                }
            }
            catch (Exception swallowed)
            {
                Log.Swallowed(swallowed);
            }
            return null;
        }

        // ---- small helpers ----

        static string Which(string exe)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (dir.Length == 0)
                    continue;
                string f = Path.Combine(dir, exe);
                if (File.Exists(f))
                    return f;
            }
            return null;
        }

        static string Quiet(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                return o;
            }
        }

        static bool Run(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                return p.WaitForExit(10000) && p.ExitCode == 0;
            }
        }

        /// <summary>Runs a command as an administrator on macOS (the system shows its own password prompt).</summary>
        static bool Admin(string command)
        {
            var psi = new ProcessStartInfo("osascript")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.Arguments = "-e 'do shell script \"" + command + "\" with administrator privileges'";
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                return p.WaitForExit(120000) && p.ExitCode == 0;
            }
        }

        static int Num(Dictionary<string, object> d, string key, int fallback)
        {
            int v;
            return d != null && d.ContainsKey(key) && Int32.TryParse(Convert.ToString(d[key]), out v) ? v : fallback;
        }

        static bool Flag(Dictionary<string, object> d, string key)
        {
            return d != null
                && d.ContainsKey(key)
                && String.Equals(Convert.ToString(d[key]), "true", StringComparison.OrdinalIgnoreCase);
        }

        void Raise()
        {
            try
            {
                if (changed != null)
                    changed();
            }
            catch (Exception swallowed)
            {
                Log.Swallowed(swallowed);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                    return;
                disposed = true;
                StopLocked();
            }
        }
    }
}
