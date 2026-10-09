using System;
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Laica
{
    public class AgentNode
    {
        public string Id, ParentId, Name, Role, Model, Effort, Job;
        public string ConnectionId = "codex";
        public float X, Y;
        /// <summary>CLI agent nodes only: false (the default) keeps the node read-only; true lets it edit files in the working folder with the agent's normal permission mode.</summary>
        public bool CanEdit;
    }
    public class AgentPlan
    {
        public int Version = 1;
        public string Goal;
        public List<AgentNode> Nodes = new List<AgentNode>();
        public List<StagePosition> Stages = new List<StagePosition>();
        public List<StageLink> HiddenStageLinks = new List<StageLink>();
    }
    public class StagePosition { public string Id; public float X,Y; }
    public class StageLink { public string FromId, ToId; }
    public class ModelInfo
    {
        public string Id, Name;
        public string ConnectionId = "codex";
        public List<string> Efforts = new List<string>();
        public override string ToString() { return Name; }
    }

    internal sealed class RpcClient : IDisposable
    {
        readonly Process process;
        readonly StreamWriter input;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly Dictionary<string, TaskCompletionSource<Dictionary<string, object>>> pending = new Dictionary<string, TaskCompletionSource<Dictionary<string, object>>>();
        readonly object pendingLock = new object();
        readonly object writeLock = new object();
        readonly Action<string, Dictionary<string, object>> notification;
        readonly StringBuilder stderr = new StringBuilder();
        int serial;
        const int MaxLine = 4 * 1024 * 1024;

        public RpcClient(string cli, string cwd, Action<string, Dictionary<string, object>> onNotification)
        {
            notification = onNotification;
            process = new Process();
            process.StartInfo = new ProcessStartInfo(cli, "app-server --strict-config --stdio") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true };
            process.Start();
            input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false));
            Task.Factory.StartNew(ReadLoop, TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(DrainError, TaskCreationOptions.LongRunning);
        }
        void DrainError() { try { string s; while ((s = process.StandardError.ReadLine()) != null) lock(stderr) { if(stderr.Length<8192) stderr.AppendLine(s.Length>1024?s.Substring(0,1024):s); } } catch { } }
        static string Str(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : null; }
        void ReadLoop()
        {
            try
            {
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    if (line.Length > MaxLine) throw new IOException("Codex protocol message exceeded the supported size.");
                    Dictionary<string, object> d;
                    try { d = json.Deserialize<Dictionary<string, object>>(line); } catch (Exception ex) { throw new IOException("Malformed Codex protocol output.",ex); }
                    if (d == null) continue;
                    string id = Str(d, "id");
                    if (id != null)
                    {
                        TaskCompletionSource<Dictionary<string, object>> t;
                        lock (pendingLock) { pending.TryGetValue(id, out t); if (t != null) pending.Remove(id); }
                        if (t != null) t.TrySetResult(d);
                        else if (d.ContainsKey("method")) RespondError(id, "Unsupported server request");
                    }
                    else if (notification != null && d.ContainsKey("method"))
                    {
                        try { notification(Str(d, "method"), d.ContainsKey("params") ? d["params"] as Dictionary<string, object> : null); } catch { }
                    }
                }
            }
            catch { }
            finally
            {
                if (notification != null) try { notification("laica/transportClosed", new Dictionary<string, object>()); } catch { }
                string message="Codex app-server closed its output."; lock(stderr) if(stderr.Length>0) message+=" stderr: "+stderr.ToString();
                lock (pendingLock) foreach (var p in pending) p.Value.TrySetException(new IOException(message));
            }
        }
        void Write(object value)
        {
            string line = json.Serialize(value);
            lock (writeLock) { input.WriteLine(line); input.Flush(); }
        }
        void RespondError(string id, string message) { try { Write(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "error", new Dictionary<string, object> { { "code", -32601 }, { "message", message } } } }); } catch { } }
        public void Notify(string method, object args) { Write(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "method", method }, { "params", args } }); }
        public async Task<Dictionary<string, object>> Request(string method, object args, CancellationToken token)
        {
            string id = Interlocked.Increment(ref serial).ToString();
            var t = new TaskCompletionSource<Dictionary<string, object>>(); lock(pendingLock) pending[id] = t;
            Write(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "method", method }, { "params", args } });
            Task delay = Task.Delay(TimeSpan.FromSeconds(30), token);
            Task completed = await Task.WhenAny(t.Task, delay).ConfigureAwait(false);
            if (completed != t.Task) { lock(pendingLock) pending.Remove(id); token.ThrowIfCancellationRequested(); throw new TimeoutException("Codex app-server request timed out: " + method); }
            var response = await t.Task.ConfigureAwait(false);
            if (response.ContainsKey("error")) throw new InvalidOperationException("Codex app-server " + method + " failed: " + json.Serialize(response["error"]));
            return response.ContainsKey("result") ? response["result"] as Dictionary<string, object> : new Dictionary<string, object>();
        }
        public void Dispose() { try { input.Close(); } catch { } try { if (!process.WaitForExit(2500)) process.Kill(); } catch { } process.Dispose(); }
    }

    public static class ModelCatalog
    {
        public static string FindCodex()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string root = Path.Combine(local, "OpenAI", "Codex", "bin");
            if (Directory.Exists(root))
            {
                string found = Directory.GetDirectories(root).SelectMany(d => Directory.GetFiles(d, "codex.exe")).OrderByDescending(p => Directory.GetLastWriteTimeUtc(p)).FirstOrDefault();
                if (found != null && File.Exists(found)) return found;
            }
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator)) { try { string p = Path.Combine(dir.Trim(), "codex.exe"); if (File.Exists(p)) return p; } catch { } }
            return null;
        }
        public static async Task<List<ModelInfo>> LoadAsync(string cli, string workingDirectory, CancellationToken token)
        {
            var models = new List<ModelInfo>();
            using (var rpc = new RpcClient(cli, workingDirectory, null))
            {
                await rpc.Request("initialize", new { clientInfo = new { name = "laica", version = "0.1" }, capabilities = new { experimentalApi = true } }, token).ConfigureAwait(false);
                rpc.Notify("initialized", new { });
                string cursor = null;
                do
                {
                    var a = new Dictionary<string, object> { { "includeHidden", false } }; if (cursor != null) a["cursor"] = cursor;
                    var r = await rpc.Request("model/list", a, token).ConfigureAwait(false);
                    object raw; var data = r.TryGetValue("data", out raw) ? raw as IEnumerable : null;
                    if (data != null) foreach (var x in data)
                    {
                        var d = x as Dictionary<string, object>; if (d == null) continue;
                        var m = new ModelInfo { Id = Get(d, "id"), Name = Get(d, "displayName") ?? Get(d, "id") };
                        object opts; if (d.TryGetValue("supportedReasoningEfforts", out opts)) { var options = opts as IEnumerable; if(options!=null) foreach (var opt in options) { var o = opt as Dictionary<string, object>; if (o != null && Get(o, "reasoningEffort") != null) m.Efforts.Add(Get(o, "reasoningEffort")); } }
                        if (m.Id != null) models.Add(m);
                    }
                    cursor = Get(r, "nextCursor");
                } while (!String.IsNullOrEmpty(cursor));
            }
            return models;
        }
        static string Get(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : null; }
    }

    public static class GraphValidator
    {
        public static void Validate(AgentPlan plan, List<ModelInfo> catalog, bool solo)
        {
            if (catalog == null) throw new ArgumentException("Model catalog is required.");
            if (plan == null || plan.Nodes == null || plan.Nodes.Count == 0 || plan.Nodes.Count > 100) throw new ArgumentException("Plan must contain 1 to 100 nodes.");
            var ids = new HashSet<string>(StringComparer.Ordinal); foreach (var n in plan.Nodes) if (n == null || String.IsNullOrWhiteSpace(n.Id) || n.Id.StartsWith("$",StringComparison.Ordinal) || !ids.Add(n.Id)) throw new ArgumentException("Every node needs a unique id.");
            AgentNode root = plan.Nodes.SingleOrDefault(n => n.Id == "root");
            if (root == null || root.ParentId != null && root.ParentId != "") throw new ArgumentException("The graph must have one root node with no parent.");
            if (Connection(root.ConnectionId)=="codex" && (root.Model != "gpt-6.1-sol" || root.Effort != "high")) throw new ArgumentException("The Codex supervisor stays gpt-6.1-sol / high.");
            foreach (var n in plan.Nodes)
            {
                if (n.Id != "root" && (String.IsNullOrWhiteSpace(n.ParentId) || !ids.Contains(n.ParentId))) throw new ArgumentException("Each worker must reference an existing parent.");
                if (solo && n.Id != "root") continue;
                var m = catalog.FirstOrDefault(x => x.Id == n.Model && Connection(x.ConnectionId)==Connection(n.ConnectionId)); if (m == null) throw new ArgumentException("Model unavailable for " + n.Name + ": " + n.Model);
                if (m.Efforts == null || !m.Efforts.Contains(n.Effort)) throw new ArgumentException("Unsupported effort for " + n.Name + ": " + n.Effort);
            }
            var seen = new HashSet<string>(); Visit(root, plan.Nodes, seen); if (seen.Count != plan.Nodes.Count) throw new ArgumentException("Graph contains a cycle or disconnected node.");
        }
        static void Visit(AgentNode n, List<AgentNode> all, HashSet<string> seen) { if (!seen.Add(n.Id)) throw new ArgumentException("Graph contains a cycle."); foreach (var c in all.Where(x => x.ParentId == n.Id)) Visit(c, all, seen); }
        public static string Connection(string id) { return String.IsNullOrWhiteSpace(id)?"codex":id; }
    }

    public static class GraphPlanHelpers
    {
        public static AgentPlan CreateStarter() { return new AgentPlan { Goal = "", Nodes = new List<AgentNode> {
            new AgentNode { Id="root", Name="Supervisor", Role="supervisor", Model="gpt-6.1-sol", Effort="high", Job="Plan the work, coordinate workers, and integrate their results.", X=70,Y=200 },
            new AgentNode { Id="investigator", ParentId="root", Name="Investigator", Role="investigator", Model="gpt-6-luna", Effort="medium", Job="Investigate the problem and report evidence.", X=370,Y=65 },
            new AgentNode { Id="implementer", ParentId="root", Name="Implementer", Role="implementer", Model="gpt-6-luna", Effort="medium", Job="Implement the supervisor's specification in the assigned files, run the required checks, and report changes or ambiguity.", X=370,Y=215 },
            new AgentNode { Id="verifier", ParentId="root", Name="Verifier", Role="verifier", Model="gpt-6-luna", Effort="medium", Job="Independently inspect the task evidence and proposals, and report risks or gaps.", X=370,Y=365 }
        } }; }
        public static string ToPrompt(AgentPlan plan) { return String.Join(Environment.NewLine, plan.Nodes.Select(n => n.Id + " parent=" + (String.IsNullOrEmpty(n.ParentId) ? "none" : n.ParentId) + " (" + n.Role + ": " + n.Name + "): " + n.Model + "/" + n.Effort + " — " + n.Job)); }
    }

    public sealed class GraphRunner
    {
        public event Action<string, string, string> NodeStatus;
        public event Action<string> Message;
        const int MaxRootPlan = 12000, MaxParent = 12000, MaxWorkers = 100000;
        readonly Dictionary<string,string> states=new Dictionary<string,string>();
        string State(string id){lock(states){string state;return states.TryGetValue(id,out state)?state:null;}}
        void Status(string id,string state,string detail) {lock(states)states[id]=state; var e=NodeStatus; if(e!=null)e(id,state,detail); }
        void Say(string s) { var e=Message; if(e!=null)e(s); }
        public async Task<string> RunAsync(AgentPlan plan, List<ModelInfo> catalog, string cli, string workingDirectory, bool solo, CancellationToken token)
        { return await RunAsync(plan,catalog,cli,workingDirectory,solo,new List<ServiceConnection>(),token).ConfigureAwait(false); }
        public async Task<string> RunAsync(AgentPlan plan, List<ModelInfo> catalog, string cli, string workingDirectory, bool solo, List<ServiceConnection> connections, CancellationToken token)
        { services=connections??new List<ServiceConnection>(); GraphValidator.Validate(plan,catalog,solo);token.ThrowIfCancellationRequested();Status("$input","done","Goal received");Status("$review","queued","Waiting for supervisor and workers");Status("$output","queued","Waiting for reviewed answer"); try { var answer=await RunCoreAsync(plan,catalog,cli,workingDirectory,solo,token).ConfigureAwait(false); Status("$output","done","Final answer ready"); return answer; } catch(OperationCanceledException) {if(State("$review")=="queued")Status("$review","cancelled","Run stopped before review");Status("$output","cancelled","Run stopped"); throw; } catch {if(State("$review")=="queued")Status("$review","skipped","Supervisor planning failed");Status("$output","error","No complete final answer");throw; } }
        List<ServiceConnection> services=new List<ServiceConnection>();
        async Task<string> RunCoreAsync(AgentPlan plan, List<ModelInfo> catalog, string cli, string workingDirectory, bool solo, CancellationToken token)
        {
            GraphValidator.Validate(plan,catalog,solo); if(solo) {var soloRoot=plan.Nodes.Single(n=>n.Id=="root");Status("root","running","Sol only");try{var soloAnswer=await Execute(soloRoot,"SOLO mode. Complete this goal yourself using read-only tools if needed. Do not delegate or claim worker involvement. Goal: "+plan.Goal,cli,workingDirectory,token).ConfigureAwait(false);Status("root","done","Complete");Status("$review","done","Supervisor completed the solo answer");return soloAnswer;}catch(OperationCanceledException){Status("root","cancelled","Cancelled");throw;}catch(Exception ex){Status("root","error",ex.Message);throw;}} var output=new Dictionary<string,string>(); var failures=new Dictionary<string,string>();
            int writers=plan.Nodes.Count(n=>n.CanEdit&&(n.ConnectionId??"").StartsWith("cli:",StringComparison.Ordinal)); if(writers>1) Say("Warning: "+writers+" nodes can edit files in the same folder and no worktree separates them, so their changes can collide. Give each its own scope.");
            var root=plan.Nodes.Single(n=>n.Id=="root"); Status("root","running","Planning");
            string rootPrompt="Goal: "+plan.Goal+"\nPlan nodes and jobs:\n"+ToPromptLimited(plan,MaxRootPlan)+"\nYou are the root coordinator. Produce a concise execution plan. The harness schedules every worker. Do not delegate, spawn agents, or use native multi-agent features.";
            try { output["root"]=await Execute(root,rootPrompt,cli,workingDirectory,token).ConfigureAwait(false); }
            catch(OperationCanceledException) { Status("root","cancelled","Cancelled"); throw; }
            catch(Exception ex) {Status("root","error",ex.Message);throw;}
            Status("root","done","Plan complete");
            var remaining=solo ? new List<AgentNode>() : plan.Nodes.Where(n=>n.Id!="root").ToList(); int active=0;
            while(remaining.Count>0)
            {
                if(token.IsCancellationRequested) { foreach(var n in remaining) Status(n.Id,"cancelled","Cancelled before scheduling"); token.ThrowIfCancellationRequested(); }
                var ready=remaining.Where(n=>output.ContainsKey(n.ParentId)||failures.ContainsKey(n.ParentId)).ToList();
                int skipped=0; foreach(var n in ready.Where(n=>failures.ContainsKey(n.ParentId))) { skipped++; failures[n.Id]="Parent failed"; Status(n.Id,"error","Parent failed; skipped"); remaining.Remove(n); }
                ready=ready.Where(n=>!failures.ContainsKey(n.ParentId)).ToList(); if(ready.Count==0) {if(remaining.Count>0&&skipped>0)continue; if(remaining.Count>0) throw new InvalidOperationException("No runnable graph nodes remain."); break; }
                var batch=ready.Take(Math.Max(1,3-active)).ToList(); if(batch.Count==0) { await Task.Delay(50,token).ConfigureAwait(false); continue; }
                var tasks=new List<Task>(); foreach(var n in batch) { remaining.Remove(n); active++; Status(n.Id,"queued","Waiting for app-server capacity"); string prompt="Goal: "+plan.Goal+"\nRoot plan:\n"+Limit(output["root"],MaxRootPlan)+"\nParent result:\n"+Limit(output[n.ParentId],MaxParent)+"\nYour assigned job: "+n.Job+"\nReturn a concise result for the coordinator. Do not spawn or delegate to agents."; tasks.Add(Task.Run(async ()=> { try { Status(n.Id,"running",n.Role+" running"); string v=await Execute(n,prompt,cli,workingDirectory,token).ConfigureAwait(false); lock(output) output[n.Id]=v; Say("RESULT / "+n.Name+" / "+n.Model+" / "+n.Effort+"\n"+v); Status(n.Id,"done","Complete"); } catch(OperationCanceledException) { Status(n.Id,"cancelled","Cancelled"); throw; } catch(Exception ex) { lock(failures) failures[n.Id]=ex.Message; Status(n.Id,"error",ex.Message); } finally { Interlocked.Decrement(ref active); } })); }
                try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { if(token.IsCancellationRequested) { foreach(var n in remaining) Status(n.Id,"cancelled","Cancelled before scheduling"); throw; } }
            }
            foreach(var f in failures) Say(f.Key+" failed: "+f.Value);
            Status("$review","running","Supervisor reviewing all worker results"); var aggregate=String.Join("\n\n",output.Where(p=>p.Key!="root").Select(p=>p.Key+":\n"+p.Value)); if(aggregate.Length>MaxWorkers) { aggregate=aggregate.Substring(0,MaxWorkers)+"\n[TRUNCATED: total worker output exceeded 100000 characters]"; Say("Worker aggregate truncated at 100000 characters."); }
            string failureText=failures.Count==0?"None":"\n"+String.Join("\n",failures.Select(f=>f.Key+": "+f.Value));
            string finalPrompt="Goal: "+plan.Goal+"\nYour initial plan:\n"+Limit(output["root"],MaxRootPlan)+"\nWorker results (may be truncated):\n"+aggregate+"\nFailed or skipped jobs:\n"+failureText+"\nReview and integrate these results into a final answer. Clearly disclose failed or skipped jobs and any partial result. Do not imply those jobs succeeded. Do not delegate.";
            try { string final=await Execute(root,finalPrompt,cli,workingDirectory,token,"$review").ConfigureAwait(false); Status("$review","done","Review complete"); return final; }
            catch(OperationCanceledException) { Status("$review","cancelled","Cancelled"); throw; }
            catch(Exception ex) {Status("$review","error",ex.Message);throw;}
        }
        static string Limit(string s,int max) { return s==null?"":s.Length<=max?s:s.Substring(0,max)+"\n[TRUNCATED]"; }
        static string ToPromptLimited(AgentPlan p,int max) { return Limit(GraphPlanHelpers.ToPrompt(p),max); }

                public Func<string,string,string,string,string,CancellationToken,Task<string>> CliRunner;
                /// <summary>Same, but told whether the node may edit files. Used for nodes marked CanEdit.</summary>
                public Func<string,string,string,string,string,bool,CancellationToken,Task<string>> CliRunnerEx;
        async Task<string> Execute(AgentNode node,string prompt,string cli,string cwd,CancellationToken token,string statusId=null)
        {
            if((node.ConnectionId??"").StartsWith("cli:",StringComparison.Ordinal))
            {
                if(CliRunner==null) throw new InvalidOperationException("Agent CLIs are unavailable in this run.");
                string agent=node.ConnectionId.Substring(4); Say(node.Name+" / "+agent+" / "+node.Model); Status(statusId??node.Id,"running",agent+" - "+node.Model);
                if(node.CanEdit&&CliRunnerEx!=null) return Limit(await CliRunnerEx(agent,node.Model,node.Effort,"Role: "+node.Role+". Assigned scope: "+node.Job+". You may edit files in the working folder within your scope; report what you changed.\n\n"+prompt,cwd,true,token).ConfigureAwait(false),node.Id=="root"?MaxRootPlan:MaxParent);
                return Limit(await CliRunner(agent,node.Model,node.Effort,"Role: "+node.Role+". Assigned scope: "+node.Job+". Reply with text only; do not edit files or run commands.\n\n"+prompt,cwd,token).ConfigureAwait(false),node.Id=="root"?MaxRootPlan:MaxParent);
            }
            if(GraphValidator.Connection(node.ConnectionId)=="codex") return await ExecuteCodex(node,prompt,cli,cwd,token,statusId).ConfigureAwait(false);
            var connection=services.Find(c=>c.Id==node.ConnectionId);
            if(connection==null) throw new InvalidOperationException("Missing service connection for "+node.Name+". Add it under Services.");
            Say(node.Name+" / API / "+connection.Name+" / "+node.Model+" / "+node.Effort);
            Status(statusId??node.Id,"running","API model: "+node.Model+" / "+node.Effort);
            return Limit(await ApiServices.ExecuteAsync(connection,node,prompt,token).ConfigureAwait(false),node.Id=="root"?MaxRootPlan:MaxParent);
        }
        async Task<string> ExecuteCodex(AgentNode node,string prompt,string cli,string cwd,CancellationToken token,string statusId)
        {
            StringBuilder answer=new StringBuilder(); var turnDone=new TaskCompletionSource<string>(); string thread=null;
            using(var rpc=new RpcClient(cli,cwd,(method,args)=> {
                if(args==null)return;
                if(method=="laica/transportClosed") turnDone.TrySetException(new IOException("Codex app-server closed before the turn finished."));
                if(method!=null && method.IndexOf("model",StringComparison.OrdinalIgnoreCase)>=0) { string reported=Get(args,"toModel")??Get(args,"model"); if(!String.IsNullOrEmpty(reported)&&reported!=node.Model) turnDone.TrySetException(new InvalidOperationException("Codex rerouted requested model "+node.Model+" to "+reported)); }
                if(method=="item/started") {var activeItem=args.ContainsKey("item")?args["item"] as Dictionary<string,object>:null;var itemType=Get(activeItem,"type");if(itemType=="commandExecution"||itemType=="webSearch") Say(node.Name+" / "+itemType+" / "+Limit(Get(activeItem,"command")??Get(activeItem,"query"),180));}
                if(method=="item/completed") { var item=args.ContainsKey("item")?args["item"] as Dictionary<string,object>:null; if(item!=null && Get(item,"type")=="agentMessage") { object c; if(item.TryGetValue("text",out c)&&c!=null) lock(answer) answer.Append(Convert.ToString(c)); } }
                if(method=="turn/completed") { var turn=args.ContainsKey("turn")?args["turn"] as Dictionary<string,object>:null; if(turn!=null && Get(turn,"status")!="completed") turnDone.TrySetException(new InvalidOperationException("Codex turn ended with status "+Get(turn,"status"))); else turnDone.TrySetResult(null); }
            }))
            {
                await rpc.Request("initialize",new { clientInfo=new{name="laica",version="0.1"}, capabilities=new{experimentalApi=true}},token).ConfigureAwait(false); rpc.Notify("initialized",new{});
                var threadConfig=new Dictionary<string,object> { {"agents.enabled",false}, {"model_reasoning_effort",node.Effort} };
                // Read the effective session config, then explicitly disable every configured MCP server for this thread.
                var cr=await rpc.Request("config/read",new { cwd=cwd, includeLayers=false },token).ConfigureAwait(false);
                object cfgRaw; var cfg=cr.TryGetValue("config",out cfgRaw)?cfgRaw as Dictionary<string,object>:null; object mcRaw; var mcs=cfg!=null&&cfg.TryGetValue("mcp_servers",out mcRaw)?mcRaw as Dictionary<string,object>:null;
                if(mcs!=null) foreach(var mcp in mcs.Keys) threadConfig["mcp_servers."+mcp+".enabled"]=false;
                string roleInstructions="Role: "+node.Role+". Assigned scope: "+node.Job+". Harness controls all scheduling; do not spawn or delegate to native agents. Workers must flag ambiguity, conflicting requirements and consequential architecture decisions for Sol rather than redesigning or expanding scope. Do not send external messages, use connectors, perform remote mutations, or execute non-read-only commands.";
                string configuredInstructions=cfg==null?null:Get(cfg,"developer_instructions");
                string developerInstructions=String.IsNullOrWhiteSpace(configuredInstructions)?roleInstructions:configuredInstructions+Environment.NewLine+Environment.NewLine+roleInstructions;
                var start=new Dictionary<string,object> { {"model",node.Model},{"cwd",cwd},{"approvalPolicy","never"},{"sandbox","read-only"},{"allowProviderModelFallback",false},{"developerInstructions",developerInstructions},{"config",threadConfig}};
                var sr=await rpc.Request("thread/start",start,token).ConfigureAwait(false); var th=sr.ContainsKey("thread")?sr["thread"] as Dictionary<string,object>:null; thread=th==null?null:Get(th,"id"); if(String.IsNullOrEmpty(thread)) throw new InvalidOperationException("Codex did not return a thread id.");
                if(th!=null && Get(th,"model")!=null && Get(th,"model")!=node.Model) throw new InvalidOperationException("Codex rerouted requested model "+node.Model+" to "+Get(th,"model"));
                if(th!=null && Get(th,"reasoningEffort")!=null && Get(th,"reasoningEffort")!=node.Effort) throw new InvalidOperationException("Codex changed requested reasoning effort.");
                Say(node.Name+" app-server thread: " + thread + " (" + node.Model + "/" + node.Effort + ")");
                Status(statusId??node.Id,"running","Actual model/effort: "+(Get(th,"model")??node.Model)+"/"+(Get(th,"reasoningEffort")??node.Effort));
                var input=new object[]{new Dictionary<string,object>{{"type","text"},{"text",prompt}}};
                await rpc.Request("turn/start",new Dictionary<string,object>{{"threadId",thread},{"input",input},{"effort",node.Effort},{"model",node.Model},{"approvalPolicy","never"},{"sandboxPolicy",new Dictionary<string,object>{{"type","readOnly"}}}},token).ConfigureAwait(false);
                Task delay=Task.Delay(TimeSpan.FromMinutes(15),token); Task done=await Task.WhenAny(turnDone.Task,delay).ConfigureAwait(false); if(done!=turnDone.Task) { if(thread!=null) try { using(var interruptLimit=new CancellationTokenSource(TimeSpan.FromSeconds(4))) await rpc.Request("turn/interrupt",new{threadId=thread},interruptLimit.Token).ConfigureAwait(false); } catch{} token.ThrowIfCancellationRequested(); throw new TimeoutException("Codex turn exceeded 15 minutes."); }
                await turnDone.Task.ConfigureAwait(false);
            }
            return Limit(answer.ToString(),node.Id=="root"?MaxRootPlan:MaxParent);
        }
        static string Get(Dictionary<string,object>d,string k){object v;return d!=null&&d.TryGetValue(k,out v)&&v!=null?Convert.ToString(v):null;}
    }
}
