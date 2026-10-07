using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Laica
{
    /// <summary>
    /// Optional remote access: serves LAICA's own interface over HTTP and forwards the same commands the desktop window uses.
    /// Off by default. Every command needs a bearer token obtained with the password; only backend commands are reachable
    /// (native dialogs, restart and the remote settings themselves live in the desktop host and cannot be called remotely).
    /// </summary>
    public sealed class RemoteServer : IDisposable
    {
        public sealed class Settings { public bool Enabled; public int Port = 8977; public bool Lan; public string Salt, Hash; }
        readonly WorkspaceBackend backend; readonly string assets, configPath;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        readonly object gate = new object();
        readonly Dictionary<string, DateTime> tokens = new Dictionary<string, DateTime>();
        readonly Dictionary<string, List<DateTime>> failures = new Dictionary<string, List<DateTime>>();
        readonly List<Stream> streams = new List<Stream>();
        TcpListener listener; Thread acceptor; Settings settings = new Settings(); string lastError; volatile bool stopping;
        const int MaxBody = 8 * 1024 * 1024;

        public RemoteServer(WorkspaceBackend backend, string dataDir, string assetsDir)
        {
            this.backend = backend; assets = Path.GetFullPath(assetsDir); configPath = Path.Combine(dataDir, "remote.json");
            try { if (File.Exists(configPath)) settings = json.Deserialize<Settings>(File.ReadAllText(configPath)) ?? new Settings(); } catch (Exception) { settings = new Settings(); }
            backend.Changed += state => Broadcast(new Dictionary<string, object> { { "Type", "state" }, { "State", state } });
            backend.Harness.Event += e => Broadcast(new Dictionary<string, object> { { "Type", "harness" }, { "Event", e } });
            backend.Harness.Changed += () => Broadcast(new Dictionary<string, object> { { "Type", "harnessSessions" } });
            backend.Channels.Changed += () => Broadcast(new Dictionary<string, object> { { "Type", "channels" } });
            if (settings.Enabled && !String.IsNullOrEmpty(settings.Hash)) TryStart();
        }

        // ---------- settings ----------
        public object Status()
        {
            var urls = new List<string>();
            if (listener != null) { urls.Add("http://localhost:" + settings.Port + "/"); if (settings.Lan) foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))) urls.Add("http://" + ip + ":" + settings.Port + "/"); }
            return new Dictionary<string, object> { { "Enabled", settings.Enabled }, { "Running", listener != null }, { "Port", settings.Port }, { "Lan", settings.Lan }, { "HasPassword", !String.IsNullOrEmpty(settings.Hash) }, { "Urls", urls.ToArray() }, { "Error", lastError }, { "Sessions", ActiveSessions() } };
        }
        int ActiveSessions() { lock (gate) { Prune(); return tokens.Count; } }
        public object Configure(bool enabled, int port, bool lan, string password)
        {
            if (port < 1024 || port > 65535) throw new ArgumentException("Choose a port between 1024 and 65535.");
            if (!String.IsNullOrEmpty(password))
            {
                if (password.Length < 8) throw new ArgumentException("Use a password of at least 8 characters.");
                byte[] salt = new byte[16]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
                settings.Salt = Convert.ToBase64String(salt); settings.Hash = Convert.ToBase64String(Derive(password, salt));
                lock (gate) tokens.Clear();   // a new password signs everyone out
            }
            if (enabled && String.IsNullOrEmpty(settings.Hash)) throw new ArgumentException("Set a password before turning on remote access.");
            Stop(); settings.Enabled = enabled; settings.Port = port; settings.Lan = lan; lastError = null;
            File.WriteAllText(configPath, json.Serialize(settings));
            if (enabled) { TryStart(); if (listener == null) throw new InvalidOperationException(lastError ?? "Could not start the server."); }
            return Status();
        }
        static byte[] Derive(string password, byte[] salt) { using (var k = new Rfc2898DeriveBytes(password, salt, 100000)) return k.GetBytes(32); }
        static bool SameBytes(byte[] a, byte[] b) { if (a.Length != b.Length) return false; int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i]; return d == 0; }

        void TryStart()
        {
            try
            {
                listener = new TcpListener(settings.Lan ? IPAddress.Any : IPAddress.Loopback, settings.Port); listener.Start(); stopping = false;
                acceptor = new Thread(AcceptLoop) { IsBackground = true, Name = "laica-remote" }; acceptor.Start();
            }
            catch (Exception ex) { lastError = ex.Message; listener = null; }
        }
        void Stop()
        {
            stopping = true; var l = listener; listener = null; if (l != null) { try { l.Stop(); } catch (Exception) { } }
            lock (streams) { foreach (var s in streams) { try { s.Close(); } catch (Exception) { } } streams.Clear(); }
            lock (gate) tokens.Clear();
        }
        public void Dispose() { Stop(); }

        void AcceptLoop()
        {
            var l = listener;
            while (!stopping && l != null)
            {
                TcpClient c; try { c = l.AcceptTcpClient(); } catch (Exception) { return; }
                ThreadPool.QueueUserWorkItem(_ => { try { Handle(c); } catch (Exception) { } finally { try { c.Close(); } catch (Exception) { } } });
            }
        }

        // ---------- HTTP ----------
        sealed class Req { public string Method, Path, Query, Ip; public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); public byte[] Body = new byte[0]; }
        static string ReadHead(Stream s)
        {
            var buf = new List<byte>(); int b, last4 = 0;
            while ((b = s.ReadByte()) >= 0) { buf.Add((byte)b); last4 = (last4 << 8) | b; if (last4 == 0x0D0A0D0A) break; if (buf.Count > 32768) throw new InvalidDataException("Header too large."); }
            return buf.Count == 0 ? null : Encoding.ASCII.GetString(buf.ToArray());
        }
        void Handle(TcpClient client)
        {
            client.ReceiveTimeout = 30000; client.SendTimeout = 30000;
            var stream = client.GetStream(); string head = ReadHead(stream); if (head == null) return;
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries); var parts = lines[0].Split(' ');
            if (parts.Length < 3) { Respond(stream, 400, "text/plain", "Bad request"); return; }
            var req = new Req { Method = parts[0].ToUpperInvariant(), Ip = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString() };
            int q = parts[1].IndexOf('?'); req.Path = Uri.UnescapeDataString(q >= 0 ? parts[1].Substring(0, q) : parts[1]); req.Query = q >= 0 ? parts[1].Substring(q + 1) : "";
            for (int i = 1; i < lines.Length; i++) { int c = lines[i].IndexOf(':'); if (c > 0) req.Headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim(); }
            string cl; int len = 0; if (req.Headers.TryGetValue("Content-Length", out cl)) Int32.TryParse(cl, out len);
            if (len > MaxBody) { Respond(stream, 413, "text/plain", "Too large"); return; }
            if (len > 0) { req.Body = new byte[len]; int got = 0; while (got < len) { int n = stream.Read(req.Body, got, len - got); if (n <= 0) return; got += n; } }
            Route(req, stream);
        }

        void Route(Req r, Stream s)
        {
            if (r.Path == "/api/login" && r.Method == "POST") { Login(r, s); return; }
            if (r.Path == "/api/rpc" && r.Method == "POST") { if (!Authorized(r)) { Respond(s, 401, "application/json", "{\"Error\":\"Sign in again.\"}"); return; } Rpc(r, s); return; }
            if (r.Path == "/api/events" && r.Method == "GET") { if (!Authorized(r)) { Respond(s, 401, "application/json", "{\"Error\":\"Sign in again.\"}"); return; } Sse(s); return; }
            if (r.Path == "/api/ping") { Respond(s, 200, "application/json", "{\"laica\":true}"); return; }
            if (r.Method != "GET" && r.Method != "HEAD") { Respond(s, 405, "text/plain", "Method not allowed"); return; }
            if (r.Path == "/remote-flag.js") { Respond(s, 200, "application/javascript", "window.__LAICA_REMOTE__=true;"); return; }
            string rel = r.Path == "/" ? "index.html" : r.Path.TrimStart('/');
            string full; try { full = Path.GetFullPath(Path.Combine(assets, rel.Replace('/', '\\'))); } catch (Exception) { Respond(s, 400, "text/plain", "Bad path"); return; }
            if (!full.StartsWith(assets + "\\", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) { Respond(s, 404, "text/plain", "Not found"); return; }
            Respond(s, 200, Mime(Path.GetExtension(full)), File.ReadAllBytes(full));
        }
        static string Mime(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant()) { case ".html": return "text/html; charset=utf-8"; case ".js": return "application/javascript"; case ".css": return "text/css"; case ".json": return "application/json"; case ".svg": return "image/svg+xml"; case ".png": return "image/png"; case ".ico": return "image/x-icon"; case ".woff2": return "font/woff2"; case ".map": return "application/json"; default: return "application/octet-stream"; }
        }
        void Respond(Stream s, int code, string type, string body) { Respond(s, code, type, Encoding.UTF8.GetBytes(body)); }
        void Respond(Stream s, int code, string type, byte[] body)
        {
            string status = code == 200 ? "OK" : code == 204 ? "No Content" : code == 400 ? "Bad Request" : code == 401 ? "Unauthorized" : code == 404 ? "Not Found" : code == 405 ? "Method Not Allowed" : code == 413 ? "Payload Too Large" : code == 429 ? "Too Many Requests" : "Error";
            var head = "HTTP/1.1 " + code + " " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
            var hb = Encoding.ASCII.GetBytes(head); s.Write(hb, 0, hb.Length); s.Write(body, 0, body.Length); s.Flush();
        }

        // ---------- auth ----------
        void Prune() { var now = DateTime.UtcNow; foreach (var k in tokens.Where(t => t.Value < now).Select(t => t.Key).ToList()) tokens.Remove(k); }
        bool Authorized(Req r)
        {
            string h; if (!r.Headers.TryGetValue("Authorization", out h) || !h.StartsWith("Bearer ")) return false;
            lock (gate) { Prune(); DateTime exp; return tokens.TryGetValue(h.Substring(7).Trim(), out exp) && exp > DateTime.UtcNow; }
        }
        void Login(Req r, Stream s)
        {
            lock (gate)
            {
                List<DateTime> f; if (!failures.TryGetValue(r.Ip, out f)) failures[r.Ip] = f = new List<DateTime>();
                f.RemoveAll(t => t < DateTime.UtcNow.AddMinutes(-5));
                if (f.Count >= 5) { Respond(s, 429, "application/json", "{\"Error\":\"Too many attempts. Wait a few minutes.\"}"); return; }
            }
            string password = ""; try { var d = json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(r.Body)); object p; if (d.TryGetValue("password", out p) && p != null) password = Convert.ToString(p); } catch (Exception) { }
            bool ok = false;
            if (!String.IsNullOrEmpty(settings.Hash) && password.Length > 0 && password.Length < 512) { try { ok = SameBytes(Derive(password, Convert.FromBase64String(settings.Salt)), Convert.FromBase64String(settings.Hash)); } catch (Exception) { } }
            if (!ok) { lock (gate) failures[r.Ip].Add(DateTime.UtcNow); Thread.Sleep(400); Respond(s, 401, "application/json", "{\"Error\":\"That password isn't right.\"}"); return; }
            byte[] raw = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(raw);
            string token = BitConverter.ToString(raw).Replace("-", "").ToLowerInvariant();
            lock (gate) { tokens[token] = DateTime.UtcNow.AddHours(12); failures.Remove(r.Ip); }
            Respond(s, 200, "application/json", json.Serialize(new Dictionary<string, object> { { "token", token } }));
        }

        // ---------- commands & events ----------
        void Rpc(Req r, Stream s)
        {
            try
            {
                var d = json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(r.Body)); string method = Convert.ToString(d["Method"]); object raw;
                var payload = d.TryGetValue("Payload", out raw) ? raw as Dictionary<string, object> : null;
                var result = backend.HandleAsync(method, payload ?? new Dictionary<string, object>()).Result;
                Respond(s, 200, "application/json", json.Serialize(new Dictionary<string, object> { { "Result", result } }));
            }
            catch (Exception ex) { Exception e = ex; while (e is AggregateException && e.InnerException != null) e = e.InnerException; Respond(s, 400, "application/json", json.Serialize(new Dictionary<string, object> { { "Error", e.Message } })); }
        }
        void Sse(Stream s)
        {
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: keep-alive\r\nX-Accel-Buffering: no\r\n\r\n: ok\n\n");
            s.Write(head, 0, head.Length); s.Flush(); lock (streams) streams.Add(s);
            try { while (!stopping) { Thread.Sleep(15000); lock (s) { var ping = Encoding.ASCII.GetBytes(": ping\n\n"); s.Write(ping, 0, ping.Length); s.Flush(); } } }
            catch (Exception) { }
            finally { lock (streams) streams.Remove(s); }
        }
        void Broadcast(object message)
        {
            Stream[] all; lock (streams) all = streams.ToArray(); if (all.Length == 0) return;
            byte[] data; try { data = Encoding.UTF8.GetBytes("data: " + json.Serialize(message) + "\n\n"); } catch (Exception) { return; }
            foreach (var s in all) { try { lock (s) { s.Write(data, 0, data.Length); s.Flush(); } } catch (Exception) { lock (streams) streams.Remove(s); try { s.Close(); } catch (Exception) { } } }
        }
    }
}
