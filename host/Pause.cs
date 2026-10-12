using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Laica
{
    /// <summary>
    /// Pause and resume running work. A command-line agent is frozen by suspending its whole process tree (nothing is lost and no
    /// time limit is hit); the built-in LAICA Agent holds at its next step. Teams pause every working member and the leader.
    /// </summary>
    public sealed partial class HarnessManager
    {
        static class Win
        {
            [DllImport("ntdll.dll")]
            public static extern int NtSuspendProcess(IntPtr handle);

            [DllImport("ntdll.dll")]
            public static extern int NtResumeProcess(IntPtr handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

            [DllImport("kernel32.dll")]
            public static extern bool CloseHandle(IntPtr handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            public static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry entry);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            public static extern bool Process32NextW(IntPtr snap, ref ProcessEntry entry);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct ProcessEntry
            {
                public uint Size,
                    Usage,
                    ProcessId;
                public UIntPtr DefaultHeap;
                public uint ModuleId,
                    Threads,
                    ParentProcessId;
                public int PriorityClassBase;
                public uint Flags;

                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
                public string ExeFile;
            }
        }

        /// <summary>The process and every descendant, parents first.</summary>
        static List<int> ProcessTree(int root)
        {
            var kids = new Dictionary<int, List<int>>();
            IntPtr snap = Win.CreateToolhelp32Snapshot(2, 0);
            if (snap != IntPtr.Zero && snap != new IntPtr(-1))
            {
                try
                {
                    var e = new Win.ProcessEntry { Size = (uint)Marshal.SizeOf(typeof(Win.ProcessEntry)) };
                    for (bool ok = Win.Process32FirstW(snap, ref e); ok; ok = Win.Process32NextW(snap, ref e))
                    {
                        List<int> l;
                        if (!kids.TryGetValue((int)e.ParentProcessId, out l))
                        {
                            l = new List<int>();
                            kids[(int)e.ParentProcessId] = l;
                        }
                        l.Add((int)e.ProcessId);
                    }
                }
                finally
                {
                    Win.CloseHandle(snap);
                }
            }
            var result = new List<int> { root };
            var seen = new HashSet<int> { root };
            for (int i = 0; i < result.Count && result.Count < 500; i++)
            {
                List<int> l;
                if (kids.TryGetValue(result[i], out l))
                    foreach (int k in l)
                        if (seen.Add(k))
                            result.Add(k);
            }
            return result;
        }

        [DllImport("libc", EntryPoint = "kill")]
        static extern int sys_kill(int pid, int sig);

        /// <summary>SIGSTOP and SIGCONT differ between macOS (17, 19) and Linux (19, 18).</summary>
        static int UnixSignal(bool stop)
        {
            bool mac = Directory.Exists("/System/Library/CoreServices");
            return stop ? (mac ? 17 : 19) : (mac ? 19 : 18);
        }

        static void SuspendTree(int pid, bool suspend)
        {
            Log.Trace((suspend ? "suspend " : "resume ") + pid);
            if (Path.DirectorySeparatorChar == '/')
            {
                var found = new List<int>();
                CollectChildren(pid, found, 0); // the process and every descendant, parents first
                if (!suspend)
                    found.Reverse();
                // The signal is sent directly rather than by starting `kill`: on macOS, starting a process while another one is
                // being frozen left the caller stuck (seen in CI).
                int signal = UnixSignal(suspend);
                foreach (int target in found)
                {
                    if (target > 1 && target != Process.GetCurrentProcess().Id)
                        sys_kill(target, signal);
                }
                Log.Trace("signalled " + found.Count);
                return;
            }
            var all = ProcessTree(pid);
            if (!suspend)
                all.Reverse();
            foreach (int p in all)
            {
                IntPtr h = Win.OpenProcess(0x0800, false, p);
                if (h == IntPtr.Zero)
                    continue;
                try
                {
                    if (suspend)
                        Win.NtSuspendProcess(h);
                    else
                        Win.NtResumeProcess(h);
                }
                finally
                {
                    Win.CloseHandle(h);
                }
            }
        }

        /// <summary>The built-in agent calls this between steps; it blocks while the chat is paused and wakes immediately on stop.</summary>
        void WaitIfPaused(Session s, CancellationToken token)
        {
            s.Go.Wait(token);
        }

        public void Pause(string id)
        {
            Log.Trace("Pause " + id);
            Session s;
            Process p;
            lock (gate)
            {
                s = Live(Get(id));
                if (!s.Busy)
                    throw new InvalidOperationException("This chat isn't working right now.");
                if (s.Harness == "workflow")
                    throw new InvalidOperationException("A workflow run can't be paused. Stop it instead.");
                if (s.Paused)
                    return;
                s.Paused = true;
                s.Go.Reset();
                p = s.Proc;
            }
            if (p != null)
            {
                try
                {
                    if (!p.HasExited)
                        SuspendTree(p.Id, true);
                }
                catch (Exception swallowed)
                {
                    Log.Swallowed(swallowed);
                }
            }
            Log.Trace("Pause: suspended, announcing");
            Emit(s, "paused", s.Harness == "laica" ? "Paused. The agent will hold at its next step." : "Paused.", null);
            Log.Trace("Pause: announced");
            Raise();
            Log.Trace("Pause: done");
        }

        public void Resume(string id)
        {
            Log.Trace("Resume " + id);
            Session s;
            Process p;
            lock (gate)
            {
                s = Get(id);
                if (!s.Paused && s.Child != null && s.Child.Paused)
                    s = s.Child;
                if (!s.Paused)
                    return;
                s.Paused = false;
                p = s.Proc;
            }
            if (p != null)
            {
                try
                {
                    if (!p.HasExited)
                        SuspendTree(p.Id, false);
                }
                catch (Exception swallowed)
                {
                    Log.Swallowed(swallowed);
                }
            }
            s.Go.Set();
            Emit(s, "resumed", "Resumed.", null);
            Raise();
        }

        /// <summary>Called when a turn ends or is stopped: a finished chat is never left paused.</summary>
        void ClearPause(Session s)
        {
            bool was;
            lock (gate)
            {
                was = s.Paused;
                s.Paused = false;
            }
            s.Go.Set();
            if (was)
                Raise();
        }

        // ---------- teams ----------
        bool TeamPausedById(string teamId)
        {
            if (String.IsNullOrEmpty(teamId))
                return false;
            lock (gate)
            {
                var t = teams.FirstOrDefault(x => Str(x, "Id") == teamId);
                return t != null && Str(t, "Paused") == "True";
            }
        }

        /// <summary>A turn started while its team is paused is paused at once, so nothing slips past the pause.</summary>
        void HonourTeamPause(string sessionId)
        {
            string teamId;
            lock (gate)
            {
                Session s;
                teamId = sessions.TryGetValue(sessionId, out s) ? s.TeamId : null;
            }
            if (TeamPausedById(teamId))
            {
                try
                {
                    Pause(sessionId);
                }
                catch (Exception swallowed)
                {
                    Log.Swallowed(swallowed);
                }
            }
        }

        public object PauseTeam(string id)
        {
            Dictionary<string, object> t;
            var ids = new List<string>();
            lock (gate)
            {
                t = teams.FirstOrDefault(x => Str(x, "Id") == id);
                if (t == null)
                    throw new ArgumentException("That team no longer exists.");
                if (!teamRuns.ContainsKey(id))
                    throw new InvalidOperationException("This team isn't working right now.");
                t["Paused"] = true;
                if (Str(t, "LeaderSessionId") != "")
                    ids.Add(Str(t, "LeaderSessionId"));
                var tasks = Arr(t, "Tasks");
                if (tasks != null)
                    foreach (object o in tasks)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d != null && Str(d, "SessionId") != "")
                            ids.Add(Str(d, "SessionId"));
                    }
                SaveTeams();
            }
            foreach (string sid in ids.Distinct())
            {
                try
                {
                    Pause(sid);
                }
                catch (Exception swallowed)
                {
                    Log.Swallowed(swallowed);
                }
            }
            Raise();
            return TeamDto(t);
        }

        public object ResumeTeam(string id)
        {
            Dictionary<string, object> t;
            var ids = new List<string>();
            lock (gate)
            {
                t = teams.FirstOrDefault(x => Str(x, "Id") == id);
                if (t == null)
                    throw new ArgumentException("That team no longer exists.");
                t["Paused"] = false;
                if (Str(t, "LeaderSessionId") != "")
                    ids.Add(Str(t, "LeaderSessionId"));
                var tasks = Arr(t, "Tasks");
                if (tasks != null)
                    foreach (object o in tasks)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d != null && Str(d, "SessionId") != "")
                            ids.Add(Str(d, "SessionId"));
                    }
                SaveTeams();
            }
            foreach (string sid in ids.Distinct())
            {
                try
                {
                    Resume(sid);
                }
                catch (Exception swallowed)
                {
                    Log.Swallowed(swallowed);
                }
            }
            Raise();
            return TeamDto(t);
        }

        /// <summary>Changes a chat's permissions, model or reasoning effort. It applies from the next message, because each turn starts the agent afresh.</summary>
        public object Configure(string id, string mode, string model, string effort)
        {
            Session s;
            lock (gate)
                s = Get(id);
            var info = Harnesses().FirstOrDefault(h => h.Id == s.Harness);
            if (!String.IsNullOrEmpty(mode))
            {
                if (info == null || !info.Modes.Contains(mode))
                    throw new ArgumentException("That permission level isn't available for this agent.");
                lock (gate)
                    s.Mode = mode;
            }
            if (model != null)
            {
                if (
                    model != ""
                    && model != "default"
                    && !System.Text.RegularExpressions.Regex.IsMatch(model, @"^[A-Za-z0-9._\-]{1,80}$")
                )
                    throw new ArgumentException("That model name isn't valid.");
                lock (gate)
                    s.Model = (model == "" || model == "default") ? null : model;
            }
            if (effort != null)
            {
                string e = effort == "" || effort == "default" ? "" : ValidEffort(effort);
                if (e == null)
                    throw new ArgumentException("That reasoning level isn't valid.");
                lock (gate)
                    s.Effort = e == "" ? null : e;
            }
            SaveSession(s);
            Raise();
            lock (gate)
                return Dto(s);
        }
    }
}
