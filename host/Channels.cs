using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Laica
{
    /// <summary>
    /// Chat-platform integrations. Telegram is two-way (pair a chat with a one-time code, then talk to an agent from your phone).
    /// Slack, Discord, Lark, DingTalk, WeCom and generic webhooks receive notifications when scheduled tasks and teams finish.
    /// Secrets (bot token, webhook URLs) are encrypted for the current Windows account.
    /// </summary>
    public sealed class ChannelManager : IDisposable
    {
        readonly HarnessManager harness; readonly string path;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        readonly object gate = new object();
        Dictionary<string, object> config = new Dictionary<string, object>();
        readonly Dictionary<long, string> bound = new Dictionary<long, string>();   // telegram chat -> harness session
        readonly Dictionary<string, int> turnStart = new Dictionary<string, int>();
        readonly Dictionary<string, string> lastApproval = new Dictionary<string, string>();
        Thread poller; volatile bool stopping; string lastError, botName, pairCode; DateTime pairExpires;
        public string ApiBase = "https://api.telegram.org";
        public event Action Changed;

        public ChannelManager(HarnessManager harness, string dataDir)
        {
            this.harness = harness; path = Path.Combine(dataDir, "harness", "channels.json");
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)); if (File.Exists(path)) config = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path)) ?? config; } catch (Exception) { }
            harness.Event += OnEvent;
            lock (gate) { var b = Obj(config, "Bound"); if (b != null) foreach (var kv in b) { long id; if (Int64.TryParse(kv.Key, out id)) bound[id] = Convert.ToString(kv.Value); } }
            if (Tg("Enabled") == "True" && Token() != null) StartPolling();
        }

        // ---------- helpers ----------
        static string Str(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : ""; }
        static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }
        static IEnumerable<object> Arr(Dictionary<string, object> d, string k) { object v; if (d != null && d.TryGetValue(k, out v) && v is System.Collections.IEnumerable && !(v is string)) return ((System.Collections.IEnumerable)v).Cast<object>(); return new object[0]; }
        string Tg(string key) { return Str(Obj(config, "Telegram"), key); }
        void Save() { lock (gate) { var b = new Dictionary<string, object>(); foreach (var kv in bound) b[kv.Key.ToString()] = kv.Value; config["Bound"] = b; try { string t = path + ".tmp"; File.WriteAllText(t, json.Serialize(config)); if (File.Exists(path)) File.Delete(path); File.Move(t, path); } catch (Exception) { } } var h = Changed; if (h != null) h(); }
        static string Protect(string s) { var plain = Encoding.UTF8.GetBytes(s); try { return Convert.ToBase64String(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); } finally { Array.Clear(plain, 0, plain.Length); } }
        static string Unprotect(string s) { if (String.IsNullOrEmpty(s)) return null; try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser)); } catch (Exception) { return null; } }
        string Token() { return Unprotect(Tg("Token")); }
        static bool IsLoopback(Uri u) { return u.IsLoopback; }

        static string Post(string url, string body, int timeoutMs)
        {
            Uri uri; if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri)))) throw new ArgumentException("Webhook addresses must use HTTPS.");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(uri); req.Method = "POST"; req.ContentType = "application/json; charset=utf-8"; req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs; req.AllowAutoRedirect = false; req.UserAgent = "LAICA/1.0";
            var bytes = Encoding.UTF8.GetBytes(body); req.ContentLength = bytes.Length; using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            try { using (var r = (HttpWebResponse)req.GetResponse()) using (var sr = new StreamReader(r.GetResponseStream())) return sr.ReadToEnd(); }
            catch (WebException ex) { var r = ex.Response as HttpWebResponse; throw new InvalidOperationException(r != null ? "The service answered HTTP " + (int)r.StatusCode + "." : "Could not reach the service (" + ex.Status + ")."); }
        }

        // ---------- status / settings ----------
        public object Status()
        {
            var hooks = Arr(config, "Webhooks").Cast<Dictionary<string, object>>().Select(h => (object)new Dictionary<string, object> { { "Id", Str(h, "Id") }, { "Name", Str(h, "Name") }, { "Kind", Str(h, "Kind") } }).ToArray();
            var allowed = bound.Keys.Select(k => (object)k.ToString()).ToArray();
            return new Dictionary<string, object> { { "Telegram", new Dictionary<string, object> { { "Enabled", Tg("Enabled") == "True" }, { "HasToken", Tg("Token") != "" }, { "Connected", poller != null && poller.IsAlive && lastError == null }, { "Error", lastError }, { "Bot", botName }, { "Chats", allowed }, { "Harness", Tg("Harness") }, { "Cwd", Tg("Cwd") }, { "Mode", Tg("Mode") }, { "ServiceId", Tg("ServiceId") }, { "Model", Tg("Model") } } }, { "Webhooks", hooks } };
        }
        public object SaveTelegram(bool enabled, string token, string harnessId, string cwd, string mode, string serviceId, string model)
        {
            if (enabled && String.IsNullOrEmpty(token) && Tg("Token") == "") throw new ArgumentException("Paste the bot token from @BotFather.");
            if (enabled && (String.IsNullOrWhiteSpace(harnessId) || String.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))) throw new ArgumentException("Choose an agent and an existing folder for Telegram chats.");
            StopPolling();
            var t = Obj(config, "Telegram") ?? new Dictionary<string, object>();
            t["Enabled"] = enabled; if (!String.IsNullOrEmpty(token)) t["Token"] = Protect(token.Trim());
            t["Harness"] = harnessId ?? ""; t["Cwd"] = cwd ?? ""; t["Mode"] = mode ?? ""; t["ServiceId"] = serviceId ?? ""; t["Model"] = model ?? ""; config["Telegram"] = t; lastError = null; Save();
            if (enabled) StartPolling(); return Status();
        }
        public string NewPairCode() { var b = new byte[4]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b); lock (gate) { pairCode = (BitConverter.ToUInt32(b, 0) % 900000 + 100000).ToString(); pairExpires = DateTime.UtcNow.AddMinutes(10); return pairCode; } }
        public void Unpair(string chat) { long id; if (Int64.TryParse(chat, out id)) { lock (gate) bound.Remove(id); Save(); } }

        // ---------- Telegram ----------
        void StartPolling() { stopping = false; poller = new Thread(PollLoop) { IsBackground = true, Name = "laica-telegram" }; poller.Start(); }
        void StopPolling() { stopping = true; poller = null; }
        string Api(string method, string body, int timeoutMs) { string token = Token(); if (token == null) throw new InvalidOperationException("No bot token."); return Post(ApiBase + "/bot" + token + "/" + method, body, timeoutMs); }
        void Say(long chat, string text)
        {
            if (String.IsNullOrEmpty(text)) text = "(empty)";
            for (int i = 0; i < text.Length; i += 3500) { try { Api("sendMessage", json.Serialize(new Dictionary<string, object> { { "chat_id", chat }, { "text", text.Substring(i, Math.Min(3500, text.Length - i)) } }), 20000); } catch (Exception) { } }
        }
        void PollLoop()
        {
            long offset = 0; var me = poller;
            try { var r = json.Deserialize<Dictionary<string, object>>(Api("getMe", "{}", 15000)); botName = Str(Obj(r, "result"), "username"); lastError = null; }
            catch (Exception ex) { lastError = "Could not sign in to Telegram: " + ex.Message; var h0 = Changed; if (h0 != null) h0(); return; }
            var h = Changed; if (h != null) h();
            while (!stopping && poller == me)
            {
                try
                {
                    var r = json.Deserialize<Dictionary<string, object>>(Api("getUpdates", json.Serialize(new Dictionary<string, object> { { "offset", offset }, { "timeout", 20 }, { "allowed_updates", new[] { "message" } } }), 40000));
                    foreach (var u in Arr(r, "result").Cast<Dictionary<string, object>>()) { long id; if (Int64.TryParse(Str(u, "update_id"), out id)) offset = Math.Max(offset, id + 1); try { Handle(Obj(u, "message")); } catch (Exception) { } }
                    if (lastError != null) { lastError = null; var h2 = Changed; if (h2 != null) h2(); }
                }
                catch (Exception ex) { lastError = "Telegram connection problem: " + ex.Message; var h3 = Changed; if (h3 != null) h3(); Thread.Sleep(5000); }
            }
        }
        void Handle(Dictionary<string, object> msg)
        {
            if (msg == null) return; string text = Str(msg, "text").Trim(); long chat; if (text == "" || !Int64.TryParse(Str(Obj(msg, "chat"), "id"), out chat)) return;
            string sid; bool paired; lock (gate) paired = bound.TryGetValue(chat, out sid);
            if (text.StartsWith("/pair"))
            {
                string code = text.Length > 5 ? text.Substring(5).Trim() : ""; bool ok; lock (gate) ok = pairCode != null && code == pairCode && DateTime.UtcNow < pairExpires;
                if (!ok) { Say(chat, "That pairing code isn't valid. Open LAICA → Settings → Channels and create a new one."); return; }
                lock (gate) { pairCode = null; if (!bound.ContainsKey(chat)) bound[chat] = ""; } Save(); Say(chat, "Paired. Send a message to start. Commands: /new /stop /status /allow /deny"); return;
            }
            lock (gate) paired = bound.ContainsKey(chat);
            if (!paired) return;   // strangers get no reply at all
            lock (gate) sid = bound[chat];
            if (text == "/new") { lock (gate) bound[chat] = ""; Save(); Say(chat, "Started a fresh conversation."); return; }
            if (text == "/stop") { if (sid != "") { try { harness.Stop(sid); } catch (Exception) { } } Say(chat, "Stopped."); return; }
            if (text == "/status") { Say(chat, sid == "" ? "No active conversation." : "Conversation: " + sid.Substring(0, 8)); return; }
            if (text == "/allow" || text == "/deny")
            {
                string rid; lock (gate) { lastApproval.TryGetValue(sid ?? "", out rid); lastApproval.Remove(sid ?? ""); }
                if (rid == null) { Say(chat, "Nothing is waiting for approval."); return; }
                try { harness.Approve(sid, rid, text == "/allow", false); } catch (Exception ex) { Say(chat, ex.Message); } return;
            }
            if (sid == "" || !SessionAlive(sid))
            {
                var cfg = Obj(config, "Telegram");
                var dto = (Dictionary<string, object>)harness.Create(Str(cfg, "Harness"), Str(cfg, "Cwd"), Str(cfg, "Mode") == "" ? null : Str(cfg, "Mode"), null, null, "Telegram " + chat, false, Str(cfg, "ServiceId") == "" ? null : Str(cfg, "ServiceId"), Str(cfg, "Model") == "" ? null : Str(cfg, "Model"));
                sid = Convert.ToString(dto["Id"]); lock (gate) bound[chat] = sid; Save();
            }
            lock (gate) turnStart[sid] = ((object[])harness.History(sid)).Length;
            try { harness.Send(sid, text); } catch (Exception ex) { Say(chat, ex.Message); }
        }
        bool SessionAlive(string sid) { try { harness.History(sid); return true; } catch (Exception) { return false; } }

        void OnEvent(object ev)
        {
            var e = ev as Dictionary<string, object>; if (e == null) return; string sid = Str(e, "SessionId"), kind = Str(e, "Kind");
            long chat = 0; bool found = false; lock (gate) foreach (var kv in bound) if (kv.Value == sid) { chat = kv.Key; found = true; break; }
            if (!found) return;
            if (kind == "approval") { try { var d = json.Deserialize<Dictionary<string, object>>(Str(e, "Detail")); lock (gate) lastApproval[sid] = Str(d, "RequestId"); Say(chat, "Approval needed: " + Str(e, "Text") + "\n\n" + Str(d, "Input") + "\n\nReply /allow or /deny"); } catch (Exception) { } }
            else if (kind == "error") Say(chat, "Problem: " + Str(e, "Text"));
            else if (kind == "done")
            {
                int start; lock (gate) { if (!turnStart.TryGetValue(sid, out start)) return; turnStart.Remove(sid); }
                var sb = new StringBuilder(); try { var all = ((object[])harness.History(sid)).Cast<Dictionary<string, object>>().ToList(); for (int i = start; i < all.Count; i++) if (Str(all[i], "Kind") == "assistant") { if (sb.Length > 0) sb.Append(Str(all[i], "Detail") == "append" ? "\n" : "\n\n"); sb.Append(Str(all[i], "Text")); } } catch (Exception) { }
                Say(chat, sb.Length == 0 ? "Done." : sb.ToString());
            }
        }

        // ---------- outbound webhooks ----------
        static readonly string[] Kinds = { "slack", "discord", "lark", "dingtalk", "wecom", "generic" };
        public object SaveWebhook(string id, string name, string kind, string url)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the webhook a name.");
            if (!Kinds.Contains(kind)) throw new ArgumentException("Choose a platform.");
            Uri uri; bool urlGiven = !String.IsNullOrWhiteSpace(url);
            if (urlGiven && (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))) throw new ArgumentException("The webhook address must start with https://.");
            lock (gate)
            {
                var list = Arr(config, "Webhooks").Cast<Dictionary<string, object>>().ToList(); var old = list.FirstOrDefault(h => Str(h, "Id") == id);
                if (old == null && !urlGiven) throw new ArgumentException("Paste the webhook address.");
                var rec = new Dictionary<string, object> { { "Id", String.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id }, { "Name", name.Trim() }, { "Kind", kind }, { "Url", urlGiven ? Protect(url.Trim()) : Str(old, "Url") } };
                list.RemoveAll(h => Str(h, "Id") == Str(rec, "Id")); list.Add(rec); config["Webhooks"] = list.Cast<object>().ToArray();
            }
            Save(); return Status();
        }
        public void DeleteWebhook(string id) { lock (gate) config["Webhooks"] = Arr(config, "Webhooks").Cast<Dictionary<string, object>>().Where(h => Str(h, "Id") != id).Cast<object>().ToArray(); Save(); }
        static string Payload(string kind, string text)
        {
            var s = new JavaScriptSerializer();
            switch (kind)
            {
                case "discord": return s.Serialize(new Dictionary<string, object> { { "content", text.Length > 1900 ? text.Substring(0, 1900) : text } });
                case "lark": return s.Serialize(new Dictionary<string, object> { { "msg_type", "text" }, { "content", new Dictionary<string, object> { { "text", text } } } });
                case "dingtalk": case "wecom": return s.Serialize(new Dictionary<string, object> { { "msgtype", "text" }, { "text", new Dictionary<string, object> { { "content", text } } } });
                default: return s.Serialize(new Dictionary<string, object> { { "text", text } });
            }
        }
        public void TestWebhook(string id)
        {
            var h = Arr(config, "Webhooks").Cast<Dictionary<string, object>>().FirstOrDefault(x => Str(x, "Id") == id); if (h == null) throw new ArgumentException("That webhook no longer exists.");
            Post(Unprotect(Str(h, "Url")), Payload(Str(h, "Kind"), "LAICA test message — notifications are working."), 15000);
        }
        /// <summary>Sends a short notification to every webhook. Never throws.</summary>
        public void Notify(string title, string body)
        {
            string text = (title + "\n" + (body ?? "")).Trim(); if (text.Length > 3000) text = text.Substring(0, 3000) + "…";
            foreach (var h in Arr(config, "Webhooks").Cast<Dictionary<string, object>>().ToList())
            { var hook = h; ThreadPool.QueueUserWorkItem(_ => { try { Post(Unprotect(Str(hook, "Url")), Payload(Str(hook, "Kind"), text), 15000); } catch (Exception) { } }); }
        }

        public void Dispose() { StopPolling(); harness.Event -= OnEvent; }
    }
}
