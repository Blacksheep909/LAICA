using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Laica;

public sealed class FakeTelegram : IDisposable
{
    public readonly HttpListener Listener = new HttpListener(); public readonly int Port;
    public readonly List<string> Sent = new List<string>(), Hooks = new List<string>(); readonly Queue<string> updates = new Queue<string>(); int nextId = 1;
    public FakeTelegram() { Port = new Random().Next(20000, 40000); for (int i = 0; ; i++) { try { Listener.Prefixes.Clear(); Listener.Prefixes.Add("http://127.0.0.1:" + Port + "/"); Listener.Start(); break; } catch (Exception) { if (i > 20) throw; Port++; } } new Thread(Run) { IsBackground = true }.Start(); }
    public void Say(long chat, string text) { lock (updates) updates.Enqueue("{\"update_id\":" + (nextId++) + ",\"message\":{\"chat\":{\"id\":" + chat + "},\"text\":\"" + text.Replace("\"", "\\\"") + "\"}}"); }
    public string[] SentCopy() { lock (Sent) return Sent.ToArray(); }
    public string[] HooksCopy() { lock (Hooks) return Hooks.ToArray(); }
    void Run()
    {
        while (Listener.IsListening)
        {
            HttpListenerContext c; try { c = Listener.GetContext(); } catch (Exception) { return; }
            string body; using (var r = new StreamReader(c.Request.InputStream)) body = r.ReadToEnd(); string p = c.Request.Url.AbsolutePath, reply = "{\"ok\":true,\"result\":[]}";
            if (p.EndsWith("/getMe")) reply = "{\"ok\":true,\"result\":{\"username\":\"laica_test_bot\"}}";
            else if (p.EndsWith("/getUpdates")) { string u = null; for (int i = 0; i < 6 && u == null; i++) { lock (updates) if (updates.Count > 0) u = updates.Dequeue(); if (u == null) Thread.Sleep(100); } reply = "{\"ok\":true,\"result\":[" + (u ?? "") + "]}"; }
            else if (p.EndsWith("/sendMessage")) { lock (Sent) Sent.Add(body); }
            else if (p.EndsWith("/hook")) { lock (Hooks) Hooks.Add(body); reply = "ok"; }
            var bytes = Encoding.UTF8.GetBytes(reply); try { c.Response.OutputStream.Write(bytes, 0, bytes.Length); c.Response.Close(); } catch (Exception) { }
        }
    }
    public void Dispose() { try { Listener.Stop(); } catch (Exception) { } }
}

public static class ChannelTests
{
    static int failures;
    static void Check(bool ok, string name) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
    static bool Wait(Func<bool> p, int ms = 8000) { for (int i = 0; i < ms / 50; i++) { if (p()) return true; Thread.Sleep(50); } return p(); }
    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "laica-channel-test-" + Guid.NewGuid().ToString("N")), work = Path.Combine(root, "work"); Directory.CreateDirectory(work);
        try
        {
            using (var api = new FakeTelegram())
            using (var hm = new HarnessManager(root))
            using (var ch = new ChannelManager(hm, root))
            {
                ch.ApiBase = "http://127.0.0.1:" + api.Port; hm.Completed += ch.Notify;
                hm.SaveAgent(new Dictionary<string, object> { { "Name", "ChanEcho" }, { "Command", "cmd.exe" }, { "Args", "/c more" } });
                string echo = hm.Harnesses().First(h => h.Name == "ChanEcho").Id;
                bool noToken = false; try { ch.SaveTelegram(true, "", echo, work, "", "", ""); } catch (ArgumentException) { noToken = true; }
                Check(noToken, "telegram: needs a bot token");
                ch.SaveTelegram(true, "123:SECRET-TOKEN", echo, work, "", "", "");
                Check(Wait(() => ((Dictionary<string, object>)((Dictionary<string, object>)ch.Status())["Telegram"])["Bot"] as string == "laica_test_bot"), "telegram: connects and learns the bot name");
                Check(!File.ReadAllText(Path.Combine(root, "harness", "channels.json")).Contains("SECRET-TOKEN"), "telegram: bot token is stored encrypted");

                api.Say(222, "hello from a stranger"); Thread.Sleep(1500);
                Check(api.SentCopy().Length == 0 && ((object[])hm.List()).Length == 0, "telegram: unpaired chats are ignored completely");
                string code = ch.NewPairCode(); api.Say(111, "/pair 000000");
                Check(Wait(() => api.SentCopy().Any(s => s.Contains("pairing code"))), "telegram: wrong pairing code is refused");
                api.Say(111, "/pair " + code);
                Check(Wait(() => api.SentCopy().Any(s => s.Contains("Paired"))), "telegram: correct code pairs the chat");
                api.Say(111, "/pair " + code); Thread.Sleep(1200);
                Check(api.SentCopy().Count(s => s.Contains("Paired")) == 1, "telegram: a pairing code works only once");
                api.Say(111, "hello bot");
                Check(Wait(() => api.SentCopy().Any(s => s.Contains("hello bot") && s.Contains("\"chat_id\":111"))), "telegram: message reaches the agent and the reply comes back");
                Check(((object[])hm.List()).Length == 1, "telegram: one conversation was created for the chat");
                api.Say(111, "/new"); api.Say(111, "second chat"); Check(Wait(() => ((object[])hm.List()).Length == 2), "telegram: /new starts a fresh conversation");
                var tg = (Dictionary<string, object>)((Dictionary<string, object>)ch.Status())["Telegram"]; Check(((object[])tg["Chats"]).Contains("111"), "telegram: status lists the paired chat");
                ch.Unpair("111"); api.Say(111, "after unpair"); Thread.Sleep(1500); Check(!api.SentCopy().Any(s => s.Contains("after unpair")), "telegram: an unpaired chat is ignored again");

                bool http = false; try { ch.SaveWebhook("", "Bad", "slack", "http://example.com/hook"); } catch (ArgumentException) { http = true; }
                Check(http, "webhooks: plain http to the internet is refused");
                string hook = "http://127.0.0.1:" + api.Port + "/hook";
                ch.SaveWebhook("", "Team Slack", "slack", hook); ch.SaveWebhook("", "Lark", "lark", hook); ch.SaveWebhook("", "Ding", "dingtalk", hook); ch.SaveWebhook("", "Disc", "discord", hook);
                Check(!File.ReadAllText(Path.Combine(root, "harness", "channels.json")).Contains("/hook"), "webhooks: addresses are stored encrypted");
                var st = (Dictionary<string, object>)ch.Status(); Check(((object[])st["Webhooks"]).Length == 4 && !new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(st).Contains("/hook"), "webhooks: status never reveals addresses");
                ch.Notify("Build done", "all green");
                Check(Wait(() => api.HooksCopy().Length == 4), "webhooks: every channel is notified");
                var hooksText = String.Join("\n", api.HooksCopy());
                Check(hooksText.Contains("\"text\":\"Build done\\nall green\"") && hooksText.Contains("\"msg_type\":\"text\"") && hooksText.Contains("\"msgtype\":\"text\"") && hooksText.Contains("\"content\":\"Build done"), "webhooks: Slack, Lark, DingTalk and Discord each get their own format");

                var task = (Dictionary<string, object>)hm.SaveTask(new Dictionary<string, object> { { "Name", "Nightly" }, { "Kind", "once" }, { "Prompt", "nightly run" }, { "Harness", echo }, { "Cwd", work }, { "RunAt", DateTime.Now.AddMinutes(-1).ToString("s") }, { "Enabled", false } });
                int before = api.HooksCopy().Length; hm.RunTask((string)task["Id"]);
                Check(Wait(() => api.HooksCopy().Length >= before + 4 && api.HooksCopy().Any(x => x.Contains("Task finished: ") && x.Contains("nightly run"))), "webhooks: finished scheduled tasks send a notification with the result");
            }
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED"); return failures;
    }
}
