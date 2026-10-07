using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Laica
{
    /// <summary>Lets the Workflow designer use installed agent CLIs (Claude Code, Gemini, custom agents…) as the "AI service" of a node, so one team can mix Codex, Claude and API models.</summary>
    public sealed partial class HarnessManager
    {
        public const string CliPrefix = "cli:";

        /// <summary>Model choices for every installed agent other than Codex (which has its own catalog) and the built-in LAICA Agent (which uses API services directly).</summary>
        public List<ModelInfo> CliModels()
        {
            var list = new List<ModelInfo>();
            foreach (var h in Harnesses().Where(x => x.Available && x.Id != "codex" && x.Id != "laica"))
            {
                var names = h.Id == "claude" ? new[] { "default", "opus", "sonnet", "haiku" } : h.Id == "gemini" ? new[] { "default", "gemini-2.5-pro", "gemini-2.5-flash" } : new[] { "default" };
                foreach (string m in names) list.Add(new ModelInfo { Id = m, Name = m == "default" ? h.Name + " (default model)" : h.Name + " · " + m, ConnectionId = CliPrefix + h.Id, Efforts = h.Id == "claude" ? new List<string> { "default", "low", "medium", "high", "xhigh", "max" } : new List<string> { "default" } });
            }
            return list;
        }

        /// <summary>
        /// Runs one prompt through an installed agent CLI and returns its text answer. Nodes in a designer team only reason over text
        /// (like API nodes), so Claude Code runs in plan (read-only) mode and nothing is ever approved on the user's behalf.
        /// </summary>
        public Task<string> RunOnceAsync(string harnessId, string model, string prompt, string cwd, CancellationToken token) { return RunOnceAsync(harnessId, model, null, prompt, cwd, token); }
        public async Task<string> RunOnceAsync(string harnessId, string model, string effort, string prompt, string cwd, CancellationToken token)
        {
            var info = Harnesses().FirstOrDefault(h => h.Id == harnessId && h.Available);
            if (info == null) throw new InvalidOperationException("That agent isn't installed on this computer any more.");
            if (info.Id == "codex" || info.Id == "laica") throw new InvalidOperationException("Use the Codex or API connection for this node.");
            if (!String.IsNullOrEmpty(model) && !Regex.IsMatch(model, @"^[A-Za-z0-9._\-]{1,80}$")) throw new ArgumentException("That model name isn't valid.");
            if (String.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) cwd = Environment.CurrentDirectory;
            bool inArg = (info.Args ?? "").Contains("{prompt}");
            string args;
            if (info.Custom) args = (info.Args ?? "").Replace("{prompt}", Quote(prompt)).Replace("{cwd}", Quote(cwd));
            else if (info.Id == "claude") args = "-p --output-format text --permission-mode plan" + (String.IsNullOrEmpty(model) || model == "default" ? "" : " --model " + model) + (ValidEffort(effort) != null ? " --effort " + ValidEffort(effort) : "");
            else args = Arguments(new Session { Harness = info.Id, Mode = info.DefaultMode, Cwd = cwd }, info, prompt);
            string exe = ResolveExe(info.Path);
            var psi = new ProcessStartInfo { FileName = exe, Arguments = args, WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) { psi.Arguments = "/c \"\"" + exe + "\" " + args + "\""; psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"; }
            using (var p = new Process { StartInfo = psi })
            {
                p.Start();
                if (!inArg) { var bytes = new UTF8Encoding(false).GetBytes(prompt); try { p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); } catch (Exception) { } }
                try { p.StandardInput.Close(); } catch (Exception) { }
                var outTask = p.StandardOutput.ReadToEndAsync(); var errTask = p.StandardError.ReadToEndAsync();
                var exited = Task.Run(() => p.WaitForExit());
                var delay = Task.Delay(TimeSpan.FromMinutes(15), token);
                var done = await Task.WhenAny(exited, delay).ConfigureAwait(false);
                if (done != exited) { KillTree(p.Id); token.ThrowIfCancellationRequested(); throw new TimeoutException(info.Name + " took longer than 15 minutes."); }
                string text = (await outTask.ConfigureAwait(false)).Trim(), err = (await errTask.ConfigureAwait(false)).Trim();
                if (p.ExitCode != 0 && text == "") throw new InvalidOperationException(err == "" ? info.Name + " stopped (exit " + p.ExitCode + ")." : err);
                return text;
            }
        }
    }
}
