using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Laica
{
    /// <summary>
    /// LAICA's own agent: talks to any configured API service (OpenAI-compatible or Anthropic) and works inside the chat folder with
    /// file, search, command, web and image tools. Risky tools use the same approval prompts as the CLI agents.
    /// </summary>
    public sealed partial class HarnessManager
    {
        const int MaxSteps = 25;
        const int MaxKeptMessages = 80;

        void RunBuiltin(Session s, HarnessInfo info, string prompt, string sent)
        {
            var svc = services().FirstOrDefault(x => x.Id == s.ServiceId);
            CancellationTokenSource cts = new CancellationTokenSource();
            lock (gate) { s.Busy = true; s.Ended = false; s.HasSentRules = true; s.Cts = cts; if (s.Title.EndsWith(" chat") || s.Title == "New chat") s.Title = prompt.Length > 40 ? prompt.Substring(0, 40).Replace("\n", " ") + "…" : prompt.Replace("\n", " "); }
            Emit(s, "user", prompt, null); Raise();
            if (svc == null) { Finish(s, "The service used by this chat was removed. Close the chat and start a new one."); return; }
            lock (gate) { s.Messages.Add(new Dictionary<string, object> { { "role", "user" }, { "text", sent } }); TrimMessages(s); }
            var worker = new Thread(() =>
            {
                string error = null;
                try { AgentLoop(s, svc, cts.Token); }
                catch (OperationCanceledException) { Emit(s, "log", "Stopped.", null); }
                catch (Exception ex) { error = Unwrap(ex); }
                Finish(s, error);
            }) { IsBackground = true, Name = "laica-agent-" + s.Id };
            worker.Start();
        }

        static string Unwrap(Exception ex) { while (ex is AggregateException && ((AggregateException)ex).InnerExceptions.Count > 0) ex = ((AggregateException)ex).InnerExceptions[0]; return ex.Message; }
        static void TrimMessages(Session s)
        {
            if (s.Messages.Count <= MaxKeptMessages) return;
            int cut = s.Messages.Count - MaxKeptMessages;
            while (cut < s.Messages.Count && Str(s.Messages[cut], "role") != "user") cut++;
            if (cut < s.Messages.Count) s.Messages.RemoveRange(0, cut);
        }
        void CancelBuiltin(Session s)
        {
            CancellationTokenSource c; lock (gate) c = s.Cts;
            if (c != null) { try { c.Cancel(); } catch (ObjectDisposedException) { } }
            lock (gate) foreach (var p in s.Pending.Values) { object w; if (p.TryGetValue("Wait", out w)) { p["Allow"] = false; ((ManualResetEventSlim)w).Set(); } }
        }

        void AgentLoop(Session s, ServiceConnection svc, CancellationToken token)
        {
            for (int step = 0; step < MaxSteps; step++)
            {
                token.ThrowIfCancellationRequested(); WaitIfPaused(s, token);
                var reply = CallModel(s, svc, token);
                string text = Str(reply, "text"); var calls = (List<Dictionary<string, object>>)reply["calls"];
                if (text.Trim() != "") Emit(s, "assistant", text, null);
                lock (gate) s.Messages.Add(new Dictionary<string, object> { { "role", "assistant" }, { "text", text }, { "calls", calls.ToArray() } });
                if (calls.Count == 0) return;
                foreach (var call in calls)
                {
                    token.ThrowIfCancellationRequested(); WaitIfPaused(s, token);
                    string name = Str(call, "name"), args = Str(call, "args"); string result;
                    try { result = ExecuteTool(s, svc, name, args, token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { result = "Error: " + Unwrap(ex); }
                    Emit(s, "tool_result", Trim(result), name);
                    lock (gate) s.Messages.Add(new Dictionary<string, object> { { "role", "tool" }, { "callId", Str(call, "id") }, { "name", name }, { "text", Trim(result) } });
                }
                SaveSession(s);
            }
            Emit(s, "error", "Stopped after " + MaxSteps + " steps. Send a message to let the agent continue.", null);
        }

        // ---------- model calls ----------
        string SystemPrompt(Session s)
        {
            return "You are LAICA Agent, a careful coding and work assistant running on the user's Windows computer. Your working folder is " + s.Cwd + ". " +
                "Use the tools to inspect and change files in that folder; every path is relative to it and you cannot leave it. Read a file before editing it. " +
                "Prefer small, verifiable changes, run checks with run_command when useful, and finish with a short plain-language summary of what you did and anything you could not verify. " +
                "Never invent file contents or command output.";
        }

        Dictionary<string, object> CallModel(Session s, ServiceConnection svc, CancellationToken token)
        {
            bool anthropic = ServicePresets.Normalize(svc.Provider) == ServicePresets.Anthropic;
            List<Dictionary<string, object>> msgs; lock (gate) msgs = s.Messages.ToList();
            var tools = ToolSpecs();
            string body, raw;
            if (anthropic)
            {
                var am = new List<object>();
                for (int i = 0; i < msgs.Count; i++)
                {
                    var m = msgs[i]; string role = Str(m, "role");
                    if (role == "user") am.Add(new Dictionary<string, object> { { "role", "user" }, { "content", Str(m, "text") } });
                    else if (role == "assistant")
                    {
                        var blocks = new List<object>(); if (Str(m, "text").Trim() != "") blocks.Add(new Dictionary<string, object> { { "type", "text" }, { "text", Str(m, "text") } });
                        var cl = Arr(m, "calls"); if (cl != null) foreach (object c in cl) { var cd = (Dictionary<string, object>)c; blocks.Add(new Dictionary<string, object> { { "type", "tool_use" }, { "id", Str(cd, "id") }, { "name", Str(cd, "name") }, { "input", ParseArgs(Str(cd, "args")) } }); }
                        if (blocks.Count == 0) blocks.Add(new Dictionary<string, object> { { "type", "text" }, { "text", "(no reply)" } });
                        am.Add(new Dictionary<string, object> { { "role", "assistant" }, { "content", blocks.ToArray() } });
                    }
                    else
                    {
                        var results = new List<object>();
                        while (i < msgs.Count && Str(msgs[i], "role") == "tool") { results.Add(new Dictionary<string, object> { { "type", "tool_result" }, { "tool_use_id", Str(msgs[i], "callId") }, { "content", Str(msgs[i], "text") } }); i++; }
                        i--; am.Add(new Dictionary<string, object> { { "role", "user" }, { "content", results.ToArray() } });
                    }
                }
                body = json.Serialize(new Dictionary<string, object> { { "model", s.Model }, { "max_tokens", 8192 }, { "system", SystemPrompt(s) }, { "messages", am.ToArray() }, { "tools", tools.Select(t => (object)new Dictionary<string, object> { { "name", t["name"] }, { "description", t["description"] }, { "input_schema", t["parameters"] } }).ToArray() } });
                raw = ApiServices.PostJsonAsync(svc, "messages", body, token).Result;
                var o = json.Deserialize<Dictionary<string, object>>(raw); AddTokens(s, Obj(o, "usage"));
                var sb = new StringBuilder(); var calls = new List<Dictionary<string, object>>();
                var content = Arr(o, "content");
                if (content != null) foreach (object c in content) { var cd = c as Dictionary<string, object>; if (cd == null) continue; if (Str(cd, "type") == "text") sb.Append(Str(cd, "text")); else if (Str(cd, "type") == "tool_use") calls.Add(new Dictionary<string, object> { { "id", Str(cd, "id") }, { "name", Str(cd, "name") }, { "args", json.Serialize(cd.ContainsKey("input") ? cd["input"] : new Dictionary<string, object>()) } }); }
                if (content == null && Obj(o, "error") != null) throw new InvalidOperationException(Str(Obj(o, "error"), "message"));
                return new Dictionary<string, object> { { "text", sb.ToString() }, { "calls", calls } };
            }
            var om = new List<object> { new Dictionary<string, object> { { "role", "system" }, { "content", SystemPrompt(s) } } };
            foreach (var m in msgs)
            {
                string role = Str(m, "role");
                if (role == "user") om.Add(new Dictionary<string, object> { { "role", "user" }, { "content", Str(m, "text") } });
                else if (role == "assistant")
                {
                    var d = new Dictionary<string, object> { { "role", "assistant" }, { "content", Str(m, "text") == "" ? null : Str(m, "text") } };
                    var cl = Arr(m, "calls"); var tcs = new List<object>();
                    if (cl != null) foreach (object c in cl) { var cd = (Dictionary<string, object>)c; tcs.Add(new Dictionary<string, object> { { "id", Str(cd, "id") }, { "type", "function" }, { "function", new Dictionary<string, object> { { "name", Str(cd, "name") }, { "arguments", Str(cd, "args") } } } }); }
                    if (tcs.Count > 0) d["tool_calls"] = tcs.ToArray();
                    om.Add(d);
                }
                else om.Add(new Dictionary<string, object> { { "role", "tool" }, { "tool_call_id", Str(m, "callId") }, { "content", Str(m, "text") } });
            }
            body = json.Serialize(new Dictionary<string, object> { { "model", s.Model }, { "messages", om.ToArray() }, { "stream", false }, { "tools", tools.Select(t => (object)new Dictionary<string, object> { { "type", "function" }, { "function", t } }).ToArray() } });
            raw = ApiServices.PostJsonAsync(svc, "chat/completions", body, token).Result;
            var r = json.Deserialize<Dictionary<string, object>>(raw); AddTokens(s, Obj(r, "usage"));
            var choices = Arr(r, "choices"); Dictionary<string, object> message = null;
            if (choices != null) foreach (object c in choices) { var cd = c as Dictionary<string, object>; if (cd != null) { message = Obj(cd, "message"); break; } }
            if (message == null) { if (Obj(r, "error") != null) throw new InvalidOperationException(Str(Obj(r, "error"), "message")); throw new InvalidOperationException("The service returned no reply."); }
            var outCalls = new List<Dictionary<string, object>>(); var tc = Arr(message, "tool_calls");
            if (tc != null) foreach (object c in tc) { var cd = c as Dictionary<string, object>; var fn = cd == null ? null : Obj(cd, "function"); if (fn == null) continue; outCalls.Add(new Dictionary<string, object> { { "id", Str(cd, "id") != "" ? Str(cd, "id") : "call_" + Guid.NewGuid().ToString("N").Substring(0, 8) }, { "name", Str(fn, "name") }, { "args", Str(fn, "arguments") == "" ? "{}" : Str(fn, "arguments") } }); }
            return new Dictionary<string, object> { { "text", Str(message, "content") }, { "calls", outCalls } };
        }

        object ParseArgs(string args) { try { return json.DeserializeObject(String.IsNullOrWhiteSpace(args) ? "{}" : args); } catch (Exception) { return new Dictionary<string, object>(); } }

        static Dictionary<string, object> Tool(string name, string description, Dictionary<string, object> props, params string[] required)
        {
            return new Dictionary<string, object> { { "name", name }, { "description", description }, { "parameters", new Dictionary<string, object> { { "type", "object" }, { "properties", props }, { "required", required } } } };
        }
        static Dictionary<string, object> P(string description) { return new Dictionary<string, object> { { "type", "string" }, { "description", description } }; }
        static List<Dictionary<string, object>> ToolSpecs()
        {
            return new List<Dictionary<string, object>> {
                Tool("list_dir", "List files and folders in a folder (relative to the working folder).", new Dictionary<string, object> { { "path", P("Folder path, '.' for the working folder") } }),
                Tool("read_file", "Read a text file.", new Dictionary<string, object> { { "path", P("File path") } }, "path"),
                Tool("write_file", "Create or overwrite a text file (parent folders are created).", new Dictionary<string, object> { { "path", P("File path") }, { "content", P("Full file content") } }, "path", "content"),
                Tool("edit_file", "Replace exactly one occurrence of old_string with new_string in a file.", new Dictionary<string, object> { { "path", P("File path") }, { "old_string", P("Exact text to replace; must occur once") }, { "new_string", P("Replacement text") } }, "path", "old_string", "new_string"),
                Tool("search", "Case-insensitive text search across files in a folder.", new Dictionary<string, object> { { "pattern", P("Text to find") }, { "path", P("Folder to search, '.' by default") } }, "pattern"),
                Tool("run_command", "Run a PowerShell command in the working folder (2 minute limit).", new Dictionary<string, object> { { "command", P("PowerShell command line") } }, "command"),
                Tool("fetch_url", "Download a public web page or text file (https only) and return its text.", new Dictionary<string, object> { { "url", P("https URL") } }, "url"),
                Tool("generate_image", "Generate an image from a prompt with the service's image model and save it as a PNG in the working folder.", new Dictionary<string, object> { { "prompt", P("What to draw") }, { "path", P("Output path ending in .png") } }, "prompt", "path")
            };
        }

        // ---------- tools ----------
        bool NeedsApproval(Session s, string tool)
        {
            if (s.Mode == "yolo") return false;
            bool edit = tool == "write_file" || tool == "edit_file";
            if (edit) return s.Mode != "accept-edits";
            return tool == "run_command" || tool == "fetch_url" || tool == "generate_image";
        }

        bool Ask(Session s, string tool, string detail, CancellationToken token)
        {
            bool auto; lock (gate) auto = s.AlwaysAllow.Contains(tool);
            if (auto) return true;
            string key = "b" + Guid.NewGuid().ToString("N").Substring(0, 10); var wait = new ManualResetEventSlim(false);
            var rec = new Dictionary<string, object> { { "Wait", wait }, { "Tool", tool }, { "Allow", false } };
            lock (gate) s.Pending[key] = rec;
            Emit(s, "approval", tool, json.Serialize(new Dictionary<string, object> { { "RequestId", key }, { "Input", detail } }));
            while (!wait.Wait(250)) token.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            return (bool)rec["Allow"];
        }

        string ExecuteTool(Session s, ServiceConnection svc, string name, string rawArgs, CancellationToken token)
        {
            var a = json.Deserialize<Dictionary<string, object>>(String.IsNullOrWhiteSpace(rawArgs) ? "{}" : rawArgs);
            string path = Str(a, "path");
            switch (name)
            {
                case "list_dir":
                    {
                        string dirp = Inside(s.Cwd, path == "" ? "." : path);
                        if (!Directory.Exists(dirp)) return "Error: that folder doesn't exist.";
                        var items = new DirectoryInfo(dirp).GetFileSystemInfos().OrderBy(x => !(x is DirectoryInfo)).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(300).Select(x => x.Name + (x is DirectoryInfo ? "/" : ""));
                        Emit(s, "tool", name, path == "" ? "." : path); return String.Join("\n", items);
                    }
                case "read_file":
                    {
                        string full = Inside(s.Cwd, path); Emit(s, "tool", name, path);
                        var fi = new FileInfo(full); if (!fi.Exists) return "Error: that file doesn't exist.";
                        if (fi.Length > 400000) return "Error: file is larger than 400 KB; use search or read a smaller file.";
                        byte[] bytes = File.ReadAllBytes(full); if (bytes.Take(8000).Any(b => b == 0)) return "Error: that looks like a binary file.";
                        return Encoding.UTF8.GetString(bytes);
                    }
                case "write_file":
                    {
                        string full = Inside(s.Cwd, path); string content = Str(a, "content");
                        if (content.Length > 2 * 1024 * 1024) return "Error: content is too large.";
                        if (NeedsApproval(s, name) && !Ask(s, name, path + "\n\n" + Trim(content), token)) return "The user denied this action.";
                        Emit(s, "tool", name, path); Directory.CreateDirectory(Path.GetDirectoryName(full)); File.WriteAllText(full, content, new UTF8Encoding(false));
                        return "Wrote " + content.Length + " characters to " + path + ".";
                    }
                case "edit_file":
                    {
                        string full = Inside(s.Cwd, path); string oldS = Str(a, "old_string"), newS = Str(a, "new_string");
                        if (!File.Exists(full)) return "Error: that file doesn't exist.";
                        string text = File.ReadAllText(full); int idx = oldS == "" ? -1 : text.IndexOf(oldS, StringComparison.Ordinal);
                        if (idx < 0) return "Error: old_string was not found. Read the file again and copy the text exactly.";
                        if (text.IndexOf(oldS, idx + 1, StringComparison.Ordinal) >= 0) return "Error: old_string occurs more than once; include more surrounding text.";
                        if (NeedsApproval(s, name) && !Ask(s, name, path + "\n\n- " + Trim(oldS) + "\n+ " + Trim(newS), token)) return "The user denied this action.";
                        Emit(s, "tool", name, path); File.WriteAllText(full, text.Substring(0, idx) + newS + text.Substring(idx + oldS.Length), new UTF8Encoding(false));
                        return "Edited " + path + ".";
                    }
                case "search":
                    {
                        string pat = Str(a, "pattern"); if (pat == "") return "Error: give a pattern."; string root = Inside(s.Cwd, path == "" ? "." : path);
                        Emit(s, "tool", name, pat); var hits = new List<string>(); string baseDir = Path.GetFullPath(s.Cwd).TrimEnd('\\') + "\\";
                        foreach (string f in Walk(root)) { if (hits.Count >= 100) break; try { var fi = new FileInfo(f); if (fi.Length > 2 * 1024 * 1024) continue; int n = 0; foreach (string line in File.ReadLines(f)) { n++; if (n == 1 && line.IndexOf('\0') >= 0) break; if (line.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0) { hits.Add(f.Substring(baseDir.Length) + ":" + n + ": " + (line.Length > 200 ? line.Substring(0, 200) : line.Trim())); if (hits.Count >= 100) break; } } } catch (Exception) { } }
                        return hits.Count == 0 ? "No matches." : String.Join("\n", hits);
                    }
                case "run_command":
                    {
                        string cmd = Str(a, "command"); if (cmd.Trim() == "") return "Error: give a command.";
                        if (NeedsApproval(s, name) && !Ask(s, name, cmd, token)) return "The user denied this action.";
                        Emit(s, "tool", name, cmd); return RunPowerShell(s.Cwd, cmd, token);
                    }
                case "fetch_url":
                    {
                        string url = Str(a, "url"); if (NeedsApproval(s, name) && !Ask(s, name, url, token)) return "The user denied this action.";
                        Emit(s, "tool", name, url); return Fetch(url, token);
                    }
                case "generate_image": return GenerateImage(s, svc, Str(a, "prompt"), path, token);
                default: return "Error: unknown tool '" + name + "'.";
            }
        }

        static IEnumerable<string> Walk(string root)
        {
            var skip = new[] { ".git", "node_modules", ".venv", "__pycache__", "dist", "build", "obj", "bin" };
            var stack = new Stack<string>(); stack.Push(root);
            while (stack.Count > 0)
            {
                string d = stack.Pop(); string[] subs = new string[0], files = new string[0];
                try { subs = Directory.GetDirectories(d); files = Directory.GetFiles(d); } catch (Exception) { }
                foreach (string f in files) yield return f;
                foreach (string sub in subs) if (!skip.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) stack.Push(sub);
            }
        }

        static string RunPowerShell(string cwd, string command, CancellationToken token)
        {
            string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Continue'; " + command));
            var psi = new ProcessStartInfo { FileName = ps, Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc, WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true }; if (coEnv != null) coEnv(psi, cwd);
            var sb = new StringBuilder(); object gateSb = new object();
            using (var p = Process.Start(psi))
            {
                p.StandardInput.Close();
                DataReceivedEventHandler on = (o, e) => { if (e.Data != null) lock (gateSb) { if (sb.Length < 20000) sb.AppendLine(e.Data); } };
                p.OutputDataReceived += on; p.ErrorDataReceived += on; p.BeginOutputReadLine(); p.BeginErrorReadLine();
                var sw = Stopwatch.StartNew();
                while (!p.WaitForExit(250)) { if (token.IsCancellationRequested) { KillTree(p.Id); token.ThrowIfCancellationRequested(); } if (sw.Elapsed.TotalSeconds > 120) { KillTree(p.Id); lock (gateSb) return Trim(sb.ToString()) + "\n[stopped after 2 minutes]"; } }
                p.WaitForExit();
                lock (gateSb) return (sb.Length == 0 ? "(no output)" : sb.ToString().TrimEnd()) + "\n[exit code " + p.ExitCode + "]";
            }
        }

        static bool IsPrivate(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return true;
            byte[] b = ip.GetAddressBytes();
            if (b.Length == 4) return b[0] == 10 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC;
        }
        static string Fetch(string url, CancellationToken token)
        {
            Uri uri; if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps) return "Error: only https URLs can be fetched.";
            foreach (var ip in Dns.GetHostAddresses(uri.Host)) if (IsPrivate(ip)) return "Error: that address is on a private network and was blocked.";
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(uri); req.AllowAutoRedirect = false; req.Timeout = 20000; req.UserAgent = "LAICA-Agent/1.0";
            using (token.Register(() => { try { req.Abort(); } catch (Exception) { } }))
            {
                try
                {
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        if ((int)resp.StatusCode >= 300) return "Error: the site redirected (" + (int)resp.StatusCode + "). Redirects are not followed; try the final URL.";
                        using (var st = resp.GetResponseStream()) { var buf = new byte[200000]; int total = 0, n; while (total < buf.Length && (n = st.Read(buf, total, buf.Length - total)) > 0) total += n; string text = Encoding.UTF8.GetString(buf, 0, total);
                            if ((resp.ContentType ?? "").IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0) { text = Regex.Replace(text, @"(?is)<(script|style)[^>]*>.*?</\1>", " "); text = Regex.Replace(text, @"<[^>]+>", " "); text = WebUtility.HtmlDecode(Regex.Replace(text, @"\s+", " ")); }
                            return Trim(text); }
                    }
                }
                catch (WebException ex) { var r = ex.Response as HttpWebResponse; return "Error: " + (r != null ? "HTTP " + (int)r.StatusCode : ex.Status.ToString()); }
            }
        }

        string GenerateImage(Session s, ServiceConnection svc, string prompt, string path, CancellationToken token)
        {
            string provider = ServicePresets.Normalize(svc.Provider);
            if (provider != ServicePresets.Compatible) return "Error: image generation needs an OpenAI-compatible service. The current service doesn't provide images.";
            if (prompt.Trim() == "" || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return "Error: give a prompt and a .png output path.";
            string full = Inside(s.Cwd, path);
            if (NeedsApproval(s, "generate_image") && !Ask(s, "generate_image", path + "\n\n" + Trim(prompt), token)) return "The user denied this action.";
            Emit(s, "tool", "generate_image", path);
            string raw = ApiServices.PostJsonAsync(svc, "images/generations", json.Serialize(new Dictionary<string, object> { { "model", String.IsNullOrEmpty(s.ImageModel) ? "gpt-image-1" : s.ImageModel }, { "prompt", prompt }, { "size", "1024x1024" }, { "n", 1 } }), token).Result;
            var o = json.Deserialize<Dictionary<string, object>>(raw); var data = Arr(o, "data"); string b64 = null;
            if (data != null) foreach (object d in data) { var dd = d as Dictionary<string, object>; if (dd != null) { b64 = Str(dd, "b64_json"); break; } }
            if (String.IsNullOrEmpty(b64)) return "Error: the service returned no image data.";
            Directory.CreateDirectory(Path.GetDirectoryName(full)); File.WriteAllBytes(full, Convert.FromBase64String(b64));
            return "Saved the image to " + path + ".";
        }
    }
}
