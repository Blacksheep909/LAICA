using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Laica
{
    public sealed class ServiceConnection
    {
        public string Id;
        public string Name;
        public string BaseUrl;
        public string Provider = ServicePresets.Compatible;
        public string KeyEnvironmentVariable;
        public string ProtectedKey;
        public override string ToString() { return (Name ?? String.Empty) + "  ·  " + ServicePresets.DisplayName(Provider); }
    }

    public static class ServicePresets
    {
        public const string Compatible = "compatible";
        public const string Ollama = "ollama";
        public const string LMStudio = "lmstudio";

        public static ServiceConnection Create(string provider)
        {
            provider = Normalize(provider);
            string name, url;
            switch (provider)
            {
                case Ollama: name = "Ollama local"; url = "http://localhost:11434/v1"; break;
                case LMStudio: name = "LM Studio local"; url = "http://localhost:1234/v1"; break;
                default: name = "Compatible API"; url = "https://api.openai.com/v1"; break;
            }
            return new ServiceConnection { Id = Guid.NewGuid().ToString("N"), Name = name, BaseUrl = url, Provider = provider, KeyEnvironmentVariable = String.Empty, ProtectedKey = String.Empty };
        }

        public static string Normalize(string provider)
        {
            if (String.IsNullOrWhiteSpace(provider)) return Compatible;
            if (String.Equals(provider, Compatible, StringComparison.OrdinalIgnoreCase)) return Compatible;
            if (String.Equals(provider, Ollama, StringComparison.OrdinalIgnoreCase)) return Ollama;
            if (String.Equals(provider, LMStudio, StringComparison.OrdinalIgnoreCase)) return LMStudio;
            throw new ArgumentException("Provider must be Compatible API, Ollama, or LM Studio.", "provider");
        }

        public static string DisplayName(string provider)
        {
            switch (Normalize(provider))
            {
                case Ollama: return "Ollama";
                case LMStudio: return "LM Studio";
                default: return "Compatible API";
            }
        }
    }

    public static class ApiServices
    {
        const int MaxResponseBytes = 4 * 1024 * 1024;
        const int TimeoutMilliseconds = 15 * 60 * 1000;
        const int MaxBaseUrlLength = 2048;
        const int MaxNameLength = 120;
        const int MaxEnvNameLength = 100;
        const int MaxModelLength = 256;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static void ValidateConnection(ServiceConnection connection)
        {
            if (connection == null) throw new ArgumentNullException("connection");
            if (String.IsNullOrWhiteSpace(connection.Id) || connection.Id.Length > 128) throw new ArgumentException("Connection ID is required and must be at most 128 characters.");
            if (String.IsNullOrWhiteSpace(connection.Name) || connection.Name.Length > MaxNameLength) throw new ArgumentException("Connection name is required and must be at most 120 characters.");
            if (String.IsNullOrWhiteSpace(connection.BaseUrl) || connection.BaseUrl.Length > MaxBaseUrlLength) throw new ArgumentException("Base URL is required and must be at most 2048 characters.");
            ServicePresets.Normalize(connection.Provider); // Missing fields from older service files default to the compatible API.
            Uri uri;
            if (!Uri.TryCreate(connection.BaseUrl.Trim(), UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) throw new ArgumentException("Base URL must be an absolute HTTPS URL (HTTP is allowed only for loopback testing/local services).");
            if (!String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment)) throw new ArgumentException("Base URL cannot contain credentials, a query, or a fragment.");
            if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri.Host)) throw new ArgumentException("HTTP is allowed only for loopback hosts; use HTTPS for remote services.");
            if (!String.IsNullOrEmpty(connection.KeyEnvironmentVariable) && (connection.KeyEnvironmentVariable.Length > MaxEnvNameLength || connection.KeyEnvironmentVariable.Any(Char.IsControl))) throw new ArgumentException("Key environment variable name is invalid or too long.");
        }

        public static string ProtectKey(string key)
        {
            if (key == null) throw new ArgumentNullException("key");
            var plain = Encoding.UTF8.GetBytes(key);
            try { return Convert.ToBase64String(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        public static async Task<List<ModelInfo>> LoadModelsAsync(ServiceConnection connection, CancellationToken token)
        {
            ValidateConnection(connection);
            var result = await Send(connection, "GET", Endpoint(connection, "models"), null, token).ConfigureAwait(false);
            var root = ParseObject(result, "Model catalog response");
            object raw;
            if (!root.TryGetValue("data", out raw) || !(raw is object[])) throw new InvalidOperationException("Model catalog response is invalid: expected a data array.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var models = new List<ModelInfo>();
            foreach (var item in (object[])raw)
            {
                var row = item as Dictionary<string, object>;
                var id = row == null ? null : GetString(row, "id");
                if (String.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Model catalog response contains a model with no ID.");
                if (id.Length > MaxModelLength) throw new InvalidOperationException("Model catalog response contains an ID that is too long.");
                if (seen.Add(id)) models.Add(new ModelInfo { Id = id, Name = id, ConnectionId = connection.Id, Efforts = new List<string> { "default" } });
            }
            return models;
        }

        public static async Task<string> ExecuteAsync(ServiceConnection connection, AgentNode node, string prompt, CancellationToken token)
        {
            ValidateConnection(connection);
            if (node == null) throw new ArgumentNullException("node");
            if (String.IsNullOrWhiteSpace(node.Model) || node.Model.Length > MaxModelLength) throw new ArgumentException("A model ID of at most 256 characters is required.", "node");
            if (prompt == null) throw new ArgumentNullException("prompt");
            if (prompt.Length > 1024 * 1024) throw new ArgumentException("Prompt exceeds the 1 MB limit.", "prompt");
            var effort = String.IsNullOrWhiteSpace(node.Effort) ? "default" : node.Effort;
            var messages = new object[] {
                new Dictionary<string, object> { { "role", "system" }, { "content", BuildSystemMessage(node) } },
                new Dictionary<string, object> { { "role", "user" }, { "content", prompt } }
            };
            var request = new Dictionary<string, object> { { "model", node.Model }, { "messages", messages }, { "stream", false } };
            if (effort != "default") request["reasoning_effort"] = effort;
            var body = Json.Serialize(request);
            var text = await Send(connection, "POST", Endpoint(connection, "chat/completions"), body, token).ConfigureAwait(false);
            var root = ParseObject(text, "Chat completion response");
            var returnedModel = GetString(root, "model");
            if (!String.IsNullOrEmpty(returnedModel) && !String.Equals(returnedModel, node.Model, StringComparison.Ordinal)) throw new InvalidOperationException("Provider returned a different model than requested; refusing to report the result as the requested model.");
            object choicesRaw;
            if (!root.TryGetValue("choices", out choicesRaw) || !(choicesRaw is object[]) || ((object[])choicesRaw).Length == 0) throw new InvalidOperationException("Chat completion response has no choices.");
            var choice = ((object[])choicesRaw)[0] as Dictionary<string, object>;
            if (choice == null) throw new InvalidOperationException("Chat completion response has an invalid choice.");
            if (String.Equals(GetString(choice, "finish_reason"), "length", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Provider stopped because the response reached its token limit; result is incomplete.");
            var finish=GetString(choice,"finish_reason");if(!String.IsNullOrEmpty(finish)&&finish!="stop")throw new InvalidOperationException("Provider did not return a complete text answer.");
            object messageRaw;
            if (!choice.TryGetValue("message", out messageRaw) || !(messageRaw is Dictionary<string, object>)) throw new InvalidOperationException("Chat completion response has no message.");
            var content = GetString((Dictionary<string, object>)messageRaw, "content");
            if (String.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("Chat completion response contains no text content.");
            return content;
        }

        static string BuildSystemMessage(AgentNode node)
        {
            return "You are an AI agent operating within a bounded LAICA task. Follow the assigned role and scope. Do not claim to be a Codex app-server thread or to have performed actions that were not provided in the prompt.\nRole: " + Safe(node.Role) + "\nName: " + Safe(node.Name) + "\nAssigned work: " + Safe(node.Job);
        }
        static string Safe(string s) { return s ?? String.Empty; }
        static string Endpoint(ServiceConnection c, string suffix)
        {
            var b = c.BaseUrl.Trim().TrimEnd('/');
            return b + "/" + suffix;
        }
        static bool IsLoopback(string host)
        {
            if (String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            IPAddress ip;
            return IPAddress.TryParse(host, out ip) && IPAddress.IsLoopback(ip);
        }
        static string GetKey(ServiceConnection c)
        {
            if (!String.IsNullOrEmpty(c.KeyEnvironmentVariable))
            {
                string configured = Environment.GetEnvironmentVariable(c.KeyEnvironmentVariable, EnvironmentVariableTarget.Process);
                if (String.IsNullOrEmpty(configured))
                {
                    try { configured = Environment.GetEnvironmentVariable(c.KeyEnvironmentVariable, EnvironmentVariableTarget.User); }
                    catch (PlatformNotSupportedException) { }
                    catch (SecurityException) { }
                }
                if (String.IsNullOrEmpty(configured)) throw new InvalidOperationException("API key environment variable '" + c.KeyEnvironmentVariable + "' is not set in process or user scope.");
                return configured;
            }
            if (String.IsNullOrEmpty(c.ProtectedKey)) return null;
            byte[] cipher = null;
            byte[] plain = null;
            try
            {
                cipher = Convert.FromBase64String(c.ProtectedKey);
                plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch { throw new InvalidOperationException("Saved API key cannot be decrypted for this Windows user."); }
            finally
            {
                if (cipher != null) Array.Clear(cipher, 0, cipher.Length);
                if (plain != null) Array.Clear(plain, 0, plain.Length);
            }
        }

        static async Task<string> Send(ServiceConnection c, string method, string url, string body, CancellationToken token)
        {
            using(var limit=CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limit.CancelAfter(TimeoutMilliseconds);
                try{return await SendCore(c,method,url,body,limit.Token).ConfigureAwait(false);}
                catch(OperationCanceledException){if(!token.IsCancellationRequested)throw new TimeoutException("Provider request exceeded 15 minutes.");throw;}
            }
        }
        static async Task<string> SendCore(ServiceConnection c, string method, string url, string body, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.AllowAutoRedirect = false;
            request.Timeout = TimeoutMilliseconds;
            request.ReadWriteTimeout = TimeoutMilliseconds;
            request.KeepAlive = false;
            request.Accept = "application/json";
            request.UserAgent = "LAICA/1.0";
            var key = GetKey(c);
            if (!String.IsNullOrEmpty(key)) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
            if (body != null)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                request.ContentType = "application/json; charset=utf-8";
                request.ContentLength = bytes.Length;
                using (var registration = token.Register(delegate { try { request.Abort(); } catch { } }))
                {
                    try
                    {
                        using (var stream = await request.GetRequestStreamAsync().ConfigureAwait(false)) await stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
                    }
                    catch (WebException) { if (token.IsCancellationRequested) throw new OperationCanceledException(token); throw; }
                }
            }
            WebResponse response = null;
            using (var registration = token.Register(delegate { try { request.Abort(); } catch { } }))
            {
                try { response = await request.GetResponseAsync().ConfigureAwait(false); }
                catch (WebException ex)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    var error = ex.Response as HttpWebResponse;
                    if (error != null)
                    {
                        int code = (int)error.StatusCode;
                        error.Close();
                        if (code >= 300 && code < 400) throw new InvalidOperationException("Provider redirected the request; redirects are disabled to protect credentials.");
                        throw new InvalidOperationException("Provider request failed with HTTP " + code + " (" + error.StatusCode + ").");
                    }
                    throw new InvalidOperationException("Provider request failed: " + SafeNetworkMessage(ex.Status.ToString()));
                }
                using (response)
                {
                    var http = response as HttpWebResponse;
                    if (http != null && (int)http.StatusCode >= 300 && (int)http.StatusCode < 400) throw new InvalidOperationException("Provider redirected the request; redirects are disabled to protect credentials.");
                    using (var stream = response.GetResponseStream()) return await ReadBounded(stream, token).ConfigureAwait(false);
                }
            }
        }
        static string SafeNetworkMessage(string status)
        {
            if (String.IsNullOrEmpty(status)) return "network error";
            return status.Length > 120 ? status.Substring(0, 120) : status;
        }
        static async Task<string> ReadBounded(Stream stream, CancellationToken token)
        {
            var buffer = new byte[8192];
            using (var output = new MemoryStream())
            {
                while (true)
                {
                    int n;
                    try { n = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false); }
                    catch (WebException) { if (token.IsCancellationRequested) throw new OperationCanceledException(token); throw; }
                    if (n == 0) break;
                    if (output.Length + n > MaxResponseBytes) throw new InvalidOperationException("Provider response exceeds the 4 MB limit.");
                    output.Write(buffer, 0, n);
                }
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }
        static Dictionary<string, object> ParseObject(string value, string label)
        {
            try
            {
                var d = Json.DeserializeObject(value) as Dictionary<string, object>;
                if (d == null) throw new InvalidOperationException();
                return d;
            }
            catch { throw new InvalidOperationException(label + " is not valid JSON."); }
        }
        static string GetString(Dictionary<string, object> d, string key)
        {
            object value;
            return d != null && d.TryGetValue(key, out value) ? value as string : null;
        }
    }
}
