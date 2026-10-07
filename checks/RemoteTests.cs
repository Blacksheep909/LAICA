using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Laica;

public static class RemoteTests
{
    static int failures; static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static void Check(bool ok, string name) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
    static int status;
    static string Do(string method, string url, string body, string token)
    {
        var req = (HttpWebRequest)WebRequest.Create(url); req.Method = method; req.Timeout = 15000;
        if (token != null) req.Headers["Authorization"] = "Bearer " + token;
        if (body != null) { var b = Encoding.UTF8.GetBytes(body); req.ContentType = "application/json"; req.ContentLength = b.Length; using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length); }
        try { using (var r = (HttpWebResponse)req.GetResponse()) { status = (int)r.StatusCode; using (var sr = new StreamReader(r.GetResponseStream())) return sr.ReadToEnd(); } }
        catch (WebException ex) { var r = ex.Response as HttpWebResponse; if (r == null) throw; status = (int)r.StatusCode; using (var sr = new StreamReader(r.GetResponseStream())) return sr.ReadToEnd(); }
    }
    static string Raw(int port, string request)
    {
        using (var c = new TcpClient("127.0.0.1", port)) { var s = c.GetStream(); var b = Encoding.ASCII.GetBytes(request); s.Write(b, 0, b.Length); c.ReceiveTimeout = 5000; var sb = new StringBuilder(); var buf = new byte[4096]; int n; try { while ((n = s.Read(buf, 0, buf.Length)) > 0) sb.Append(Encoding.UTF8.GetString(buf, 0, n)); } catch (Exception) { } return sb.ToString(); }
    }
    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "laica-remote-test-" + Guid.NewGuid().ToString("N")); string assets = Path.Combine(root, "ui"), data = Path.Combine(root, "data");
        Directory.CreateDirectory(assets); Directory.CreateDirectory(data); File.WriteAllText(Path.Combine(assets, "index.html"), "<html>hello-laica</html>"); File.WriteAllText(Path.Combine(root, "secret.txt"), "top-secret");
        int port = new Random().Next(30000, 50000);
        try
        {
            var backend = new WorkspaceBackend(data, Path.Combine(data, "codex"));
            using (var remote = new RemoteServer(backend, data, assets))
            {
                bool shortPw = false; try { remote.Configure(true, port, false, "short"); } catch (ArgumentException) { shortPw = true; }
                Check(shortPw, "remote: short passwords are refused");
                bool noPw = false; try { remote.Configure(true, port, false, null); } catch (ArgumentException) { noPw = true; }
                Check(noPw, "remote: cannot be enabled without a password");
                remote.Configure(true, port, false, "correct-horse-battery");
                var st = (Dictionary<string, object>)remote.Status(); Check((bool)st["Running"] && (bool)st["HasPassword"], "remote: server starts once configured");
                string baseUrl = "http://127.0.0.1:" + port;
                Check(Do("GET", baseUrl + "/", null, null).Contains("hello-laica") && status == 200, "remote: serves the interface");
                Check(Do("GET", baseUrl + "/remote-flag.js", null, null).Contains("__LAICA_REMOTE__"), "remote: serves the remote-mode flag");
                string trav = Raw(port, "GET /%2e%2e/secret.txt HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
                Check(!trav.Contains("top-secret") && (trav.Contains(" 404 ") || trav.Contains(" 400 ")), "remote: path traversal is blocked");
                Do("POST", baseUrl + "/api/rpc", "{\"Method\":\"harnesses\"}", null); Check(status == 401, "remote: commands need a sign-in");
                Do("POST", baseUrl + "/api/rpc", "{\"Method\":\"harnesses\"}", "not-a-token"); Check(status == 401, "remote: a made-up token is rejected");
                Do("POST", baseUrl + "/api/login", "{\"password\":\"wrong-password\"}", null); Check(status == 401, "remote: wrong password is rejected");
                string token = (string)Json.Deserialize<Dictionary<string, object>>(Do("POST", baseUrl + "/api/login", "{\"password\":\"correct-horse-battery\"}", null))["token"];
                Check(status == 200 && token.Length == 64, "remote: right password returns a token");
                string harnesses = Do("POST", baseUrl + "/api/rpc", "{\"Method\":\"harnesses\"}", token);
                Check(status == 200 && harnesses.Contains("Claude Code") && harnesses.Contains("LAICA Agent"), "remote: commands work with the token");
                Do("POST", baseUrl + "/api/rpc", "{\"Method\":\"chooseFolder\"}", token); Check(status == 400, "remote: host-only commands are not reachable");
                Do("POST", baseUrl + "/api/rpc", "{\"Method\":\"remoteConfigure\",\"Payload\":{\"Enabled\":false}}", token); Check(status == 400, "remote: remote settings cannot be changed remotely");

                // live events
                var lines = new List<string>(); var sse = (HttpWebRequest)WebRequest.Create(baseUrl + "/api/events"); sse.Headers["Authorization"] = "Bearer " + token; sse.ReadWriteTimeout = 20000;
                var reader = new Thread(() => { try { using (var r = sse.GetResponse()) using (var sr = new StreamReader(r.GetResponseStream())) { string l; while ((l = sr.ReadLine()) != null) lock (lines) lines.Add(l); } } catch (Exception) { } }) { IsBackground = true }; reader.Start();
                Thread.Sleep(500);
                string fake = Path.Combine(root, "echo.cmd"); File.WriteAllText(fake, "@echo off\r\nmore\r\n");
                Do("POST", baseUrl + "/api/rpc", Json.Serialize(new Dictionary<string, object> { { "Method", "agentSave" }, { "Payload", new Dictionary<string, object> { { "Name", "RemoteEcho" }, { "Command", "cmd.exe" }, { "Args", "/c more" } } } }), token);
                string agentId = ((System.Collections.IEnumerable)Json.Deserialize<Dictionary<string, object>>(Do("POST", baseUrl + "/api/rpc", "{\"Method\":\"agentList\"}", token))["Result"]).Cast<Dictionary<string, object>>().First(a => (string)a["Name"] == "RemoteEcho")["Id"].ToString();
                string sid = ((Dictionary<string, object>)Json.Deserialize<Dictionary<string, object>>(Do("POST", baseUrl + "/api/rpc", Json.Serialize(new Dictionary<string, object> { { "Method", "harnessCreate" }, { "Payload", new Dictionary<string, object> { { "Harness", agentId }, { "Cwd", root } } } }), token))["Result"] as Dictionary<string, object>)["Id"].ToString();
                Do("POST", baseUrl + "/api/rpc", Json.Serialize(new Dictionary<string, object> { { "Method", "harnessSend" }, { "Payload", new Dictionary<string, object> { { "Id", sid }, { "Prompt", "hello remote" } } } }), token);
                for (int i = 0; i < 100; i++) { lock (lines) if (lines.Any(l => l.Contains("hello remote") && l.Contains("assistant"))) break; Thread.Sleep(100); }
                bool pushed; lock (lines) pushed = lines.Any(l => l.StartsWith("data:") && l.Contains("\"Type\":\"harness\"") && l.Contains("hello remote"));
                Check(pushed, "remote: live events stream to the browser");

                for (int i = 0; i < 6; i++) Do("POST", baseUrl + "/api/login", "{\"password\":\"nope-nope-nope\"}", null);
                Check(status == 429, "remote: repeated wrong passwords are rate-limited");

                remote.Configure(true, port, false, "a-brand-new-password");
                Do("POST", baseUrl + "/api/rpc", "{\"Method\":\"harnesses\"}", token); Check(status == 401, "remote: changing the password signs everyone out");
                remote.Configure(false, port, false, null);
                bool refused = false; try { Do("GET", baseUrl + "/", null, null); } catch (WebException) { refused = true; }
                Check(refused, "remote: turning it off closes the port");
            }
            backend.Dispose();
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED"); return failures;
    }
}
