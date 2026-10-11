using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Laica
{
    /// <summary>
    /// A small, safe log: one file a day under the data folder (laica-YYYYMMDD.log), kept for 14 days, at most 5 MB a day.
    /// It never throws and never blocks the caller for long. Failures that the code deliberately carries on after are
    /// recorded with <see cref="Swallowed"/>, so "nothing happened" can be diagnosed afterwards.
    /// </summary>
    public static class Log
    {
        const long DayLimit = 5 * 1024 * 1024;
        static readonly object gate = new object();
        static readonly Dictionary<string, DateTime> lastSeen = new Dictionary<string, DateTime>();
        static readonly Dictionary<string, int> repeats = new Dictionary<string, int>();
        static string dir;
        static string verbose = Environment.GetEnvironmentVariable("LAICA_LOG");

        /// <summary>The folder the logs are written to (empty before <see cref="Init"/>).</summary>
        public static string Folder
        {
            get { return dir ?? ""; }
        }

        public static void Init(string dataDir)
        {
            try
            {
                lock (gate)
                {
                    if (dir != null)
                        return;
                    dir = Path.Combine(dataDir ?? Path.GetTempPath(), "logs");
                    Directory.CreateDirectory(dir);
                    foreach (string old in Directory.GetFiles(dir, "laica-*.log"))
                        if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-14))
                            File.Delete(old);
                }
                Info("log", "LAICA " + AppVersion.Value + " started on " + Environment.OSVersion);
            }
            catch (Exception) { }
        }

        public static void Info(string area, string message)
        {
            Write("I", area, message, null);
        }

        public static void Warn(string area, string message, Exception ex = null)
        {
            Write("W", area, message, ex);
        }

        public static void Error(string area, string message, Exception ex = null)
        {
            Write("E", area, message, ex);
        }

        /// <summary>For a failure the caller carries on after. The same failure from the same line is written once, then counted.</summary>
        public static void Swallowed(
            Exception ex,
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0,
            [CallerMemberName] string member = ""
        )
        {
            if (ex == null || ex is ThreadAbortException || ex is OperationCanceledException)
                return;
            string site = Path.GetFileNameWithoutExtension(file) + ":" + line;
            string key = site + "|" + ex.GetType().Name + "|" + ex.Message;
            lock (gate)
            {
                DateTime seen;
                if (lastSeen.TryGetValue(key, out seen) && (DateTime.UtcNow - seen).TotalMinutes < 10)
                {
                    int n;
                    repeats.TryGetValue(key, out n);
                    repeats[key] = n + 1;
                    return;
                }
                lastSeen[key] = DateTime.UtcNow;
                if (lastSeen.Count > 500)
                {
                    lastSeen.Clear();
                    repeats.Clear();
                }
            }
            Write("S", site + " " + member, "carried on after a failure", ex);
        }

        static void Write(string level, string area, string message, Exception ex)
        {
            try
            {
                if (dir == null)
                    return;
                if (level == "S" && verbose != "debug" && IsRoutine(ex))
                    return;
                var sb = new StringBuilder();
                sb.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("Z ").Append(level).Append(' ');
                sb.Append(area).Append(": ").Append(message);
                if (ex != null)
                {
                    sb.Append("\n    ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
                    string[] stack = (ex.StackTrace ?? "").Split('\n');
                    for (int i = 0; i < stack.Length && i < 4; i++)
                        if (stack[i].Trim().Length > 0)
                            sb.Append("\n      ").Append(stack[i].Trim());
                }
                sb.Append('\n');
                lock (gate)
                {
                    string path = Path.Combine(dir, "laica-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".log");
                    if (File.Exists(path) && new FileInfo(path).Length > DayLimit)
                        return;
                    File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch (Exception) { }
        }

        // Probing for a file that is not there, a half-written line of JSON, a process that has already gone: expected, not worth a line.
        static bool IsRoutine(Exception ex)
        {
            return ex is FileNotFoundException
                || ex is DirectoryNotFoundException
                || ex is FormatException
                || ex is ObjectDisposedException
                || ex is KeyNotFoundException
                || ex is InvalidCastException;
        }
    }

    /// <summary>Writes a file so that a crash or a full disk leaves the old content or the new one, never half of either.</summary>
    public static class Disk
    {
        public static void WriteText(string path, string text)
        {
            WriteBytes(path, new UTF8Encoding(false).GetBytes(text ?? ""));
        }

        public static void WriteBytes(string path, byte[] bytes)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    f.Write(bytes, 0, bytes.Length);
                    f.Flush(true);
                }
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
            }
            catch (Exception)
            {
                try
                {
                    if (File.Exists(tmp))
                        File.Delete(tmp);
                }
                catch (Exception) { }
                throw;
            }
        }
    }
}
