using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Laica
{
    // Small loopback-only contract checks. Invoke ServiceTests.RunAsync() from a developer test harness.
    public static class ServiceTests
    {
        static int assertions;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static int Main()
        {
            try
            {
                int count = RunAsync().GetAwaiter().GetResult();
                Console.WriteLine("ServiceTests passed: " + count);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        public static async Task<int> RunAsync()
        {
            assertions = 0;
            var conn = new ServiceConnection { Id = "test", Name = "Local test", BaseUrl = "http://127.0.0.1:1/v1", KeyEnvironmentVariable = "", ProtectedKey = "" };

            using (var server = new LocalServer(delegate(Request req)
            {
                Assert(req.Method == "GET" && req.Path == "/v1/models", "models endpoint uses GET");
                return Reply(200, "{\"data\":[{\"id\":\"model-a\"},{\"id\":\"model-a\"},{\"id\":\"model-b\"}]}");
            }))
            {
                conn.BaseUrl = server.BaseUrl + "/v1";
                var models = await ApiServices.LoadModelsAsync(conn, CancellationToken.None);
                Assert(models.Count == 2 && models[0].Id == "model-a" && models[1].Id == "model-b", "catalog removes duplicate IDs preserving order");
                Assert(models[0].Efforts.Count == 1 && models[0].Efforts[0] == "default", "generic model advertises default effort only");
                Assert(models[0].ConnectionId == "test", "catalog models retain connection ownership");
            }

            var node = new AgentNode { Id = "agent-1", Name = "Worker", Role = "implementer", Job = "Only answer the assigned prompt", Model = "vendor/exact-model", Effort = "default" };
            using (var server = new LocalServer(delegate(Request req)
            {
                Assert(req.Method == "POST" && req.Path == "/v1/chat/completions", "chat uses expected endpoint and method");
                var payload = (Dictionary<string, object>)Json.DeserializeObject(req.Body);
                Assert((string)payload["model"] == node.Model, "requested model is sent exactly");
                Assert(!(payload.ContainsKey("reasoning_effort")), "default effort is omitted");
                Assert((bool)payload["stream"] == false, "streaming is disabled");
                var messages = (object[])payload["messages"];
                Assert((string)((Dictionary<string, object>)messages[0])["role"] == "system", "scope is sent as a system message");
                Assert((string)((Dictionary<string, object>)messages[1])["content"] == "do task", "prompt is sent as user content");
                return Reply(200, "{\"model\":\"vendor/exact-model\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"done\"}}]}");
            }))
            {
                conn.BaseUrl = server.BaseUrl + "/v1";
                Assert(await ApiServices.ExecuteAsync(conn, node, "do task", CancellationToken.None) == "done", "completion text is returned");
            }

            node.Effort = "high";
            using (var server = new LocalServer(delegate(Request req)
            {
                var payload = (Dictionary<string, object>)Json.DeserializeObject(req.Body);
                Assert((string)payload["reasoning_effort"] == "high", "configured non-default effort is passed");
                return Reply(200, "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"ok\"}}]}");
            }))
            {
                conn.BaseUrl = server.BaseUrl + "/v1";
                await ApiServices.ExecuteAsync(conn, node, "x", CancellationToken.None);
            }

            using (var server = new LocalServer(delegate(Request req) { return Reply(400, "private response body must not leak"); }))
            {
                conn.BaseUrl = server.BaseUrl;
                var ex = await Throws<InvalidOperationException>(delegate { return ApiServices.LoadModelsAsync(conn, CancellationToken.None); });
                Assert(ex.Message.Contains("HTTP 400") && !ex.Message.Contains("private response body"), "HTTP errors expose status only, not response body");
            }

            using (var server = new LocalServer(delegate(Request req) { return new ReplyData(302, "", "Location: http://127.0.0.1/steal\r\n"); }))
            {
                conn.BaseUrl = server.BaseUrl;
                var ex = await Throws<InvalidOperationException>(delegate { return ApiServices.LoadModelsAsync(conn, CancellationToken.None); });
                Assert(ex.Message.Contains("redirected"), "redirects are rejected");
            }

            using (var server = new LocalServer(async delegate(Request req) { await Task.Delay(10000); return Reply(200, "{}"); }))
            using (var cancel = new CancellationTokenSource())
            {
                conn.BaseUrl = server.BaseUrl;
                cancel.CancelAfter(100);
                await Throws<OperationCanceledException>(delegate { return ApiServices.LoadModelsAsync(conn, cancel.Token); });
                Assert(true, "cancellation aborts an in-flight request");
            }

            node.Effort = "default";
            using (var server = new LocalServer(delegate(Request req) { return Reply(200, "{\"model\":\"some-other-model\",\"choices\":[{\"message\":{\"content\":\"wrong\"}}]}"); }))
            {
                conn.BaseUrl = server.BaseUrl;
                var ex = await Throws<InvalidOperationException>(delegate { return ApiServices.ExecuteAsync(conn, node, "x", CancellationToken.None); });
                Assert(ex.Message.Contains("different model"), "mismatched returned model is rejected");
            }
            using (var server = new LocalServer(delegate(Request req) { return Reply(200, "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}"); }))
            {
                conn.BaseUrl = server.BaseUrl;
                var ex = await Throws<InvalidOperationException>(delegate { return ApiServices.ExecuteAsync(conn, node, "x", CancellationToken.None); });
                Assert(ex.Message.Contains("incomplete"), "length-limited completion is not returned as complete");
            }

            var originalUrl = conn.BaseUrl;
            conn.BaseUrl = "http://example.com/v1";
            ThrowsSync<ArgumentException>(delegate { ApiServices.ValidateConnection(conn); }, "remote HTTP endpoint is rejected");
            conn.BaseUrl = "https://user:pass@example.com/v1";
            ThrowsSync<ArgumentException>(delegate { ApiServices.ValidateConnection(conn); }, "URL userinfo is rejected");
            conn.BaseUrl = "https://example.com/v1?secret=x";
            ThrowsSync<ArgumentException>(delegate { ApiServices.ValidateConnection(conn); }, "query strings are rejected");
            conn.BaseUrl = originalUrl;

            var protectedValue = ApiServices.ProtectKey("local-test-secret");
            Assert(protectedValue != "local-test-secret" && protectedValue.Length > 20, "key protection does not persist plaintext");
            conn.KeyEnvironmentVariable = "";
            conn.ProtectedKey = protectedValue;
            using (var server = new LocalServer(delegate(Request req)
            {
                Assert(req.Authorization == "Bearer local-test-secret", "protected key is decrypted only for request authorization");
                return Reply(200, "{\"choices\":[{\"message\":{\"content\":\"safe\"}}]}");
            }))
            {
                conn.BaseUrl = server.BaseUrl;
                Assert(await ApiServices.ExecuteAsync(conn, node, "x", CancellationToken.None) == "safe", "encrypted key authorizes local request");
            }
            conn.ProtectedKey = "";
            conn.KeyEnvironmentVariable = "LAICA_SERVICE_TEST_MISSING_KEY_9B73D6";
            var missingKey = await Throws<InvalidOperationException>(delegate { return ApiServices.LoadModelsAsync(conn, CancellationToken.None); });
            Assert(missingKey.Message.Contains(conn.KeyEnvironmentVariable), "missing configured environment key fails clearly");
            conn.KeyEnvironmentVariable = "";
            conn.ProtectedKey = "";
            return assertions;
        }

        static void Assert(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Service test failed: " + description);
            assertions++;
        }
        static async Task<T> Throws<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (T ex) { assertions++; return ex; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }
        static void ThrowsSync<T>(Action action, string description) where T : Exception
        {
            try { action(); }
            catch (T) { Assert(true, description); return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + description);
        }
        static ReplyData Reply(int status, string body) { return new ReplyData(status, body, ""); }

        sealed class Request
        {
            public string Method, Path, Body, Authorization;
        }
        sealed class ReplyData
        {
            public int Status;
            public string Body, Extra;
            public ReplyData(int status, string body, string extra) { Status = status; Body = body; Extra = extra; }
        }
        sealed class LocalServer : IDisposable
        {
            readonly TcpListener listener;
            readonly CancellationTokenSource stop = new CancellationTokenSource();
            readonly Task worker;
            public string BaseUrl { get; private set; }
            public LocalServer(Func<Request, ReplyData> handler) : this(req => Task.FromResult(handler(req))) { }
            public LocalServer(Func<Request, Task<ReplyData>> handler)
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
                worker = Task.Run(async delegate
                {
                    while (!stop.IsCancellationRequested)
                    {
                        TcpClient client = null;
                        try
                        {
                            client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                            using (client)
                            using (var stream = client.GetStream())
                            {
                                var req = await ReadRequest(stream).ConfigureAwait(false);
                                var reply = await handler(req).ConfigureAwait(false);
                                var bytes = Encoding.UTF8.GetBytes(reply.Body ?? "");
                                var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + reply.Status + " Test\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n" + reply.Extra + "\r\n");
                                await stream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
                                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                            }
                        }
                        catch { if (client != null) client.Close(); if (!stop.IsCancellationRequested) { } }
                    }
                });
            }
            async Task<Request> ReadRequest(Stream stream)
            {
                var headerBytes = new List<byte>();
                var one = new byte[1];
                while (headerBytes.Count < 32768)
                {
                    int n = await stream.ReadAsync(one, 0, 1).ConfigureAwait(false);
                    if (n == 0) throw new IOException("client closed");
                    headerBytes.Add(one[0]);
                    int count = headerBytes.Count;
                    if (count >= 4 && headerBytes[count - 4] == 13 && headerBytes[count - 3] == 10 && headerBytes[count - 2] == 13 && headerBytes[count - 1] == 10) break;
                }
                var headers = Encoding.ASCII.GetString(headerBytes.ToArray());
                var first = headers.Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
                int length = 0;
                foreach (var line in headers.Split(new[] { "\r\n" }, StringSplitOptions.None)) if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) Int32.TryParse(line.Substring(15).Trim(), out length);
                var body = new byte[length];
                int offset = 0;
                while (offset < length) { int n = await stream.ReadAsync(body, offset, length - offset).ConfigureAwait(false); if (n == 0) throw new IOException("short request body"); offset += n; }
                string authorization = null;
                foreach (var line in headers.Split(new[] { "\r\n" }, StringSplitOptions.None)) if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) authorization = line.Substring(14).Trim();
                return new Request { Method = first[0], Path = first[1], Body = Encoding.UTF8.GetString(body), Authorization = authorization };
            }
            public void Dispose()
            {
                stop.Cancel();
                listener.Stop();
                try { worker.Wait(1000); } catch { }
                stop.Dispose();
            }
        }
    }
}
