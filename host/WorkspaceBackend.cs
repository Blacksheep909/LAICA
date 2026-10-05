using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Laica
{
    /// <summary>UI-independent state and command boundary for the LAICA WebView host.</summary>
    public sealed class WorkspaceBackend : IDisposable
    {
        readonly string appDir, codexHome;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 40 };
        readonly object gate = new object();
        readonly CodexActivityObserver observer;
        readonly HashSet<string> invalidStores = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AgentPlan plan;
        List<ModelInfo> models = new List<ModelInfo>();
        List<ServiceConnection> services = new List<ServiceConnection>();
        ProfileLibrary profiles = new ProfileLibrary();
        string mode = "multi", status = "Ready", answer = "", workingDirectory;
        bool snapToGrid, refreshing, busy, disposed;
        CancellationTokenSource runToken;
        readonly List<ActivityEvent> runEvents = new List<ActivityEvent>();
        readonly Dictionary<string, string> nodeStates = new Dictionary<string, string>();
        DateTime overviewReadUtc = DateTime.MinValue;
        object overview;
        public event Action<object> Changed;
        public string WorkingDirectory { get { lock (gate) return workingDirectory; } set { ChooseWorkingDirectory(new Dictionary<string,object>{{"Path",value}}); } }

        public WorkspaceBackend(string appDir, string codexHome)
        {
            if (String.IsNullOrWhiteSpace(appDir)) throw new ArgumentException("Application directory is required.", "appDir");
            if (String.IsNullOrWhiteSpace(codexHome)) throw new ArgumentException("Codex home directory is required.", "codexHome");
            this.appDir = Path.GetFullPath(appDir); this.codexHome = Path.GetFullPath(codexHome);
            workingDirectory = Environment.CurrentDirectory;
            observer = new CodexActivityObserver(this.codexHome);
            plan = GraphPlanHelpers.CreateStarter();
            LoadSavedState();
        }

        public object State()
        {
            lock (gate) return new Dictionary<string, object> {
                {"Plan", ProfileLibrary.Copy(plan)}, {"Models", models.Select(ModelDto).ToArray()},
                {"Services", services.Select(ServiceDto).ToArray()}, {"Profiles", profiles.Profiles.Select(ProfileDto).ToArray()},
                {"SelectedProfileId", profiles.SelectedId}, {"Mode", mode}, {"SnapToGrid", snapToGrid},
                {"Busy", busy}, {"Refreshing", refreshing}, {"Answer", answer}, {"Status", status},
                {"Version", "0.7.0"}, {"WorkingDirectory", workingDirectory},
                {"RunEvents", runEvents.Select(EventDto).ToArray()}, {"NodeStates", new Dictionary<string,string>(nodeStates)}
            };
        }

        public Task<object> HandleAsync(string method, Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            try
            {
                object result;
                switch (method ?? "")
                {
                    case "state": result = State(); break;
                    case "refreshModels": return AwaitPublish(RefreshModelsAsync());
                    case "updatePlan": result = UpdatePlan(payload); break;
                    case "saveProfile": result = SaveProfile(payload); break;
                    case "loadProfile": result = LoadProfile(payload); break;
                    case "renameProfile": result = RenameProfile(payload); break;
                    case "deleteProfile": result = DeleteProfile(payload); break;
                    case "setMode": result = SetMode(payload); break;
                    case "activateTeam": result = ActivateTeam(payload); break;
                    case "saveService": result = SaveService(payload); break;
                    case "removeService": result = RemoveService(payload); break;
                    case "testService": return TestServiceAsync(payload);
                    case "applyService": result = ApplyService(payload); break;
                    case "sessions": result = observer.ListSessions().Select(SessionDto).ToArray(); break;
                    case "activity": result = ActivityDto(observer.Read(Text(payload,"Id"))); break;
                    case "teamOverview": result = TeamOverview(); break;
                    case "run": result = StartRun(); break;
                    case "stop": result = StopRun(); break;
                    case "importPlan": result = ImportPlan(payload); break;
                    case "exportPlan": result = ProfileLibrary.Copy(plan); break;
                    case "chooseWorkingDirectory": result = ChooseWorkingDirectory(payload); break;
                    default: throw new InvalidOperationException("Unknown workspace command: " + method);
                }
                Publish(); return Task.FromResult(result);
            }
            catch (Exception ex) { return Task.FromException<object>(ex); }
        }

        Task<object> AwaitPublish(Task<object> task) { return task.ContinueWith(t => { Publish(); if (t.IsFaulted) throw t.Exception.InnerException; return t.Result; }, TaskScheduler.Default); }
        async Task<object> RefreshModelsAsync()
        {
            lock(gate) { EnsureNotBusy(); if(refreshing) return State(); refreshing=true; status="Refreshing model catalog…"; }
            Publish(); var found=new List<ModelInfo>(); var problems=new List<string>(); string cli=ModelCatalog.FindCodex();
            if(cli!=null) try { found.AddRange(await ModelCatalog.LoadAsync(cli,workingDirectory,CancellationToken.None).ConfigureAwait(false)); } catch(Exception ex) { problems.Add("Codex: "+ex.Message); }
            else problems.Add("Codex CLI unavailable.");
            List<ServiceConnection> copy; lock(gate) copy=services.Select(CloneService).ToList();
            foreach(var c in copy) try { using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20))) found.AddRange(await ApiServices.LoadModelsAsync(c,timeout.Token).ConfigureAwait(false)); } catch(Exception ex) { problems.Add(c.Name+": "+ex.Message); }
            lock(gate) { models=found; refreshing=false; status=problems.Count==0?"Model choices refreshed.":"Some services unavailable. "+String.Join(" / ",problems); }
            Publish(); return State();
        }

        object UpdatePlan(Dictionary<string,object> p)
        {
            EnsureNotBusy();
            var next=ReadPlan(Value(p,"Plan")); ValidateStructure(next);
            string nextMode=Text(p,"Mode")??mode; if(nextMode!="solo"&&nextMode!="multi")throw new ArgumentException("Mode must be solo or multi.");
            bool snap=Bool(p,"SnapToGrid",snapToGrid); string planJson=json.Serialize(next);EnsurePlanSize(planJson);
            string oldModeFile=ReadFileOrNull(ModePath),oldPreferences=ReadFileOrNull(PreferencesPath);
            try { if(nextMode!=mode)SaveMode(nextMode);if(snap!=snapToGrid)SavePreferences(snap,workingDirectory);AtomicSave(LastPlanPath,planJson); }
            catch { try{if(oldModeFile!=null)AtomicSave(ModePath,oldModeFile);else if(File.Exists(ModePath))File.Delete(ModePath);if(oldPreferences!=null)AtomicSave(PreferencesPath,oldPreferences);else if(File.Exists(PreferencesPath))File.Delete(PreferencesPath);}catch{}throw; }
            lock(gate){plan=next;mode=nextMode;snapToGrid=snap;status="Plan saved.";} return State();
        }
        object SaveProfile(Dictionary<string,object> p)
        {
            EnsureNotBusy();
            string name=Text(p,"Name"); bool create=Bool(p,"Create",false); ProfileLibrary copy=CloneProfiles(profiles);
            copy.Save(name,plan,mode,snapToGrid,create); SaveProfileLibrary(copy); lock(gate){profiles=copy;status="Profile saved.";} return State();
        }
        object LoadProfile(Dictionary<string,object> p)
        {
            EnsureNotBusy();
            string id=Text(p,"Id"), resolution=Text(p,"Resolution");
            if(String.IsNullOrEmpty(id)){var copy=CloneProfiles(profiles);copy.SelectedId=null;SaveProfileLibrary(copy);lock(gate){profiles=copy;status="Profile selection cleared; the current draft is preserved.";}return State();}
            var profile=profiles.Profiles.FirstOrDefault(x=>x.Id==id); if(profile==null)throw new ArgumentException("Profile not found.");
            if(IsDirty()&&String.IsNullOrEmpty(resolution))return new Dictionary<string,object>{{"NeedsResolution",true}};
            if(!String.IsNullOrEmpty(resolution)&&resolution!="save"&&resolution!="discard")throw new ArgumentException("Resolution must be save or discard.");
            var nextProfiles=CloneProfiles(profiles);
            if(resolution=="save") nextProfiles.Save(SelectedProfileName()??"Current team",plan,mode,snapToGrid,false);
            var nextProfile=nextProfiles.Profiles.FirstOrDefault(x=>x.Id==id);if(nextProfile==null)throw new InvalidOperationException("Profile disappeared while resolving the draft.");
            var nextPlan=ProfileLibrary.Copy(nextProfile.Plan);ValidateStructure(nextPlan);nextProfiles.SelectedId=id;
            string profilePath=Path.Combine(appDir,"profiles.json"),oldProfiles=File.Exists(profilePath)?File.ReadAllText(profilePath):null;
            string oldMode=mode,oldPrefs=ReadFileOrNull(PreferencesPath),oldModeFile=ReadFileOrNull(ModePath),oldPlanFile=ReadFileOrNull(LastPlanPath);
            AtomicSave(profilePath,nextProfiles.Serialize());
            try { AtomicSave(LastPlanPath,json.Serialize(nextPlan)); SaveMode(nextProfile.Mode); SavePreferences(nextProfile.SnapToGrid,workingDirectory); }
            catch { try { if(oldProfiles==null){if(File.Exists(profilePath))File.Delete(profilePath);}else AtomicSave(profilePath,oldProfiles);if(oldPlanFile!=null)AtomicSave(LastPlanPath,oldPlanFile);else if(File.Exists(LastPlanPath))File.Delete(LastPlanPath);if(oldModeFile!=null)AtomicSave(ModePath,oldModeFile);else if(File.Exists(ModePath))File.Delete(ModePath);if(oldPrefs!=null)AtomicSave(PreferencesPath,oldPrefs);else if(File.Exists(PreferencesPath))File.Delete(PreferencesPath); } catch { } throw; }
            lock(gate){profiles=nextProfiles;plan=nextPlan;mode=nextProfile.Mode;snapToGrid=nextProfile.SnapToGrid;answer="";status="Profile loaded.";} return State();
        }
        object RenameProfile(Dictionary<string,object> p)
        {
            EnsureNotBusy();string name=Text(p,"Name");ProfileLibrary.ValidateName(name);var copy=CloneProfiles(profiles);var selected=copy.Selected;if(selected==null)throw new InvalidOperationException("No profile is selected.");selected.Name=name.Trim();SaveProfileLibrary(copy);lock(gate)profiles=copy;return State();
        }
        object DeleteProfile(Dictionary<string,object> p)
        {
            EnsureNotBusy();string id=Text(p,"Id");var copy=CloneProfiles(profiles);var item=copy.Profiles.FirstOrDefault(x=>x.Id==id);if(item==null)throw new ArgumentException("Profile not found.");copy.Profiles.Remove(item);if(copy.SelectedId==id)copy.SelectedId=null;SaveProfileLibrary(copy);lock(gate)profiles=copy;return State();
        }
        object SetMode(Dictionary<string,object> p){EnsureNotBusy();string next=Text(p,"Mode");if(next!="solo"&&next!="multi")throw new ArgumentException("Mode must be solo or multi.");SaveMode(next);lock(gate){mode=next;status="Harness mode applied.";}return State();}
        object ActivateTeam(Dictionary<string,object> p)
        {
            EnsureNotBusy();
            AgentPlan next=p.ContainsKey("Plan")?ReadPlan(p["Plan"]):ProfileLibrary.Copy(plan);ValidateStructure(next);
            if(next.Nodes.Any(n=>GraphValidator.Connection(n.ConnectionId)!="codex"))throw new ArgumentException("Activated teams must use Codex for every agent.");
            GraphValidator.Validate(next,models,false);next.Goal="";string snapshot=json.Serialize(next);EnsurePlanSize(snapshot);AtomicSave(Path.Combine(appDir,"active-team.json"),snapshot);lock(gate){status="Team activated.";}return State();
        }
        object SaveService(Dictionary<string,object> p)
        {
            EnsureNotBusy();
            string id=Text(p,"Id");var prior=services.FirstOrDefault(x=>x.Id==id);bool exists=prior!=null;
            var c=new ServiceConnection{Id=exists?id:Guid.NewGuid().ToString("N"),Name=Text(p,"Name"),Provider=Text(p,"Provider"),BaseUrl=Text(p,"BaseUrl"),KeyEnvironmentVariable=Text(p,"KeyEnvironmentVariable")??"",ProtectedKey=exists?prior.ProtectedKey:""};
            if(p.ContainsKey("RemoveKey")&&Bool(p,"RemoveKey",false))c.ProtectedKey="";
            if(p.ContainsKey("ApiKey")&&Value(p,"ApiKey")!=null){string key=Convert.ToString(Value(p,"ApiKey"));if(key.Length>0){if(key.Length>1024*1024)throw new ArgumentException("API key exceeds 1 MB.");c.ProtectedKey=ApiServices.ProtectKey(key);}}
            ApiServices.ValidateConnection(c);var copy=services.Select(CloneService).ToList();if(exists)copy[copy.FindIndex(x=>x.Id==id)]=c;else{if(copy.Count>=20)throw new InvalidOperationException("Up to 20 services are supported.");copy.Add(c);}SaveServiceList(copy);lock(gate){services=copy;models.RemoveAll(m=>m.ConnectionId==c.Id);PruneModels();status="Service saved.";}return State();
        }
        object RemoveService(Dictionary<string,object> p){EnsureNotBusy();string id=Text(p,"Id");var copy=services.Select(CloneService).Where(x=>x.Id!=id).ToList();if(copy.Count==services.Count)throw new ArgumentException("Service not found.");SaveServiceList(copy);lock(gate){services=copy;PruneModels();}return State();}
        async Task<object> TestServiceAsync(Dictionary<string,object> p)
        {
            EnsureNotBusy();var c=services.FirstOrDefault(x=>x.Id==Text(p,"Id"));if(c==null)throw new ArgumentException("Service not found.");
            try { List<ModelInfo> catalog;using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20)))catalog=await ApiServices.LoadModelsAsync(CloneService(c),timeout.Token).ConfigureAwait(false);
                lock(gate){models.RemoveAll(m=>m.ConnectionId==c.Id);models.AddRange(catalog);status=c.Name+" connected; "+catalog.Count+" model(s) available.";}Publish();return new Dictionary<string,object>{{"Models",catalog.Select(ModelDto).ToArray()},{"Status",status}};
            } catch(Exception ex){lock(gate)status=c.Name+" connection failed: "+ex.Message;Publish();throw;}
        }
        object ApplyService(Dictionary<string,object> p)
        {
            EnsureNotBusy();string id=Text(p,"Id"), modelId=Text(p,"ModelId");var model=models.FirstOrDefault(m=>m.Id==modelId&&m.ConnectionId==id);if(model==null)throw new ArgumentException("Model is unavailable for this service.");var next=ProfileLibrary.Copy(plan);
            if(model.Efforts==null||model.Efforts.Count==0)throw new ArgumentException("The selected model does not report a supported reasoning effort.");
            foreach(var n in next.Nodes){n.ConnectionId=id;n.Model=model.Id;if(!model.Efforts.Contains(n.Effort))n.Effort=model.Efforts.Contains("default")?"default":model.Efforts.Contains("medium")?"medium":model.Efforts[0];}
            ValidateStructure(next);GraphValidator.Validate(next,models,false);string contents=json.Serialize(next);EnsurePlanSize(contents);AtomicSave(LastPlanPath,contents);lock(gate){plan=next;status="Model applied to the team.";}return State();
        }
        object StartRun()
        {
            AgentPlan snapshot;List<ModelInfo> catalog;List<ServiceConnection> connections;string cwd;bool solo;CancellationToken token;
            lock(gate){if(busy)throw new InvalidOperationException("A run is already active.");if(refreshing)throw new InvalidOperationException("Wait for the model catalog to finish refreshing.");ValidateStructure(plan);var root=plan.Nodes.Single(n=>n.Id=="root");if(GraphValidator.Connection(root.ConnectionId)=="codex")throw new InvalidOperationException("LAICA harness runs require a local service supervisor. Activate Codex teams for native Codex handoff.");GraphValidator.Validate(plan,models,mode=="solo");if(plan.Goal==null||plan.Goal.Trim().Length==0)throw new ArgumentException("Enter a goal before starting the run.");busy=true;answer="";runEvents.Clear();nodeStates.Clear();runToken=new CancellationTokenSource();token=runToken.Token;snapshot=ProfileLibrary.Copy(plan);catalog=models.ToList();connections=services.Select(CloneService).ToList();cwd=workingDirectory;solo=mode=="solo";status="Run started.";AddRunEvent("run","started","Run started.");}
            var runner=new GraphRunner();runner.NodeStatus+=(id,state,detail)=>{lock(gate){nodeStates[id]=state;AddRunStateEvent(id,state,detail,snapshot);}Publish();};runner.Message+=message=>{lock(gate)AddRunMessage(message,snapshot);Publish();};
            Task.Run(async()=>{try{string cli=ModelCatalog.FindCodex();string output=await runner.RunAsync(snapshot,catalog,cli,cwd,solo,connections,token).ConfigureAwait(false);lock(gate){answer=output;status="Run completed.";AddRunEvent("run","completed","Run completed successfully.");}}catch(OperationCanceledException){lock(gate){status=disposed?"Run cancelled because the workspace closed.":"Run stopped.";AddRunEvent("run","cancelled",status);}}catch(Exception ex){lock(gate){status="Run failed: "+ex.Message;AddRunEvent("run","error",status);}}finally{lock(gate){busy=false;if(runToken!=null){runToken.Dispose();runToken=null;}}Publish();}});
            Publish();return State();
        }
        object StopRun(){lock(gate){if(runToken!=null){status="Stopping this LAICA run…";AddRunEvent("run","stopping","Stop requested; cancelling LAICA jobs.");runToken.Cancel();}}Publish();return State();}
        object ImportPlan(Dictionary<string,object> p){EnsureNotBusy();var next=ReadPlan(Value(p,"Plan"));ValidateStructure(next);string contents=json.Serialize(next);EnsurePlanSize(contents);AtomicSave(LastPlanPath,contents);lock(gate){plan=next;status="Plan imported.";}return State();}
        object ChooseWorkingDirectory(Dictionary<string,object> p){EnsureNotBusy();string path=Text(p,"Path");if(String.IsNullOrWhiteSpace(path)||!Directory.Exists(path))throw new ArgumentException("Choose an existing working directory.");string full=Path.GetFullPath(path);SavePreferences(snapToGrid,full);lock(gate){workingDirectory=full;status="Working directory selected.";}return State();}

        void LoadSavedState()
        {
            string planPath=LastPlanPath;
            if(!File.Exists(planPath))planPath=Path.Combine(appDir,"plans","last-plan.json");
            if(File.Exists(planPath))try{if(new FileInfo(planPath).Length>4*1024*1024)throw new InvalidDataException("Saved plan exceeds 4 MB.");var loaded=json.Deserialize<AgentPlan>(File.ReadAllText(planPath));ValidateStructure(loaded);plan=loaded;}catch(Exception ex){invalidStores.Add(Path.GetFullPath(planPath));status="Saved plan unavailable; original file preserved. "+ex.Message;}
            try{profiles=ProfileLibrary.Read(ProfilePath,ValidateStructure);}catch(Exception ex){invalidStores.Add(Path.GetFullPath(ProfilePath));status="Profiles unavailable; original file preserved. "+ex.Message;}
            try{if(File.Exists(ServicePath)){if(new FileInfo(ServicePath).Length>1024*1024)throw new InvalidDataException("Service store exceeds 1 MB.");var loaded=json.Deserialize<List<ServiceConnection>>(File.ReadAllText(ServicePath))??new List<ServiceConnection>();if(loaded.Count>20||loaded.Any(c=>c==null||c.Id=="codex")||loaded.Select(c=>c.Id).Distinct().Count()!=loaded.Count)throw new InvalidDataException("Invalid service store.");foreach(var c in loaded)ApiServices.ValidateConnection(c);services=loaded;}}catch(Exception ex){invalidStores.Add(Path.GetFullPath(ServicePath));status="Services unavailable; original file preserved. "+ex.Message;}
            try{if(File.Exists(ModePath)){var d=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(ModePath));var value=d==null?null:Text(d,"Mode");if(value!="solo"&&value!="multi")throw new InvalidDataException("Invalid harness mode.");mode=value;}}catch(Exception ex){invalidStores.Add(Path.GetFullPath(ModePath));status="Harness mode unavailable; original file preserved. "+ex.Message;}
            try{if(File.Exists(PreferencesPath)){var d=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(PreferencesPath));if(d==null)throw new InvalidDataException("Invalid workspace preferences.");snapToGrid=Bool(d,"SnapToGrid",false);string folder=Text(d,"WorkingDirectory");if(!String.IsNullOrWhiteSpace(folder)&&Directory.Exists(folder))workingDirectory=Path.GetFullPath(folder);}}catch(Exception ex){invalidStores.Add(Path.GetFullPath(PreferencesPath));status="Workspace preferences unavailable; original file preserved. "+ex.Message;}
            if(!File.Exists(PreferencesPath)&&profiles.Selected!=null&&json.Serialize(plan)==json.Serialize(profiles.Selected.Plan))snapToGrid=profiles.Selected.SnapToGrid;
        }
        string ProfilePath{get{return Path.Combine(appDir,"profiles.json");}}
        string ServicePath{get{return Path.Combine(appDir,"services.json");}}
        string ModePath{get{return Path.Combine(appDir,"harness-mode.json");}}
        string PreferencesPath{get{return Path.Combine(appDir,"workspace-preferences.json");}}
        void SaveProfileLibrary(ProfileLibrary library){string data=library.Serialize();if(Encoding.UTF8.GetByteCount(data)>4*1024*1024)throw new InvalidOperationException("Profile library exceeds 4 MB.");AtomicSave(ProfilePath,data);}
        void SaveMode(string next){AtomicSave(ModePath,json.Serialize(new Dictionary<string,string>{{"Mode",next}}));}
        void SavePreferences(bool snap,string folder){AtomicSave(PreferencesPath,json.Serialize(new Dictionary<string,object>{{"SnapToGrid",snap},{"WorkingDirectory",folder}}));}
        void EnsureNotBusy(){lock(gate)if(busy||refreshing)throw new InvalidOperationException("Finish the current LAICA operation before changing workspace state.");}
        void EnsurePlanSize(string data){if(Encoding.UTF8.GetByteCount(data)>4*1024*1024)throw new InvalidOperationException("Plan exceeds 4 MB.");}
        string ReadFileOrNull(string path){return File.Exists(path)?File.ReadAllText(path):null;}
        void PruneModels(){models.RemoveAll(m=>m.ConnectionId!="codex"&&!services.Any(s=>s.Id==m.ConnectionId));}
        void SaveServiceList(List<ServiceConnection> next){string contents=json.Serialize(next);if(Encoding.UTF8.GetByteCount(contents)>1024*1024)throw new InvalidOperationException("Service store exceeds 1 MB.");AtomicSave(Path.Combine(appDir,"services.json"),contents);}
        string LastPlanPath{get{return Path.Combine(appDir,"plans","last-plan-v05.json");}}
        bool IsDirty(){var selected=profiles.Selected;return selected==null||json.Serialize(plan)!=json.Serialize(selected.Plan)||mode!=selected.Mode||snapToGrid!=selected.SnapToGrid;}
        string SelectedProfileName(){var p=profiles.Selected;return p==null?null:p.Name;}
        void AddRunEvent(string agent,string kind,string text){runEvents.Add(new ActivityEvent{TimeUtc=DateTime.UtcNow,AgentId=agent??"",Kind=kind??"progress",Text=Limit(text,4000)});if(runEvents.Count>300)runEvents.RemoveAt(0);}
        void AddRunStateEvent(string id,string state,string detail,AgentPlan snapshot)
        {
            bool stage=id=="$input"||id=="$review"||id=="$output";var node=stage?null:snapshot.Nodes.FirstOrDefault(n=>n.Id==id);string name=stage?(id=="$input"?"Task input":id=="$review"?"Supervisor review":"Final answer"):(node==null?id:node.Name);
            string activity=state=="done"?"completed":state=="running"?"started":state=="queued"?"queued":state=="cancelled"?"cancelled":state=="error"?"failed":state=="skipped"?"skipped":state;
            string text=stage?StageDescription(id,state):name+" "+activity+(String.IsNullOrWhiteSpace(detail)?".":": "+Limit(detail,700));
            AddRunEvent(id,state,text);
        }
        static string StageDescription(string id,string state)
        {
            if(id=="$input")return state=="done"?"Task received and ready for the team.":"Task input "+state+".";
            if(id=="$review")return state=="running"?"Supervisor is reviewing the team results.":state=="done"?"Supervisor review completed.":state=="cancelled"?"Supervisor review was cancelled.":state=="error"?"Supervisor review failed.":state=="skipped"?"Supervisor review was skipped.":"Supervisor review "+state+".";
            return state=="done"?"Final answer is ready.":state=="cancelled"?"Final answer generation was cancelled.":state=="error"?"Final answer generation failed.":"Final answer "+state+".";
        }
        void AddRunMessage(string message,AgentPlan snapshot)
        {
            string text=message??"";string agent="run",kind="activity";
            if(text.StartsWith("RESULT / ",StringComparison.Ordinal)){string[] parts=text.Split(new[]{" / "},3,StringSplitOptions.None);if(parts.Length>1){var node=snapshot.Nodes.FirstOrDefault(n=>n.Id==parts[1]||n.Name==parts[1]);if(node!=null)agent=node.Id;}kind="result";text="Team member result received.";}
            else if(text.IndexOf(" / API / ",StringComparison.Ordinal)>=0){kind="request";text="Sending a request to a local service.";}
            AddRunEvent(agent,kind,text);
        }
        void Publish(){var e=Changed;if(e!=null)try{e(State());}catch{}}
        static void ValidateStructure(AgentPlan p)
        {
            if(p==null||p.Version!=1||p.Nodes==null||p.Nodes.Count==0||p.Nodes.Count>100)throw new ArgumentException("Unsupported or empty LAICA plan.");var ids=new HashSet<string>(StringComparer.Ordinal);
            foreach(var n in p.Nodes)if(n==null||String.IsNullOrWhiteSpace(n.Id)||n.Id.StartsWith("$",StringComparison.Ordinal)||!ids.Add(n.Id)||String.IsNullOrWhiteSpace(n.Name)||n.Name.Length>80||String.IsNullOrWhiteSpace(n.Model)||n.Model.Length>256||String.IsNullOrWhiteSpace(n.Effort)||n.Effort.Length>32||String.IsNullOrWhiteSpace(n.Role)||n.Role.Length>80||(n.ConnectionId??"codex").Length>128||Single.IsNaN(n.X)||Single.IsNaN(n.Y)||Single.IsInfinity(n.X)||Single.IsInfinity(n.Y)||Math.Abs(n.X)>100000||Math.Abs(n.Y)>100000||(n.Job??"").Length>12000)throw new ArgumentException("Invalid agent in plan.");
            if((p.Goal??"").Length>16000)throw new ArgumentException("Goal is too long.");var root=p.Nodes.FirstOrDefault(n=>n.Id=="root");if(root==null||!String.IsNullOrEmpty(root.ParentId))throw new ArgumentException("A team needs one supervisor.");
            foreach(var n in p.Nodes){var seen=new HashSet<string>();var current=n;while(current!=null){if(!seen.Add(current.Id))throw new ArgumentException("Hierarchy has a loop.");if(String.IsNullOrEmpty(current.ParentId))break;current=p.Nodes.FirstOrDefault(a=>a.Id==current.ParentId);if(current==null)throw new ArgumentException("Missing parent agent.");}}
            if(p.Stages!=null){var stageIds=new HashSet<string>();foreach(var s in p.Stages)if(s==null||(s.Id!="$input"&&s.Id!="$review"&&s.Id!="$output")||!stageIds.Add(s.Id)||!Finite(s.X)||!Finite(s.Y)||Math.Abs(s.X)>100000||Math.Abs(s.Y)>100000)throw new ArgumentException("Invalid stage position.");}
            if(p.HiddenStageLinks!=null){if(p.HiddenStageLinks.Count>102)throw new ArgumentException("Too many hidden stage lines.");var routes=new HashSet<string>();foreach(var r in p.HiddenStageLinks)if(r==null||!((r.FromId=="$input"&&r.ToId=="root")||(ids.Contains(r.FromId??"")&&r.ToId=="$review")||(r.FromId=="$review"&&r.ToId=="$output"))||!routes.Add(r.FromId+"\n"+r.ToId))throw new ArgumentException("Invalid hidden stage line.");}
        }
        static bool Finite(float x){return !Single.IsNaN(x)&&!Single.IsInfinity(x);}
        AgentPlan ReadPlan(object value){if(value==null)throw new ArgumentException("Plan is required.");return value is string?json.Deserialize<AgentPlan>((string)value):json.Deserialize<AgentPlan>(json.Serialize(value));}
        static object Value(Dictionary<string,object>d,string k){object v;return d.TryGetValue(k,out v)?v:null;}
        static string Text(Dictionary<string,object>d,string k){var v=Value(d,k);return v==null?null:Convert.ToString(v);}
        static bool Bool(Dictionary<string,object>d,string k,bool fallback){var v=Value(d,k);return v==null?fallback:Convert.ToBoolean(v);}
        static string Limit(string s,int n){return s==null?"":s.Length<=n?s:s.Substring(0,n);}
        static object ModelDto(ModelInfo m){return new Dictionary<string,object>{{"Id",m.Id},{"Name",m.Name},{"ConnectionId",m.ConnectionId??"codex"},{"Efforts",m.Efforts??new List<string>()}};}
        static object ServiceDto(ServiceConnection s){return new Dictionary<string,object>{{"Id",s.Id},{"Name",s.Name},{"BaseUrl",s.BaseUrl},{"Provider",s.Provider},{"KeyEnvironmentVariable",s.KeyEnvironmentVariable??""},{"HasKey",!String.IsNullOrEmpty(s.ProtectedKey)}};}
        static object ProfileDto(TeamProfile p){return new Dictionary<string,object>{{"Id",p.Id},{"Name",p.Name},{"Mode",p.Mode},{"SnapToGrid",p.SnapToGrid},{"Plan",p.Plan}};}
        static object SessionDto(SessionChoice s){return new Dictionary<string,object>{{"Id",s.Id},{"Title",s.Title},{"Project",s.Project},{"UpdatedUtc",Iso(s.UpdatedUtc)}};}
        object TeamOverview(){
            if(overview!=null&&DateTime.UtcNow-overviewReadUtc<TimeSpan.FromSeconds(12))return overview;
            var rows=observer.ReadOverview(6).Select(t=>(object)new Dictionary<string,object>{{"Id",t.Session.Id},{"Title",t.Session.Title},{"Project",t.Session.Project},{"UpdatedUtc",Iso(t.Session.UpdatedUtc)},{"Task",t.Activity.Task},{"State",t.Activity.State},{"ObservedUtc",Iso(t.Activity.UpdatedUtc)},{"Action",t.Activity.Agents.FirstOrDefault(x=>x.Id==t.Activity.SessionId)==null?null:t.Activity.Agents.First(x=>x.Id==t.Activity.SessionId).Action}}).ToArray();
            overview=rows;overviewReadUtc=DateTime.UtcNow;return rows;
        }
        static object EventDto(ActivityEvent e){return new Dictionary<string,object>{{"TimeUtc",Iso(e.TimeUtc)},{"AgentId",e.AgentId??""},{"Kind",e.Kind},{"Text",e.Text??""},{"Path",e.Path??""},{"CallId",e.CallId},{"Tool",e.Tool},{"Command",e.Command},{"Purpose",e.Purpose},{"Outcome",e.Outcome},{"Detail",e.Detail},{"ExitCode",e.ExitCode}};}
        static object ActivityDto(ActivitySnapshot a){return new Dictionary<string,object>{{"SessionId",a.SessionId},{"Project",a.Project},{"Task",a.Task},{"State",a.State},{"Notice",a.Notice},{"UpdatedUtc",Iso(a.UpdatedUtc)},{"Agents",a.Agents.Select(x=>(object)new Dictionary<string,object>{{"Id",x.Id},{"ParentId",x.ParentId},{"Name",x.Name},{"Role",x.Role},{"Model",x.Model},{"Effort",x.Effort},{"State",x.State},{"Action",x.Action},{"Assignment",x.Assignment},{"StartedUtc",Iso(x.StartedUtc)},{"UpdatedUtc",Iso(x.UpdatedUtc)},{"Files",x.Files.Select(f=>(object)new Dictionary<string,object>{{"Path",f.Path},{"Kind",f.Kind}}).ToArray()},{"RecentEvents",x.RecentEvents.Select(EventDto).ToArray()}}).ToArray()},{"Events",a.Events.Select(EventDto).ToArray()}};}
        static string Iso(DateTime d){return d.ToUniversalTime().ToString("o",System.Globalization.CultureInfo.InvariantCulture);}
        static ServiceConnection CloneService(ServiceConnection c){return new ServiceConnection{Id=c.Id,Name=c.Name,BaseUrl=c.BaseUrl,Provider=c.Provider,KeyEnvironmentVariable=c.KeyEnvironmentVariable,ProtectedKey=c.ProtectedKey};}
        static ProfileLibrary CloneProfiles(ProfileLibrary p){var clone=new JavaScriptSerializer{MaxJsonLength=4*1024*1024}.Deserialize<ProfileLibrary>(p.Serialize());return clone;}
        void AtomicSave(string path,string contents){string full=Path.GetFullPath(path);if(invalidStores.Contains(full))throw new InvalidOperationException("This settings file is invalid and was preserved; repair or remove it before saving.");Directory.CreateDirectory(Path.GetDirectoryName(full));string tmp=full+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllText(tmp,contents,new UTF8Encoding(false));if(File.Exists(full))File.Replace(tmp,full,null);else File.Move(tmp,full);}finally{if(File.Exists(tmp))File.Delete(tmp);}}
        public void Dispose(){lock(gate){if(disposed)return;disposed=true;if(runToken!=null)runToken.Cancel();}observer.Dispose();}
    }
}
