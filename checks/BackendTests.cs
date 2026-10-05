using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Laica
{
    // Small deterministic backend contract checks. Call Run() from the v07 host check runner.
    public static class BackendTests
    {
        public static void Main() { try { Run(); RunLocalIntegrationAsync().GetAwaiter().GetResult(); Console.WriteLine("Backend checks passed."); } catch(Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode=1; } }
        public static void Run()
        {
            string root=Path.Combine(Environment.CurrentDirectory,".laica-v07-backend-"+Guid.NewGuid().ToString("N"));
            string app=Path.Combine(root,"app"),home=Path.Combine(root,"codex");Directory.CreateDirectory(app);Directory.CreateDirectory(home);
            try
            {
                using(var backend=new WorkspaceBackend(app,home))
                {
                    var json=new JavaScriptSerializer();
                    var state=backend.State();var stateText=json.Serialize(state);
                    Must(stateText.Contains("\"Version\":\"0.7.0\""),"versioned state DTO");
                    Must(!stateText.Contains("ProtectedKey")&&!stateText.Contains("ApiKey"),"secret material never enters state");
                    VerifyDataPaths();
                    var plan=GraphPlanHelpers.CreateStarter();plan.Goal="saved roundtrip";
                    plan.Nodes[0].Model="fixture-root";plan.Nodes[0].Effort="low";
                    foreach(var node in plan.Nodes.Skip(1)){node.Model="fixture-worker";node.Effort="medium";}
                    SetModels(backend,new List<ModelInfo>{new ModelInfo{Id="fixture-root",Name="Fixture root",ConnectionId="codex",Efforts=new List<string>{"low","medium"}},new ModelInfo{Id="fixture-worker",Name="Fixture worker",ConnectionId="codex",Efforts=new List<string>{"medium","high"}}});
                    backend.HandleAsync("updatePlan",new Dictionary<string,object>{{"Plan",plan},{"Mode","multi"},{"SnapToGrid",true}}).GetAwaiter().GetResult();
                    Must(File.Exists(Path.Combine(app,"plans","last-plan-v05.json")),"last plan saved at compatible path");
                    backend.HandleAsync("saveProfile",new Dictionary<string,object>{{"Name","First"},{"Create",true}}).GetAwaiter().GetResult();
                    var mutated=ProfileLibrary.Copy(plan);mutated.Goal="draft";
                    backend.HandleAsync("updatePlan",new Dictionary<string,object>{{"Plan",mutated},{"Mode","multi"},{"SnapToGrid",true}}).GetAwaiter().GetResult();
                    var needs=backend.HandleAsync("loadProfile",new Dictionary<string,object>{{"Id",Selected(backend)}}).GetAwaiter().GetResult() as Dictionary<string,object>;
                    Must(needs!=null&&Convert.ToBoolean(needs["NeedsResolution"]),"dirty profile asks for resolution");
                    backend.HandleAsync("loadProfile",new Dictionary<string,object>{{"Id",Selected(backend)},{"Resolution","discard"}}).GetAwaiter().GetResult();
                    Must(Goal(backend)=="saved roundtrip","profile discard loads saved draft");
                    var broken=ProfileLibrary.Copy(plan);broken.Nodes.Add(new AgentNode{Id="bad",ParentId="missing",Name="Bad",Role="implementer",Model="m",Effort="default",Job=""});
                    Reject(()=>backend.HandleAsync("updatePlan",new Dictionary<string,object>{{"Plan",broken},{"Mode","multi"},{"SnapToGrid",false}}).GetAwaiter().GetResult(),"missing/cyclic draft rejected");
                    var cycle=ProfileLibrary.Copy(plan);cycle.Nodes[1].ParentId=cycle.Nodes[2].Id;cycle.Nodes[2].ParentId=cycle.Nodes[1].Id;
                    Reject(()=>backend.HandleAsync("importPlan",new Dictionary<string,object>{{"Plan",cycle}}).GetAwaiter().GetResult(),"cycle rejected");
                    var active=ProfileLibrary.Copy(plan);active.Goal="activation goal";
                    backend.HandleAsync("activateTeam",new Dictionary<string,object>{{"Plan",active}}).GetAwaiter().GetResult();
                    var activeFile=json.Deserialize<AgentPlan>(File.ReadAllText(Path.Combine(app,"active-team.json")));
                    Must(activeFile.Goal==""&&activeFile.Nodes[0].Model=="fixture-root"&&activeFile.Nodes[0].Effort=="low"&&Goal(backend)=="saved roundtrip","activation accepts a discovered non-default supervisor and preserves draft");
                    var unsupported=ProfileLibrary.Copy(active);unsupported.Nodes[0].Model="not-discovered";
                    Reject(()=>backend.HandleAsync("activateTeam",new Dictionary<string,object>{{"Plan",unsupported}}).GetAwaiter().GetResult(),"activation rejects unavailable supervisor model");
                    active.Nodes[1].ConnectionId="external";
                    Reject(()=>backend.HandleAsync("activateTeam",new Dictionary<string,object>{{"Plan",active}}).GetAwaiter().GetResult(),"activation rejects non-Codex agents");
                    backend.HandleAsync("saveService",new Dictionary<string,object>{{"Name","Local"},{"Provider","ollama"},{"BaseUrl","http://localhost:11434/v1"},{"KeyEnvironmentVariable",""},{"ApiKey","preserved-secret"}}).GetAwaiter().GetResult();
                    string serviceFile=File.ReadAllText(Path.Combine(app,"services.json"));
                    Must(!serviceFile.Contains("preserved-secret"),"service file contains encrypted key only");
                    Must(!json.Serialize(backend.State()).Contains("preserved-secret"),"service key absent from frontend state");
                    string serviceId=Convert.ToString(((Dictionary<string,object>)((object[])((Dictionary<string,object>)backend.State())["Services"])[0])["Id"]);
                    backend.HandleAsync("saveService",new Dictionary<string,object>{{"Id",serviceId},{"Name","Local renamed"},{"Provider","ollama"},{"BaseUrl","http://localhost:11434/v1"},{"KeyEnvironmentVariable",""},{"ApiKey",""}}).GetAwaiter().GetResult();
                    Must(Convert.ToBoolean(((Dictionary<string,object>)((object[])((Dictionary<string,object>)backend.State())["Services"])[0])["HasKey"]),"omitted key preserves DPAPI secret");
                    SetModels(backend,new List<ModelInfo>{new ModelInfo{Id="local-model",Name="Local",ConnectionId=serviceId,Efforts=new List<string>{"default"}},new ModelInfo{Id="fixture-root",Name="Fixture root",ConnectionId="codex",Efforts=new List<string>{"low","medium"}},new ModelInfo{Id="fixture-worker",Name="Fixture worker",ConnectionId="codex",Efforts=new List<string>{"medium","high"}}});
                    backend.HandleAsync("applyService",new Dictionary<string,object>{{"Id",serviceId},{"ModelId","local-model"}}).GetAwaiter().GetResult();
                    Must(((AgentPlan)((Dictionary<string,object>)backend.State())["Plan"]).Nodes.All(n=>n.ConnectionId==serviceId),"local service applies to root and every worker");
                    backend.HandleAsync("applyService",new Dictionary<string,object>{{"Id","codex"},{"ModelId","fixture-worker"}}).GetAwaiter().GetResult();
                    var codexPlan=(AgentPlan)((Dictionary<string,object>)backend.State())["Plan"];
                    Must(codexPlan.Nodes.All(n=>n.ConnectionId=="codex"&&n.Model=="fixture-worker"&&n.Effort=="medium"),"selected discovered Codex model applies to supervisor and workers");
                    Reject(()=>backend.HandleAsync("run",new Dictionary<string,object>()).GetAwaiter().GetResult(),"harness rejects Codex root");
                    backend.HandleAsync("updatePlan",new Dictionary<string,object>{{"Plan",codexPlan},{"Mode","solo"},{"SnapToGrid",false}}).GetAwaiter().GetResult();
                    backend.HandleAsync("chooseWorkingDirectory",new Dictionary<string,object>{{"Path",home}}).GetAwaiter().GetResult();
                }
                using(var backend=new WorkspaceBackend(app,home))
                {
                    var state=(Dictionary<string,object>)backend.State();
                    Must(Convert.ToString(state["Mode"])=="solo"&&!Convert.ToBoolean(state["SnapToGrid"]),"mode and canvas preference survive restart");
                    Must(String.Equals(Convert.ToString(state["WorkingDirectory"]),Path.GetFullPath(home),StringComparison.OrdinalIgnoreCase),"working directory survives restart");
                    backend.HandleAsync("loadProfile",new Dictionary<string,object>{{"Id",""}}).GetAwaiter().GetResult();
                    Must(((Dictionary<string,object>)backend.State())["SelectedProfileId"]==null,"empty profile id clears selection");
                }
                // Loading a damaged service file reports its failure without rewriting it.
                string bad=Path.Combine(app,"services.json");File.WriteAllText(bad,"{broken");
                using(var backend=new WorkspaceBackend(app,home)){
                    Must(File.ReadAllText(bad)=="{broken","corrupt settings remain untouched");
                    Reject(()=>backend.HandleAsync("saveService",new Dictionary<string,object>{{"Name","Do not overwrite"},{"Provider","ollama"},{"BaseUrl","http://localhost:11434/v1"}}).GetAwaiter().GetResult(),"writes refuse to replace corrupt stores");
                    Must(File.ReadAllText(bad)=="{broken","failed recovery leaves original bytes intact");
                }
            }
            finally { try{Directory.Delete(root,true);}catch{} }
        }
        static void VerifyDataPaths()
        {
            string original=Environment.GetEnvironmentVariable("LAICA_HOME");
            try
            {
                Environment.SetEnvironmentVariable("LAICA_HOME",null);
                string expected=Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LAICA","workspace"));
                Must(String.Equals(DataPaths.DataDirectory(),expected,StringComparison.OrdinalIgnoreCase),"shared default data directory is LAICA workspace under LocalAppData");
                string fixture=Path.Combine(Path.GetTempPath(),"laica-path-fixture-"+Guid.NewGuid().ToString("N"));
                Environment.SetEnvironmentVariable("LAICA_HOME",fixture);
                Must(String.Equals(DataPaths.DataDirectory(),Path.GetFullPath(fixture),StringComparison.OrdinalIgnoreCase),"LAICA_HOME overrides shared data directory");
            }
            finally { Environment.SetEnvironmentVariable("LAICA_HOME",original); }
        }
        static string Selected(WorkspaceBackend b){var s=(Dictionary<string,object>)b.State();return Convert.ToString(s["SelectedProfileId"]);}
        static string Goal(WorkspaceBackend b){var s=(Dictionary<string,object>)b.State();return Convert.ToString(((AgentPlan)s["Plan"]).Goal);}
        static void SetModels(WorkspaceBackend b,List<ModelInfo> models){typeof(WorkspaceBackend).GetField("models",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(b,models);}
        static void Must(bool ok,string message){if(!ok)throw new Exception("Backend check failed: "+message);}
        static void Reject(Action action,string message){bool rejected=false;try{action();}catch{rejected=true;}Must(rejected,message);}

        static async Task RunLocalIntegrationAsync()
        {
            string root=Path.Combine(Environment.CurrentDirectory,".laica-v07-integration-"+Guid.NewGuid().ToString("N"));
            string app=Path.Combine(root,"app"),home=Path.Combine(root,"codex");Directory.CreateDirectory(app);Directory.CreateDirectory(home);
            try
            {
            using(var fixture=new BackendLoopbackFixture())
            {
                var backend=new WorkspaceBackend(app,home);
                try
                {
                    backend.HandleAsync("saveService",new Dictionary<string,object>{{"Name","Fixture only"},{"Provider","compatible"},{"BaseUrl",fixture.BaseUrl+"/v1"},{"KeyEnvironmentVariable",""}}).GetAwaiter().GetResult();
                    var service=(Dictionary<string,object>)((object[])((Dictionary<string,object>)backend.State())["Services"])[0];string serviceId=Convert.ToString(service["Id"]);
                    var tested=(Dictionary<string,object>)await backend.HandleAsync("testService",new Dictionary<string,object>{{"Id",serviceId}}).ConfigureAwait(false);
                    Must(((object[])tested["Models"]).Length==1,"testService discovers a local fixture model");
                    Must(((object[])((Dictionary<string,object>)backend.State())["Models"]).Length==1,"testService updates the workspace model catalog");
                    var plan=new AgentPlan{Goal="fixture integration goal",Nodes=new List<AgentNode>{new AgentNode{Id="root",Name="Supervisor",Role="supervisor",Model="fixture-model",Effort="default",ConnectionId=serviceId,Job="Run fixture-only request"}}};
                    backend.HandleAsync("updatePlan",new Dictionary<string,object>{{"Plan",plan},{"Mode","solo"},{"SnapToGrid",false}}).GetAwaiter().GetResult();

                    await backend.HandleAsync("run",new Dictionary<string,object>()).ConfigureAwait(false);
                    await WaitFor(()=>fixture.PostCount>=1,"first fixture request").ConfigureAwait(false);
                    await WaitFor(()=>!BoolState(backend,"Busy"),"first local run completion").ConfigureAwait(false);
                    Must(Convert.ToString(State(backend)["Answer"])=="fixture answer","local run completion returns Answer");
                    Must(HasEvent(backend,"Task received and ready for the team.")&&HasEvent(backend,"Final answer is ready."),"run events use human descriptions for input and output stages");
                    Must(Convert.ToString(((Dictionary<string,string>)State(backend)["NodeStates"])["root"])=="done","completed run exposes final node state");

                    await backend.HandleAsync("run",new Dictionary<string,object>()).ConfigureAwait(false);
                    await WaitFor(()=>fixture.PostCount>=2,"cancellable fixture request").ConfigureAwait(false);
                    await backend.HandleAsync("stop",new Dictionary<string,object>()).ConfigureAwait(false);
                    await WaitFor(()=>!BoolState(backend,"Busy"),"cancelled local run cleanup").ConfigureAwait(false);
                    Must(Convert.ToString(State(backend)["Status"]).Contains("stopped"),"stop cancellation resets Busy and reports stopped status");
                    Must(HasKind(backend,"cancelled"),"stop cancellation emits a RunEvents cancellation record");

                    await backend.HandleAsync("run",new Dictionary<string,object>()).ConfigureAwait(false);
                    await WaitFor(()=>fixture.PostCount>=3,"recovery fixture request").ConfigureAwait(false);
                    await WaitFor(()=>!BoolState(backend,"Busy"),"run after cancellation completion").ConfigureAwait(false);
                    Must(Convert.ToString(State(backend)["Answer"])=="fixture answer","a new local run succeeds after stop");

                    await backend.HandleAsync("run",new Dictionary<string,object>()).ConfigureAwait(false);
                    await WaitFor(()=>fixture.PostCount>=4,"failing fixture request").ConfigureAwait(false);
                    await WaitFor(()=>!BoolState(backend,"Busy"),"failed local run cleanup").ConfigureAwait(false);
                    Must(HasKind(backend,"error")&&Convert.ToString(State(backend)["Status"]).StartsWith("Run failed:",StringComparison.Ordinal),"run failure emits an explicit error event and status");

                    await backend.HandleAsync("run",new Dictionary<string,object>()).ConfigureAwait(false);
                    await WaitFor(()=>fixture.PostCount>=5,"dispose-cancelled fixture request").ConfigureAwait(false);
                    backend.Dispose();
                    await WaitFor(()=>!BoolState(backend,"Busy"),"disposed run cleanup").ConfigureAwait(false);
                    Must(Convert.ToString(State(backend)["Status"]).Contains("workspace closed"),"disposing the backend cancels its active run");
                    Must(HasKind(backend,"cancelled"),"dispose cancellation emits a RunEvents cancellation record");
                }
                finally{backend.Dispose();}
            }
            }
            finally{try{Directory.Delete(root,true);}catch{}}
        }

        static Dictionary<string,object> State(WorkspaceBackend b){return (Dictionary<string,object>)b.State();}
        static bool BoolState(WorkspaceBackend b,string key){return Convert.ToBoolean(State(b)[key]);}
        static bool HasKind(WorkspaceBackend b,string kind){return ((object[])State(b)["RunEvents"]).Cast<Dictionary<string,object>>().Any(e=>Convert.ToString(e["Kind"])==kind);}
        static bool HasEvent(WorkspaceBackend b,string text){return ((object[])State(b)["RunEvents"]).Cast<Dictionary<string,object>>().Any(e=>Convert.ToString(e["Text"]).Contains(text));}
        static async Task WaitFor(Func<bool> condition,string description){var until=DateTime.UtcNow.AddSeconds(8);while(DateTime.UtcNow<until){if(condition())return;await Task.Delay(20).ConfigureAwait(false);}throw new TimeoutException("Timed out waiting for "+description+".");}

        sealed class BackendLoopbackFixture:IDisposable
        {
            readonly TcpListener listener;
            readonly CancellationTokenSource shutdown=new CancellationTokenSource();
            readonly Task acceptLoop;
            readonly ConcurrentBag<Task> clients=new ConcurrentBag<Task>();
            int posts;
            public string BaseUrl{get;private set;}
            public int PostCount{get{return Volatile.Read(ref posts);}}
            public BackendLoopbackFixture(){listener=new TcpListener(IPAddress.Loopback,0);listener.Start();BaseUrl="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port;acceptLoop=Task.Run(async delegate{await AcceptLoopAsync().ConfigureAwait(false);});}
            async Task AcceptLoopAsync(){while(!shutdown.IsCancellationRequested){TcpClient client;try{client=await listener.AcceptTcpClientAsync().ConfigureAwait(false);}catch{break;}clients.Add(Task.Run(async delegate{await HandleAsync(client).ConfigureAwait(false);}));}}
            async Task HandleAsync(TcpClient client)
            {
                using(client)try
                {
                    var stream=client.GetStream();var header=new List<byte>();int a=0,b=0,c=0,d=0;
                    while(header.Count<32768){int x=stream.ReadByte();if(x<0)return;header.Add((byte)x);a=b;b=c;c=d;d=x;if(a==13&&b==10&&c==13&&d==10)break;}
                    string[] lines=Encoding.ASCII.GetString(header.ToArray()).Split(new[]{"\r\n"},StringSplitOptions.None);string[] first=lines[0].Split(' ');int length=0;
                    for(int i=1;i<lines.Length;i++){int ix=lines[i].IndexOf(':');if(ix>0&&lines[i].Substring(0,ix).Equals("Content-Length",StringComparison.OrdinalIgnoreCase))Int32.TryParse(lines[i].Substring(ix+1).Trim(),out length);}
                    byte[] body=new byte[length];int read=0;while(read<length){int n=await stream.ReadAsync(body,read,length-read,shutdown.Token).ConfigureAwait(false);if(n==0)return;read+=n;}
                    bool get=first.Length>0&&first[0]=="GET";int call=get?0:Interlocked.Increment(ref posts);
                    if(call==2||call==5)await Task.Delay(Timeout.Infinite,shutdown.Token).ConfigureAwait(false);
                    int status=call==4?500:200;string responseBody=get?"{\"data\":[{\"id\":\"fixture-model\"}]}":status==200?"{\"model\":\"fixture-model\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"fixture answer\"}}]}":"{\"error\":{\"message\":\"fixture failure\"}}";
                    byte[] payload=Encoding.UTF8.GetBytes(responseBody);byte[] prefix=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+" "+(status==200?"OK":"Error")+"\r\nContent-Type: application/json\r\nContent-Length: "+payload.Length+"\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(prefix,0,prefix.Length,shutdown.Token).ConfigureAwait(false);await stream.WriteAsync(payload,0,payload.Length,shutdown.Token).ConfigureAwait(false);await stream.FlushAsync(shutdown.Token).ConfigureAwait(false);
                }
                catch{}
            }
            public void Dispose(){shutdown.Cancel();listener.Stop();try{acceptLoop.Wait(500);}catch{}foreach(var task in clients)try{task.Wait(500);}catch{}shutdown.Dispose();}
        }
    }
}
