using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Laica
{
    /// <summary>
    /// What is particular to each agent CLI, in one place: the command line it is started with, and the conversation held with
    /// it while it runs (what is written to it, how its output is read). Adding a CLI that streams JSON means adding a Protocol
    /// here; a CLI that prints text needs only its arguments (VendorArguments) and uses the plain-text protocol.
    /// </summary>
    public sealed partial class HarnessManager
    {
        /// <summary>The command-line arguments of a built-in agent for this turn. Custom agents supply their own.</summary>
        static string VendorArguments(Session s)
        {
            switch (s.Harness)
            {
                case "claude":
                    return "-p --output-format stream-json --input-format stream-json --verbose --permission-prompt-tool stdio --permission-mode "
                        + s.Mode
                        + (UseModel(s) ? " --model " + s.Model : "")
                        + (UseEffort(s) ? " --effort " + s.Effort : "")
                        + (s.ExternalId != null ? " --resume " + s.ExternalId : "");
                case "codex":
                    return CodexArguments(s);
                case "gemini":
                case "qwen":
                    return "-p \" \"" + (UseModel(s) ? " -m " + s.Model : "") + (s.Mode == "yolo" ? " --yolo" : "");
                case "opencode":
                    return "run";
                case "goose":
                    return "run -i -";
                case "aider":
                    return "--yes --no-pretty --message-file -";
                default:
                    return "-p \" \"";
            }
        }

        static string CodexArguments(Session s)
        {
            if (s.Mode == "ask-first")
                return "app-server";
            string review = s.Mode == "approve-for-me" ? "--approve-for-me " : ""; // Codex's own automatic reviewer approves the safe requests and refuses the rest
            string sandbox =
                s.Mode == "approve-for-me" ? ""
                : s.Mode == "danger-full-access" ? "--dangerously-bypass-approvals-and-sandbox"
                : "-s " + (s.Mode == "read-only" ? "read-only" : "workspace-write");
            return s.ExternalId != null
                ? "exec " + review + "resume " + s.ExternalId + " --json --skip-git-repo-check -"
                : "exec "
                    + review
                    + "--json --skip-git-repo-check "
                    + (UseModel(s) ? "-m " + s.Model + " " : "")
                    + (UseEffort(s) ? "-c model_reasoning_effort=" + s.Effort + " " : "")
                    + sandbox
                    + " -";
        }

        /// <summary>One way of talking to a CLI during a turn.</summary>
        abstract class Protocol
        {
            /// <summary>Added to the command line.</summary>
            public virtual string ExtraArgs
            {
                get { return ""; }
            }

            /// <summary>Called once the process has started: sends the prompt the way this CLI expects it.</summary>
            public virtual void Begin(HarnessManager m, Session s, HarnessInfo info, Process proc, string sent)
            {
                s.Interactive = false;
                byte[] bytes = new UTF8Encoding(false).GetBytes(sent);
                try
                {
                    if (!(info.Args ?? "").Contains("{prompt}"))
                        proc.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    proc.StandardInput.Close();
                }
                catch (Exception swallowed)
                {
                    Log.Swallowed(swallowed);
                }
            }

            /// <summary>One line of the CLI's output. JSON protocols parse it and call Event; anything unreadable is logged.</summary>
            public virtual void Line(HarnessManager m, Session s, string line)
            {
                Dictionary<string, object> o;
                try
                {
                    o = m.json.Deserialize<Dictionary<string, object>>(line);
                }
                catch (Exception swallowed)
                {
                    Log.Swallowed(swallowed);
                    m.Emit(s, "log", line, null);
                    return;
                }
                Event(m, s, o, Str(o, "type"));
            }

            protected abstract void Event(HarnessManager m, Session s, Dictionary<string, object> o, string type);

            /// <summary>The output has ended.</summary>
            public virtual void End(HarnessManager m, Session s) { }
        }

        /// <summary>Output that is plain text: each line is part of the answer.</summary>
        sealed class TextProtocol : Protocol
        {
            public override void Line(HarnessManager m, Session s, string line)
            {
                m.Emit(s, "assistant", line, "append");
            }

            protected override void Event(HarnessManager m, Session s, Dictionary<string, object> o, string type) { }
        }

        /// <summary>Claude Code's stream-json (Qwen Code's output has the same shape): for Claude the message goes in as one JSON line and stdin stays open for permission answers.</summary>
        sealed class ClaudeProtocol : Protocol
        {
            readonly bool extraFlag;

            public ClaudeProtocol(bool qwen)
            {
                extraFlag = qwen;
            }

            public override string ExtraArgs
            {
                get { return extraFlag ? " --output-format stream-json" : ""; }
            }

            public override void Begin(HarnessManager m, Session s, HarnessInfo info, Process proc, string sent)
            {
                if (extraFlag)
                {
                    base.Begin(m, s, info, proc, sent); // Qwen Code reads its prompt from stdin like a plain CLI
                    return;
                }
                s.Interactive = true;
                m.WriteLine(
                    s,
                    m.json.Serialize(
                        new Dictionary<string, object>
                        {
                            { "type", "user" },
                            {
                                "message",
                                new Dictionary<string, object> { { "role", "user" }, { "content", sent } }
                            },
                        }
                    )
                );
            }

            protected override void Event(HarnessManager m, Session s, Dictionary<string, object> o, string type)
            {
                m.ParseClaude(s, o, type);
            }
        }

        /// <summary>Codex run non-interactively: the prompt goes in on stdin and the events are read back.</summary>
        sealed class CodexExecProtocol : Protocol
        {
            protected override void Event(HarnessManager m, Session s, Dictionary<string, object> o, string type)
            {
                m.ParseCodex(s, o, type);
            }
        }

        /// <summary>Codex's app server, used when it should ask before acting: a JSON-RPC conversation.</summary>
        sealed class CodexAppProtocol : Protocol
        {
            public override void Begin(HarnessManager m, Session s, HarnessInfo info, Process proc, string sent)
            {
                s.Interactive = true;
                s.TurnPrompt = sent;
                m.WriteLine(
                    s,
                    m.json.Serialize(
                        new Dictionary<string, object>
                        {
                            { "id", 1 },
                            { "method", "initialize" },
                            {
                                "params",
                                new Dictionary<string, object>
                                {
                                    {
                                        "clientInfo",
                                        new Dictionary<string, object>
                                        {
                                            { "name", "laica" },
                                            { "title", "LAICA" },
                                            { "version", AppVersion.Value },
                                        }
                                    },
                                    {
                                        "capabilities",
                                        new Dictionary<string, object> { { "experimentalApi", true } }
                                    },
                                }
                            },
                        }
                    )
                );
            }

            protected override void Event(HarnessManager m, Session s, Dictionary<string, object> o, string type)
            {
                m.ParseCodexApp(s, o);
            }
        }

        /// <summary>Gemini CLI's stream-json: the answer arrives in small pieces that are joined when it pauses or ends.</summary>
        sealed class GeminiProtocol : Protocol
        {
            public override string ExtraArgs
            {
                get { return " --output-format stream-json"; }
            }

            protected override void Event(HarnessManager m, Session s, Dictionary<string, object> o, string type)
            {
                m.ParseGemini(s, o, type);
            }

            public override void End(HarnessManager m, Session s)
            {
                m.FlushText(s);
            }
        }

        static readonly Protocol TextOutput = new TextProtocol(),
            CodexExec = new CodexExecProtocol(),
            CodexApp = new CodexAppProtocol(),
            ClaudeStream = new ClaudeProtocol(false),
            QwenStream = new ClaudeProtocol(true),
            GeminiStream = new GeminiProtocol();

        /// <summary>The protocol for a turn, from the parser name the agent was registered with ("sdk" is Qwen Code's Claude-style stream).</summary>
        static Protocol ProtocolFor(string parser, Session s)
        {
            switch (parser)
            {
                case "text":
                    return TextOutput;
                case "claude":
                    return ClaudeStream;
                case "sdk":
                    return QwenStream;
                case "gemini":
                    return GeminiStream;
                default:
                    return s.Mode == "ask-first" && parser == "codex" ? CodexApp : CodexExec;
            }
        }
    }
}
