using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Laica
{
    /// <summary>Chats whose "model" is a Workflow-designer team: each message becomes the team's goal and the graph runs to produce the answer.</summary>
    public sealed partial class HarnessManager
    {
        /// <summary>(profile id, goal, folder, token, progress) -> final answer. Supplied by the workspace backend.</summary>
        public Func<string, string, string, CancellationToken, Action<string>, Task<string>> WorkflowRunner;

        void RunWorkflowChat(Session s, string prompt)
        {
            var cts = new CancellationTokenSource();
            lock (gate) { s.Busy = true; s.Ended = false; s.HasSentRules = true; s.Cts = cts; if (s.Title.EndsWith(" chat") || s.Title == "New chat") s.Title = prompt.Length > 40 ? prompt.Substring(0, 40).Replace("\n", " ") + "…" : prompt.Replace("\n", " "); }
            Emit(s, "user", prompt, null); Raise();
            new Thread(() =>
            {
                string error = null;
                try { string result = WorkflowRunner(s.ServiceId, prompt, s.Cwd, cts.Token, line => Emit(s, "log", line, null)).Result; Emit(s, "assistant", result, null); }
                catch (OperationCanceledException) { Emit(s, "log", "Stopped.", null); }
                catch (Exception ex) { error = Unwrap(ex); if (error.StartsWith("Model unavailable")) error += " Open Services and refresh models, then try again."; }
                Finish(s, error);
            }) { IsBackground = true, Name = "workflow-" + s.Id }.Start();
        }
    }
}