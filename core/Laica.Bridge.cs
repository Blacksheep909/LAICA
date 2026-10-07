using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Laica
{
    // A small, read-only MCP stdio bridge. It never starts a model or modifies Codex settings.
    public static class BridgeHost
    {
        const int MaxLine = 1024 * 1024;
        const int MaxOutput = 128 * 1024;
        static readonly string[] Versions = { "2024-11-05", "2025-03-26", "2025-06-18" };
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = MaxLine, RecursionLimit = 40 };
        static string appDir;

        public static void Main(string[] args)
        {
            try
            {
                appDir = AppDomain.CurrentDomain.BaseDirectory;
                Console.InputEncoding = new UTF8Encoding(false, true);
                Console.OutputEncoding = new UTF8Encoding(false);
                string line; bool oversized;
                while ((line = ReadBoundedLine(out oversized)) != null)
                {
                    if (oversized) { WriteError(null, -32700, "Message exceeds the 1 MB limit."); continue; }
                    Dictionary<string, object> request;
                    try { request = Json.Deserialize<Dictionary<string, object>>(line); }
                    catch { WriteError(null, -32700, "Parse error."); continue; }
                    if (request == null) { WriteError(null, -32600, "Invalid request."); continue; }
                    object id; bool hasId = request.TryGetValue("id", out id);
                    object methodValue; string method = request.TryGetValue("method", out methodValue) ? methodValue as string : null;
                    if (String.IsNullOrEmpty(method)) { if (hasId) WriteError(id, -32600, "Invalid request."); continue; }
                    if (!hasId) continue; // Notifications have no response.
                    try { Handle(id, method, request.ContainsKey("params") ? request["params"] as Dictionary<string, object> : null); }
                    catch (BridgeException ex) { WriteError(id, ex.Code, ex.Message); }
                    catch (Exception) { WriteError(id, -32603, "Bridge operation failed."); }
                }
            }
            catch (Exception) { try { Console.Error.WriteLine("LAICA bridge stopped after an input/output error."); } catch { } }
        }

        static string ReadBoundedLine(out bool oversized)
        {
            oversized=false; var line=new StringBuilder(); int value;
            while((value=Console.In.Read())!=-1)
            {
                if(value=='\n') break;
                if(line.Length<MaxLine) line.Append((char)value); else oversized=true;
            }
            if(value==-1 && line.Length==0 && !oversized) return null;
            if(line.Length>0 && line[line.Length-1]=='\r')line.Length--;
            return line.ToString();
        }

        static void Handle(object id, string method, Dictionary<string, object> p)
        {
            if (method == "ping") { WriteResult(id, new Dictionary<string, object>()); return; }
            if (method == "initialize")
            {
                string requested = GetString(p, "protocolVersion");
                string version = Versions.Contains(requested) ? requested : Versions[Versions.Length - 1];
                WriteResult(id, new Dictionary<string, object> {
                    { "protocolVersion", version },
                    { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object>() } } },
                    { "serverInfo", new Dictionary<string, object> { { "name", "LAICA" }, { "version", "0.5" } } },
                    { "instructions", NativeInstructions }
                }); return;
            }
            if (method == "tools/list") { WriteResult(id, new Dictionary<string, object> { { "tools", ToolDefinitions() } }); return; }
            if (method == "tools/call") { CallTool(id, p); return; }
            throw new BridgeException(-32601, "Method not found.");
        }

        const string NativeInstructions = "LAICA selects the team. Codex is the task UI; the current supervisor owns consequential reasoning, planning, and final review. The selected team's root model and effort must match the active Codex chat. If they differ, ask the user to choose the matching model and effort in Codex; never start a duplicate supervisor or change global Codex configuration. Use the user task entered in Codex. Check each selected worker model and effort against actual native tool availability and report unavailable models. Delegate each worker its exact selected model, effort, role, and bounded job. Use a fixed named subagent role only when it matches the selected model and effort (for example gpt-6-luna/medium); otherwise use a general worker in a fresh or narrow context and specify the selected model and effort explicitly so a fixed role setting cannot override them. In effective multi mode, schedule dependencies and include parent results in dependent worker jobs; use at most three concurrent workers. Workers return results, evidence, uncertainty, and consequential decisions to the supervisor for review, without private chain-of-thought; do not retry automatically at high or allow worker nesting. API-service nodes are unsupported by this native adapter and require an adapter; never substitute a Codex worker. Effective solo mode suppresses all service workers; native Codex solo mode disables native delegation. If either mode is unknown, do not spawn workers until both modes are known. Codex settings are read-only.";

        static object[] ToolDefinitions()
        {
            return new object[] {
                Tool("get_team", "When the user requests the selected team, read and validate its bounded jobs for execution by the current native Codex supervisor.", new Dictionary<string, object>{{"type","object"},{"properties",new Dictionary<string,object>()},{"required",new string[0]},{"additionalProperties",false}}),
                Tool("get_activity", "Read a public-only activity snapshot for a Codex session.", new Dictionary<string, object>{{"type","object"},{"properties",new Dictionary<string,object>{{"session_id",new Dictionary<string,object>{{"type","string"},{"description","Codex session UUID."}}}}},{"required",new string[]{"session_id"}},{"additionalProperties",false}})
            };
        }
        static object Tool(string name, string description, object schema)
        {
            return new Dictionary<string, object> {
                {"name",name},{"title",name=="get_team"?"Get selected team":"Get session activity"},{"description",description},{"inputSchema",schema},
                {"annotations",new Dictionary<string,object>{{"readOnlyHint",true},{"destructiveHint",false},{"openWorldHint",false}}}
            };
        }
        static void CallTool(object id, Dictionary<string, object> p)
        {
            string name = GetString(p, "name");
            Dictionary<string, object> args = p != null && p.ContainsKey("arguments") ? p["arguments"] as Dictionary<string, object> : null;
            object data;
            if (name == "get_team") data = ReadTeam();
            else if (name == "get_activity") data = ReadActivity(args);
            else throw new BridgeException(-32602, "Unknown tool.");
            string json = Json.Serialize(data);
            if (Encoding.UTF8.GetByteCount(json) > MaxOutput) throw new BridgeException(-32000, "Tool result exceeds the 128 KB limit.");
            WriteResult(id, new Dictionary<string, object> { { "content", new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", json } } } }, { "isError", false } });
        }

        static object ReadTeam()
        {
            string path = Path.Combine(appDir, "active-team.json");
            if (!File.Exists(path)) throw new BridgeException(-32001, "No selected team is available (active-team.json is missing).");
            Dictionary<string, object> data;
            try
            {
                var info = new FileInfo(path); if (info.Length > MaxLine) throw new Exception();
                data = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch { throw new BridgeException(-32001, "Selected team is unreadable or exceeds 1 MB."); }
            if (data == null) throw new BridgeException(-32001, "Selected team is invalid.");
            object version; object nodesValue;
            if (!data.TryGetValue("Version", out version) || !data.TryGetValue("Nodes", out nodesValue)) throw new BridgeException(-32001, "Selected team must be a version 1 AgentPlan.");
            try { if (Convert.ToInt32(version) != 1) throw new BridgeException(-32001, "Selected team must be a version 1 AgentPlan."); }
            catch (BridgeException) { throw; } catch { throw new BridgeException(-32001, "Selected team must be a version 1 AgentPlan."); }
            string goal=GetString(data,"Goal")??""; if(goal.Length>16000)throw new BridgeException(-32001,"Selected team goal is too long.");
            IList rawNodes = nodesValue as IList;
            if (rawNodes == null || rawNodes.Count < 1 || rawNodes.Count > 100) throw new BridgeException(-32001, "Selected team must contain 1 to 100 agents.");
            var nodes = new List<Dictionary<string, object>>(); var byId = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal); var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (object raw in rawNodes)
            {
                var n = raw as Dictionary<string, object>; if (n == null) throw new BridgeException(-32001, "Selected team contains an invalid agent.");
                object rawConnection; if(n.TryGetValue("ConnectionId",out rawConnection) && rawConnection!=null && !(rawConnection is string))throw new BridgeException(-32001,"Selected team contains an invalid connection id.");
                string agentId = GetString(n,"Id"), agentName=GetString(n,"Name"), role=GetString(n,"Role"), model=GetString(n,"Model"), effort=GetString(n,"Effort"), connection=GetString(n,"ConnectionId") ?? "codex", parent=GetString(n,"ParentId"), job=GetString(n,"Job") ?? "";
                if (!SafeId(agentId) || !names.Add(agentId) || String.IsNullOrWhiteSpace(agentName) || agentName.Length>80 || String.IsNullOrWhiteSpace(role) || role.Length>80 || String.IsNullOrWhiteSpace(model) || model.Length>256 || String.IsNullOrWhiteSpace(effort) || effort.Length>32 || connection.Length>128 || job.Length>12000) throw new BridgeException(-32001, "Selected team contains an invalid agent or unsafe agent id.");
                var item = new Dictionary<string, object>{{"id",agentId},{"parent_id",parent},{"name",agentName},{"role",role},{"model",model},{"effort",effort},{"connection_id",connection},{"job",job},{"execution",IsCodex(connection)?"native_codex":"unsupported_service"}};
                nodes.Add(item); byId.Add(agentId,item);
            }
            Dictionary<string, object> root;
            if (!byId.TryGetValue("root",out root) || root["parent_id"] != null && Convert.ToString(root["parent_id"])!="") throw new BridgeException(-32001, "Selected team must have one root with no parent.");
            if (!IsCodex(Convert.ToString(root["connection_id"]))) throw new BridgeException(-32001, "The selected root uses an API service. A service adapter is required; the native Codex bridge cannot run it.");
            foreach(var node in nodes)
            {
                string current=Convert.ToString(node["id"]); var visited=new HashSet<string>(StringComparer.Ordinal);
                while(current!="root") { if(!visited.Add(current))throw new BridgeException(-32001,"Selected team hierarchy contains a cycle."); var item=byId[current]; string par=Convert.ToString(item["parent_id"]); if(String.IsNullOrEmpty(par)||!byId.ContainsKey(par))throw new BridgeException(-32001,"Selected team hierarchy is detached or has a missing parent."); current=par; }
            }
            string nativeMode=ReadNativeMode(), harnessMode=ReadHarnessMode();
            string mode=EffectiveMode(nativeMode,harnessMode);
            return new Dictionary<string, object>{{"goal",goal},{"mode",mode},{"native_mode",nativeMode},{"harness_mode",harnessMode},{"supervisor_model",root["model"]},{"supervisor_effort",root["effort"]},{"requires_matching_codex_chat",true},{"nodes",nodes},{"native_instructions",NativeInstructions}};
        }
        static bool IsCodex(string c) { return String.IsNullOrWhiteSpace(c) || String.Equals(c.Trim(),"codex",StringComparison.OrdinalIgnoreCase); }
        static bool SafeId(string id) { return !String.IsNullOrEmpty(id) && id.Length<=128 && Regex.IsMatch(id,"^[A-Za-z0-9][A-Za-z0-9_-]*$") && !id.StartsWith("$",StringComparison.Ordinal); }
        static string ReadNativeMode()
        {
            string config=Path.Combine(ResolveHome(),"config.toml");
            try
            {
                if(!File.Exists(config)) return "unknown";
                string text=File.ReadAllText(config,Encoding.UTF8);
                Match section=Regex.Match(text,@"(?ms)^\[agents\][ \t]*\r?\n(?<body>.*?)(?=^\[|\z)");
                Match enabled=Regex.Match(section.Groups["body"].Value,@"(?m)^enabled\s*=\s*(true|false)[ \t\r]*(?:#.*)?$");
                return enabled.Success ? (enabled.Groups[1].Value=="true"?"multi":"solo") : "unknown";
            }
            catch { return "unknown"; }
        }
        static string ReadHarnessMode()
        {
            string path=Path.Combine(appDir,"harness-mode.json");
            try
            {
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                {
                    if(stream.Length>4096)return "unknown";
                    using(var reader=new StreamReader(stream,Encoding.UTF8,true))
                    {
                        Dictionary<string,object> data=Json.Deserialize<Dictionary<string,object>>(reader.ReadToEnd());
                        string mode=GetString(data,"Mode");
                        return mode=="multi"||mode=="solo"?mode:"unknown";
                    }
                }
            }
            catch(FileNotFoundException) { return ReadNativeMode(); }
            catch(DirectoryNotFoundException) { return ReadNativeMode(); }
            catch { return "unknown"; }
        }
        static string EffectiveMode(string nativeMode,string harnessMode)
        {
            if(nativeMode=="solo"||harnessMode=="solo")return "solo";
            if(nativeMode=="multi"&&harnessMode=="multi")return "multi";
            return "unknown";
        }
        static object ReadActivity(Dictionary<string, object> args)
        {
            string session=GetString(args,"session_id");
            if(String.IsNullOrEmpty(session) || session.Length>64 || !Regex.IsMatch(session,"^[A-Fa-f0-9-]{16,64}$")) throw new BridgeException(-32602,"session_id must be a Codex session UUID.");
            string home=ResolveHome();
            try { using(var observer=new CodexActivityObserver(home)) return observer.Read(session); }
            catch { throw new BridgeException(-32002,"Activity snapshot is unavailable."); }
        }
        static string GetString(Dictionary<string, object> d,string key) { object v; return d!=null&&d.TryGetValue(key,out v)?v as string:null; }
        static string ResolveHome() { string configured=Environment.GetEnvironmentVariable("CODEX_HOME"); return Path.GetFullPath(String.IsNullOrWhiteSpace(configured)?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex"):configured); }
        static void WriteResult(object id,object result) { Write(new Dictionary<string,object>{{"jsonrpc","2.0"},{"id",id},{"result",result}}); }
        static void WriteError(object id,int code,string message) { Write(new Dictionary<string,object>{{"jsonrpc","2.0"},{"id",id},{"error",new Dictionary<string,object>{{"code",code},{"message",message}}}}); }
        static void Write(object value) { try { string line=Json.Serialize(value); if(Encoding.UTF8.GetByteCount(line)<=MaxLine) Console.Out.WriteLine(line); else Console.Out.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32000,\"message\":\"Response exceeds limit.\"}}"); } catch { } }
        sealed class BridgeException : Exception { public readonly int Code; public BridgeException(int code,string message):base(message){Code=code;} }
    }
}
