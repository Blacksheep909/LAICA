using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Laica
{
    public class ActivitySnapshot
    {
        public string SessionId, Project, Task, State, Notice;
        public DateTime UpdatedUtc;
        public List<ActivityAgent> Agents = new List<ActivityAgent>();
        public List<ActivityEvent> Events = new List<ActivityEvent>();
    }
    public class ActivityAgent
    {
        public string Id, ParentId, Name, Role, Model, Effort, RequestedModel, RequestedEffort, State, Action, Assignment;
        public DateTime StartedUtc, UpdatedUtc;
        public List<ActivityFile> Files = new List<ActivityFile>();
        public List<ActivityEvent> RecentEvents = new List<ActivityEvent>();
    }
    public class ActivityFile { public string Path, Kind; }
    public class ActivityEvent
    {
        public DateTime TimeUtc;
        public string AgentId, Kind, Text, Path;
        public string CallId, Tool, Command, Purpose, Outcome, Detail;
        public int? ExitCode;
    }
    public class SessionChoice
    {
        public string Id, Title, Project;
        public DateTime UpdatedUtc;
        public override string ToString() { return (Title ?? "Codex session") + (String.IsNullOrEmpty(Project) ? "" : " — " + Project) + (UpdatedUtc==DateTime.MinValue?"":" · "+UpdatedUtc.ToLocalTime().ToString("dd MMM HH:mm")); }
    }
    public sealed class TeamObservation { public SessionChoice Session; public ActivitySnapshot Activity; }

    // Best-effort local JSONL observation. Recorded text and commands are data only and are never executed.
    public sealed class CodexActivityObserver : IDisposable
    {
        const int MaxFiles = 400, MaxLine = 1024 * 1024, MaxEvents = 120, MaxFilesPerAgent = 80, InitialTailBytes = 2 * 1024 * 1024, RecoveryBytes = 32 * 1024 * 1024, RecoveryChunk = 1024 * 1024;
        readonly string home, sessionsRoot;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024, RecursionLimit = 40 };
        readonly Dictionary<string, Tail> tails = new Dictionary<string, Tail>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, ActivitySnapshot> snapshots = new Dictionary<string, ActivitySnapshot>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> recovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, PendingAssignment> pending = new Dictionary<string, PendingAssignment>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, PendingAssignment> assignments = new Dictionary<string, PendingAssignment>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Dictionary<string, ToolInvocation>> toolCalls = new Dictionary<string, Dictionary<string, ToolInvocation>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Queue<string>> toolCallOrder = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);
        readonly object gate = new object();
        bool disposed;
        const string NoticeText = "Local session-log monitoring is best effort; it is not an official live subscription.";
        sealed class Tail { public long Offset; public string Partial = ""; public Decoder Decoder=Encoding.UTF8.GetDecoder(); }
        sealed class PendingAssignment { public string TaskName, Role, Model, Effort, Text; }
        sealed class ToolInvocation { public ActivitySnapshot Snapshot; public ActivityAgent Agent; public ActivityEvent Event; }

        public CodexActivityObserver(string codexHome)
        {
            if (String.IsNullOrWhiteSpace(codexHome)) throw new ArgumentException("A Codex home directory is required.", "codexHome");
            home = Path.GetFullPath(codexHome);
            sessionsRoot = Path.Combine(home, "sessions");
        }
        public List<SessionChoice> ListSessions()
        {
            lock (gate) { EnsureOpen(); var titles=ReadSessionTitles(); return Enumerate().Select(f=>ReadChoice(f,titles)).Where(x => x != null).OrderByDescending(x => x.UpdatedUtc).Take(100).ToList(); }
        }
        public ActivitySnapshot Read(string rootSessionId)
        {
            if (String.IsNullOrWhiteSpace(rootSessionId)) throw new ArgumentException("Session id required.", "rootSessionId");
            lock (gate) { EnsureOpen(); return ReadCore(rootSessionId,Enumerate(),true); }
        }
        public List<TeamObservation> ReadOverview(int limit)
        {
            limit=Math.Max(1,Math.Min(8,limit));
            lock(gate)
            {
                EnsureOpen();var files=Enumerate();var titles=ReadSessionTitles();
                var roots=files.Select(f=>new{File=f,Choice=ReadChoice(f,titles)}).Where(x=>x.Choice!=null).OrderByDescending(x=>x.Choice.UpdatedUtc).Take(limit).ToList();
                var result=new List<TeamObservation>(roots.Count);
                foreach(var root in roots)result.Add(new TeamObservation{Session=root.Choice,Activity=ReadCore(root.Choice.Id,files,false)});
                return result;
            }
        }
        ActivitySnapshot ReadCore(string rootSessionId,List<string> files,bool includeChildren)
        {
                
                var rootFile = files.FirstOrDefault(f => String.Equals(Text(ReadMetadata(f),"id")??Text(ReadMetadata(f),"session_id"), rootSessionId, StringComparison.OrdinalIgnoreCase) || String.Equals(Path.GetFileNameWithoutExtension(f), rootSessionId, StringComparison.OrdinalIgnoreCase));
                if (rootFile == null) return new ActivitySnapshot { SessionId=rootSessionId, State="unavailable", Notice=NoticeText };
                var rootMeta=ReadMetadata(rootFile);
                ActivitySnapshot snap;
                DateTime sessionStart=rootMeta==null?File.GetLastWriteTimeUtc(rootFile):Date(rootMeta,"timestamp",File.GetLastWriteTimeUtc(rootFile));
                if(!snapshots.TryGetValue(rootSessionId,out snap)) { snap = new ActivitySnapshot { SessionId=rootSessionId, State="idle", Notice=NoticeText, UpdatedUtc=File.GetLastWriteTimeUtc(rootFile) }; snapshots[rootSessionId]=snap; }
                if(snap.Agents.Count==0) snap.Agents.Add(new ActivityAgent { Id=rootSessionId, Name="Session", State="idle", StartedUtc=sessionStart, UpdatedUtc=sessionStart });
                var agent=snap.Agents[0];
                if(rootMeta!=null){agent.Name=SafeProject(Text(rootMeta,"cwd"))??"Session";snap.Project=SafeProject(Text(rootMeta,"cwd"));}
                RecoverOnce(rootFile,snap,agent);
                var lines = TailFile(rootFile);
                foreach (string line in lines) ParseLine(line, snap, agent,File.GetLastWriteTimeUtc(rootFile));
                agent.Role="supervisor";agent.Name=agent.Model=="gpt-6.1-sol"?"Sol":"Codex";
                if(includeChildren)for(int depth=1;depth<=2;depth++)
                foreach (string childFile in files)
                {
                    if (String.Equals(childFile,rootFile,StringComparison.OrdinalIgnoreCase)) continue;
                    Dictionary<string,object> meta=ReadMetadata(childFile); if(meta==null)continue;
                    Dictionary<string,object> source=Get(meta,"source") as Dictionary<string,object>;
                    Dictionary<string,object> sub=Get(source,"subagent") as Dictionary<string,object>;
                    Dictionary<string,object> spawn=Get(sub,"thread_spawn") as Dictionary<string,object>;
                    if(spawn==null || !snap.Agents.Any(x=>String.Equals(x.Id,Text(spawn,"parent_thread_id"),StringComparison.OrdinalIgnoreCase)))continue;
                    string childId=Text(meta,"id")??Text(meta,"session_id"); if(String.IsNullOrEmpty(childId))continue;
                    var child=snap.Agents.FirstOrDefault(x=>x.Id==childId);
                    if(child==null){if(snap.Agents.Count>=101)continue;string parent=Text(spawn,"parent_thread_id");PendingAssignment assign=FindAssignment(childId,Text(spawn,"agent_path")??Text(spawn,"agent_nickname"),Text(spawn,"agent_role"));DateTime childStart=Date(meta,"timestamp",File.GetLastWriteTimeUtc(childFile));child=new ActivityAgent {Id=childId,ParentId=parent,Name=Text(spawn,"agent_nickname")??(assign==null?"Subagent":assign.TaskName),Role=Text(spawn,"agent_role")??(assign==null?null:assign.Role),RequestedModel=assign==null?null:assign.Model,RequestedEffort=assign==null?null:assign.Effort,Assignment=assign==null?null:assign.Text,StartedUtc=childStart,UpdatedUtc=childStart,State="idle"};snap.Agents.Add(child);}
                    RecoverOnce(childFile,snap,child);
                    foreach(string childLine in TailFile(childFile)) ParseLine(childLine,snap,child,File.GetLastWriteTimeUtc(childFile));
                    if(DateTime.UtcNow-child.UpdatedUtc>TimeSpan.FromSeconds(60) && child.State=="running")child.State="stale";
                }
                foreach(var known in snap.Agents)if(known.State=="running" && DateTime.UtcNow-known.UpdatedUtc>TimeSpan.FromSeconds(60))known.State="stale";
                snap.State=agent.State;
                if(snap.State=="running" && !snap.Agents.Any(x=>x.State=="running" && DateTime.UtcNow-x.UpdatedUtc<TimeSpan.FromSeconds(60)))snap.State="stale";
                snap.Events = TakeLastCompat(snap.Events.OrderBy(e=>e.TimeUtc),MaxEvents);
                return Clone(snap);
        }
        SessionChoice ReadChoice(string file,Dictionary<string,string> titles)
        {
            try
            {
                using (var fs = Open(file)) using (var sr = new StreamReader(fs, Encoding.UTF8, true, 4096))
                {
                    string line = sr.ReadLine(); if (line == null || line.Length > MaxLine) return null;
                    var env = Parse(line); var p = Get(env,"payload") as Dictionary<string,object>;
                    if (Text(env,"type")!="session_meta" || p==null) return null;
                    if (Get(Get(p,"source") as Dictionary<string,object>,"subagent")!=null) return null;
                    string id=Text(p,"id") ?? Text(p,"session_id"); string cwd=Text(p,"cwd");
                    DateTime dt=File.GetLastWriteTimeUtc(file);
                    string title;titles.TryGetValue(id??"",out title);title=CleanSessionTitle(title);
                    return String.IsNullOrEmpty(id)?null:new SessionChoice { Id=id, Project=SafeProject(cwd), Title=String.IsNullOrEmpty(title)?"Untitled chat · …"+id.Substring(Math.Max(0,id.Length-12)):title, UpdatedUtc=dt };
                }
            } catch { return null; }
        }
        Dictionary<string,object> ReadMetadata(string file)
        {
            try { using(var fs=Open(file))using(var sr=new StreamReader(fs,Encoding.UTF8,true,4096)){string line=sr.ReadLine();if(line==null||line.Length>MaxLine)return null;var env=Parse(line);if(Text(env,"type")!="session_meta")return null;return Get(env,"payload") as Dictionary<string,object>;} } catch{return null;}
        }
        Dictionary<string,string> ReadSessionTitles(){
            var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            string file=Path.Combine(home,"session_index.jsonl");if(!File.Exists(file))return result;
            try{
                using(var fs=Open(file)){
                    long end=fs.Length,start=Math.Max(0,end-8L*1024*1024);fs.Position=start;
                    if(start>0){int value;while(fs.Position<end&&(value=fs.ReadByte())>=0&&value!='\n'){} }
                    int length=(int)Math.Max(0,end-fs.Position);byte[] bytes=new byte[length];int count=0,read;
                    while(count<length&&(read=fs.Read(bytes,count,length-count))>0)count+=read;
                    var recent=new Queue<string>();
                    using(var reader=new StringReader(Encoding.UTF8.GetString(bytes,0,count))){string line;while((line=reader.ReadLine())!=null){if(line.Length==0||line.Length>MaxLine)continue;recent.Enqueue(line);if(recent.Count>20000)recent.Dequeue();}}
                    foreach(string line in recent){try{var entry=Parse(line);string id=Text(entry,"id")??Text(entry,"thread_id")??Text(entry,"session_id");string title=CleanSessionTitle(Text(entry,"thread_name"));if(!String.IsNullOrEmpty(id)&&!String.IsNullOrEmpty(title))result[id]=title;}catch{}}
                }
            }catch{}
            return result;
        }
        static string CleanSessionTitle(string title){if(String.IsNullOrWhiteSpace(title))return null;var b=new StringBuilder();foreach(char c in title){if(Char.IsControl(c)){if(Char.IsWhiteSpace(c))b.Append(' ');continue;}b.Append(c);if(b.Length>=240)break;}string value=Regex.Replace(b.ToString(),@"\s+"," ").Trim();return value.Length==0?null:Limit(value,240);}
        string SessionId(string file) { var c=ReadChoice(file,new Dictionary<string,string>()); return c==null?null:c.Id; }
        List<string> Enumerate()
        {
            if (!Directory.Exists(sessionsRoot)) return new List<string>();
            try {
                var recent=new List<KeyValuePair<string,DateTime>>();DateTime cutoff=DateTime.UtcNow.AddDays(-45);
                foreach(string f in Directory.EnumerateFiles(sessionsRoot,"*.jsonl",SearchOption.AllDirectories)){
                    DateTime time;try{time=File.GetLastWriteTimeUtc(f);}catch{continue;}if(time<=cutoff)continue;
                    if(recent.Count==MaxFiles&&time<=recent[recent.Count-1].Value)continue;
                    recent.Add(new KeyValuePair<string,DateTime>(f,time));recent.Sort((a,b)=>b.Value.CompareTo(a.Value));
                    if(recent.Count>MaxFiles)recent.RemoveAt(recent.Count-1);
                }
                return recent.Select(x=>x.Key).ToList();
            } catch { return new List<string>(); }
        }
        static FileStream Open(string f) { return new FileStream(f,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete); }
        List<string> TailFile(string file)
        {
            var result=new List<string>(); Tail t; string key=Path.GetFullPath(file);
            if(!tails.TryGetValue(key,out t)){t=new Tail();tails[key]=t;}
            try
            {
                using(var fs=Open(file))
                {
                    if(fs.Length<t.Offset){t.Offset=0;t.Partial="";t.Decoder=Encoding.UTF8.GetDecoder();}
                    if(t.Offset==0 && fs.Length>InitialTailBytes){fs.Position=fs.Length-InitialTailBytes; int b; while((b=fs.ReadByte())>=0 && b!='\n'){} t.Offset=fs.Position;}
                    fs.Position=t.Offset;
                    {
                        int size=(int)Math.Min(InitialTailBytes,Math.Max(0,fs.Length-fs.Position));
                        byte[] bytes=new byte[size];int count=0,n;
                        while(count<size && (n=fs.Read(bytes,count,size-count))>0)count+=n;
                        t.Offset=fs.Position;
                        char[] characters=new char[Encoding.UTF8.GetMaxCharCount(count)];
                        int length=t.Decoder.GetChars(bytes,0,count,characters,0,false);
                        string chunk=new string(characters,0,length);
                        string all=t.Partial+chunk; int start=0,ix;
                        while((ix=all.IndexOf('\n',start))>=0){string line=all.Substring(start,ix-start).TrimEnd('\r'); if(line.Length<=MaxLine)result.Add(line); start=ix+1;}
                        t.Partial=start<all.Length?all.Substring(start):"";
                        if(t.Partial.Length>MaxLine)t.Partial="";
                    }
                }
            } catch { }
            return result;
        }
        void RecoverOnce(string file,ActivitySnapshot s,ActivityAgent a)
        {
            string key=Path.GetFullPath(file);if(!recovered.Add(key))return;
            DateTime fileTime=File.GetLastWriteTimeUtc(file), latestLifecycle=DateTime.MinValue;bool gotContext=false,gotLifecycle=false,gotStart=false;
            try
            {
                using(var fs=Open(file))
                {
                    long floor=Math.Max(0,fs.Length-RecoveryBytes),pos=fs.Length;string carry="";bool first=true;byte[] bytes=new byte[RecoveryChunk];
                    while(pos>floor)
                    {
                        int n=(int)Math.Min(bytes.Length,pos-floor);pos-=n;fs.Position=pos;int read=fs.Read(bytes,0,n);if(read<=0)break;
                        string combined=Encoding.UTF8.GetString(bytes,0,read)+carry;int cursor=combined.Length;
                        if(first){int end=combined.LastIndexOf('\n');cursor=end<0?0:end+1;first=false;}
                        while(cursor>0)
                        {
                            int nl=combined.LastIndexOf('\n',cursor-1);if(nl<0){carry=combined.Substring(0,cursor);break;}
                            string line=combined.Substring(nl+1,cursor-nl-1).TrimEnd('\r');if(line.Length<=MaxLine)RecoverRecord(line,s,a,fileTime,ref gotContext,ref gotLifecycle,ref gotStart,ref latestLifecycle);
                            cursor=nl;carry="";
                            if(gotContext&&gotLifecycle&&gotStart){pos=floor;carry="";break;}
                        }
                    }
                    if(floor==0&&carry.Length>0&&carry.Length<=MaxLine)RecoverRecord(carry,s,a,fileTime,ref gotContext,ref gotLifecycle,ref gotStart,ref latestLifecycle);
                }
            }catch{}
        }
        void RecoverRecord(string line,ActivitySnapshot s,ActivityAgent a,DateTime fileTime,ref bool gotContext,ref bool gotLifecycle,ref bool gotStart,ref DateTime latestLifecycle)
        {
            Dictionary<string,object> env;try{env=Parse(line);}catch{return;}
            string type=Text(env,"type");if(type!="turn_context"&&type!="event_msg")return;
            var p=Get(env,"payload") as Dictionary<string,object>;if(p==null)return;
            DateTime fallback=type=="turn_context"?DateTime.MinValue:fileTime;DateTime time=Date(env,"timestamp",Date(p,"timestamp",fallback));if(time==DateTime.MinValue)time=a.UpdatedUtc;
            if(type=="turn_context")
            {
                if(!gotContext){a.Model=Text(p,"model");a.Effort=Text(p,"effort");Touch(s,a,time);gotContext=true;}return;
            }
            string kind=Text(p,"type");if(kind!="task_started"&&kind!="task_complete"&&kind!="turn_aborted")return;
            Touch(s,a,time);
            if(kind=="task_started"&&!gotStart){a.StartedUtc=time;gotStart=true;}
            if(!gotLifecycle){a.State=kind=="task_started"?"running":kind=="task_complete"?"complete":"error";if(a.ParentId==null)s.State=a.State;latestLifecycle=time;gotLifecycle=true;}
        }
        void ParseLine(string line,ActivitySnapshot s,ActivityAgent a,DateTime fileTime)
        {
            Dictionary<string,object> env; try{env=Parse(line);}catch{return;}
            string typ=Text(env,"type"); var p=Get(env,"payload") as Dictionary<string,object>; if(p==null)return;
            if(typ!="turn_context" && typ!="event_msg" && typ!="response_item")return;
            DateTime fallback=typ=="turn_context"?a.UpdatedUtc:fileTime;DateTime tm=Date(env,"timestamp",Date(p,"timestamp",fallback));
            string sub=Text(p,"type"); string text=null,kind=null;
            if(typ=="turn_context") { a.Model=Text(p,"model"); a.Effort=Text(p,"effort"); Touch(s,a,tm);return; }
            if(typ=="event_msg")
            {
                if(sub=="task_started"){a.State="running";a.StartedUtc=tm;if(a.ParentId==null)s.State="running";kind="started";text=SafeLabel(sub);}
                else if(sub=="task_complete"){a.State="complete";if(a.ParentId==null)s.State="complete";kind="complete";text=CompletionSummary(s,a);}
                else if(sub=="turn_aborted"){a.State="error";if(a.ParentId==null)s.State="error";kind="aborted";text=SafeLabel(sub);}
                else if(sub=="item_completed")ParseCompletedItem(Get(p,"item") as Dictionary<string,object>,s,a,tm);
            }
            else if(typ=="response_item")
            {
                string role=Text(p,"role"); var content=Get(p,"content") as System.Collections.IEnumerable;
                bool recognized=false;
                if(role=="user" && a.ParentId==null && content!=null)foreach(var item in content){var d=item as Dictionary<string,object>;string ct=Text(d,"type"),x=Text(d,"text");if((ct=="input_text"||ct=="text")&&!String.IsNullOrWhiteSpace(x)){int start=x.IndexOf("## My request:",StringComparison.OrdinalIgnoreCase);if(start>=0)x=x.Substring(start+14);s.Task=Limit(RedactSensitive(FirstLine(x.Trim())),240);recognized=true;break;}}
                if(role=="assistant" && content!=null) foreach(var item in content) { var d=item as Dictionary<string,object>; if(d==null)continue; string ct=Text(d,"type"); if(ct=="output_text" || ct=="text") { string x=Text(d,"text"); if(IsPublicMessage(p,x)){kind=Text(p,"phase")=="final"?"final":"commentary";text=Limit(RedactSensitive(x),240);a.Action=text;recognized=true;break;} } }
                string it=Text(p,"type"); if(it=="function_call"||it=="custom_tool_call") { var toolEvent=RecordToolCall(p,s,a,tm);if(toolEvent!=null){kind=toolEvent.Kind;text=toolEvent.Text;a.Action=text;a.State="running";if(a.ParentId==null)s.State="running";AddFiles(s,a,p,tm);if(a.ParentId==null)TrackAssignment(p);recognized=true;} }
                if(it=="function_call_output"||it=="custom_tool_call_output"){RecordToolOutput(p,s,a,tm);recognized=true;}
                if(recognized)Touch(s,a,tm);
            }
            if(kind!=null) {
                Touch(s,a,tm);
                if(a.State=="stale"&&DateTime.UtcNow-tm<TimeSpan.FromSeconds(60)){a.State="running";if(a.ParentId==null)s.State="running";}
                AddEvent(s,new ActivityEvent {TimeUtc=tm,AgentId=a.Id,Kind=kind,Text=Limit(RedactSensitive(text),240)});
            }
        }
        ActivityEvent RecordToolCall(Dictionary<string,object> p,ActivitySnapshot s,ActivityAgent a,DateTime tm)
        {
            Dictionary<string,object> args=ToolArguments(p);string name=Text(p,"name")??Text(p,"tool_name")??"tool";
            string command=ExtractRecordedCommand(p,args);string callId=SafeField(Text(p,"call_id")??Text(p,"id"),128);
            string server=Text(p,"server")??Text(p,"server_name"),mcpTool=Text(p,"tool")??Text(p,"tool_name");
            bool mcp=!String.IsNullOrEmpty(server)||!String.IsNullOrEmpty(mcpTool)||name.IndexOf("mcp__",StringComparison.OrdinalIgnoreCase)==0;
            string tool=mcp?(String.IsNullOrEmpty(server)||String.IsNullOrEmpty(mcpTool)?(mcpTool??name):server+"."+mcpTool):(!String.IsNullOrEmpty(command)?"exec_command":name);
            if(String.IsNullOrWhiteSpace(command)&&name.IndexOf("exec_command",StringComparison.OrdinalIgnoreCase)>=0)tool="exec_command";
            string purpose=ExplicitPurpose(p,args)??ExtractRawPurpose(p);
            bool commandCall=!String.IsNullOrWhiteSpace(command)||tool=="exec_command";
            var e=new ActivityEvent{TimeUtc=tm,AgentId=a.Id,Kind=commandCall?"command_started":"tool_started",CallId=callId,Tool=SafeField(RedactSensitive(tool),160),Command=command==null?null:BoundedRecorded(command,8000),Purpose=purpose==null?null:BoundedRecorded(purpose,256),Outcome="Running",Text=commandCall?(command==null?"Command started; command text not recorded":"Command started: "+Limit(RedactSensitive(command),150)):"Tool started: "+Limit(RedactSensitive(tool),150)};
            e.Text=Limit(RedactSensitive(e.Text),240);AddEvent(s,e);
            if(!String.IsNullOrEmpty(callId))RememberToolCall(a.Id,callId,new ToolInvocation{Snapshot=s,Agent=a,Event=e});
            return e;
        }
        void RecordToolOutput(Dictionary<string,object> p,ActivitySnapshot s,ActivityAgent a,DateTime tm)
        {
            string callId=SafeField(Text(p,"call_id")??Text(p,"id"),128);object output=Get(p,"output")??Get(p,"result")??Get(p,"content");
            ToolInvocation context=FindToolCall(a.Id,callId);ActivityEvent e=context==null?null:context.Event;
            if(e==null){string tool=Text(p,"server")??Text(p,"server_name");string mcp=Text(p,"tool")??Text(p,"tool_name")??Text(p,"name");if(!String.IsNullOrEmpty(tool)&&!String.IsNullOrEmpty(mcp))tool+="."+mcp;else tool=mcp; e=new ActivityEvent{TimeUtc=tm,AgentId=a.Id,CallId=callId,Tool=SafeField(RedactSensitive(tool),160)};}
            ApplyToolOutcome(e,output,p);e.TimeUtc=tm;
            bool failed=IsFailedOutcome(e.Outcome);e.Kind=e.Tool!=null&&e.Tool.StartsWith("exec_command",StringComparison.OrdinalIgnoreCase)?(failed?"command_failed":"command_complete"):(failed?"tool_failed":"tool_complete");
            e.Text=EventOutcomeText(e);if(context==null)AddEvent(s,e);else SyncRecentEvent(context.Snapshot,context.Agent,e);
            a.Action=Limit(e.Text,240);Touch(s,a,tm);
        }
        void RememberToolCall(string agentId,string callId,ToolInvocation invocation)
        {
            Dictionary<string,ToolInvocation> byId;Queue<string> order;
            if(!toolCalls.TryGetValue(agentId,out byId)){byId=new Dictionary<string,ToolInvocation>(StringComparer.Ordinal);toolCalls[agentId]=byId;order=new Queue<string>();toolCallOrder[agentId]=order;}else order=toolCallOrder[agentId];
            byId[callId]=invocation;order.Enqueue(callId);
            while(byId.Count>128&&order.Count>0){string oldest=order.Dequeue();if(oldest!=callId)byId.Remove(oldest);}
        }
        ToolInvocation FindToolCall(string agentId,string callId)
        { Dictionary<string,ToolInvocation> byId;ToolInvocation invocation;return !String.IsNullOrEmpty(callId)&&toolCalls.TryGetValue(agentId,out byId)&&byId.TryGetValue(callId,out invocation)?invocation:null; }
        Dictionary<string,object> ToolArguments(Dictionary<string,object> p)
        {
            object raw=Get(p,"arguments")??Get(p,"input");var args=raw as Dictionary<string,object>;if(args!=null)return args;
            var text=raw as string;if(!String.IsNullOrEmpty(text)){try{return Parse(text);}catch{}}
            return null;
        }
        static string ExplicitPurpose(Dictionary<string,object> p,Dictionary<string,object> args)
        {return SafeField(RedactSensitive(Text(p,"justification")??Text(p,"title")??Text(args,"justification")??Text(args,"title")),256);}
        string ExtractRawPurpose(Dictionary<string,object> p)
        {
            string raw=Text(p,"arguments")??Text(p,"input");if(String.IsNullOrEmpty(raw))return null;
            Match match=Regex.Match(raw,@"tools\.exec_command\s*\(\s*\{[\s\S]{0,2000}?\b(?:justification|title)\s*:\s*(""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')",RegexOptions.IgnoreCase);
            if(!match.Success)return null;string literal=match.Groups[1].Value,value;
            try{value=literal.StartsWith("\"",StringComparison.Ordinal)?new JavaScriptSerializer{MaxJsonLength=MaxLine}.Deserialize<string>(literal):Regex.Unescape(literal.Substring(1,literal.Length-2));}catch{return null;}
            return SafeField(RedactSensitive(value),256);
        }
        string ExtractRecordedCommand(Dictionary<string,object> p,Dictionary<string,object> args)
        {
            string command=Text(p,"cmd")??Text(p,"command")??Text(args,"cmd")??Text(args,"command");
            if(!String.IsNullOrWhiteSpace(command))return RedactSensitive(command);
            string raw=Text(p,"arguments")??Text(p,"input");if(String.IsNullOrEmpty(raw))return null;
            var matches=Regex.Matches(raw,@"tools\.exec_command\s*\(\s*\{[\s\S]{0,2000}?\b(?:cmd|command)\s*:\s*(""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')",RegexOptions.IgnoreCase);
            if(matches.Count==0)return null;
            var commands=new List<string>();
            foreach(Match match in matches){string literal=match.Groups[1].Value;try{command=literal.StartsWith("\"",StringComparison.Ordinal)?new JavaScriptSerializer{MaxJsonLength=MaxLine}.Deserialize<string>(literal):Regex.Unescape(literal.Substring(1,literal.Length-2));}catch{continue;}if(!String.IsNullOrWhiteSpace(command))commands.Add(RedactSensitive(command));}
            return commands.Count==0?null:String.Join("\n[batch command]\n",commands);
        }
        void ApplyToolOutcome(ActivityEvent e,object output,Dictionary<string,object> envelope)
        {
            object combined=output??(object)envelope;int? code=FindExitCode(combined);if(!code.HasValue)code=FindExitCode(envelope);e.ExitCode=code;
            string status=FindStatus(combined)??FindStatus(envelope);bool error=FindErrorFlag(combined)||FindErrorFlag(envelope);
            if(code.HasValue&&code.Value!=0)e.Outcome="Exited with code "+code.Value;
            else if(error||IsFailureStatus(status))e.Outcome=error?"Tool reported an error":SafeField(RedactSensitive(status),128);
            else if(code.HasValue)e.Outcome="Exited with code 0";
            else if(!String.IsNullOrWhiteSpace(status))e.Outcome=SafeField(RedactSensitive(status),128);
            else e.Outcome=output==null?"Completed; result not recorded":"Completed; exit status not recorded";
            object detail=FindOutput(combined);string text=detail==null?null:Convert.ToString(detail,CultureInfo.InvariantCulture);
            e.Detail=String.IsNullOrEmpty(text)?null:BoundedRecorded(RedactSensitive(text),12000);
        }
        static bool IsFailedOutcome(string outcome)
        {return (!String.IsNullOrEmpty(outcome)&&Regex.IsMatch(outcome,@"\b(failed|error|cancelled|canceled)\b",RegexOptions.IgnoreCase))||Regex.IsMatch(outcome??"",@"Exited with code (?!0\b)-?\d+",RegexOptions.IgnoreCase);}
        static bool IsFailureStatus(string status)
        {return !String.IsNullOrWhiteSpace(status)&&Regex.IsMatch(status,@"\b(failed|failure|error|cancelled|canceled|aborted)\b",RegexOptions.IgnoreCase);}
        static string EventOutcomeText(ActivityEvent e)
        {
            bool command=e.Tool!=null&&e.Tool.IndexOf("exec_command",StringComparison.OrdinalIgnoreCase)>=0;
            bool failed=IsFailedOutcome(e.Outcome);
            if(command){string head=e.Command==null?"Command finished; command text not recorded":"Command "+(failed?"failed":"finished")+": "+Limit(e.Command,160);return Limit(RedactSensitive(head+" ("+e.Outcome+")"),240);}
            return Limit(RedactSensitive("Tool "+(failed?"failed":"finished")+": "+(e.Tool??"tool")+" ("+(e.Outcome??"outcome not recorded")+")"),240);
        }
        void SyncRecentEvent(ActivitySnapshot snapshot,ActivityAgent agent,ActivityEvent source)
        {
            foreach(var copy in agent.RecentEvents.Where(x=>x.CallId==source.CallId&&source.CallId!=null))CopyEventFields(source,copy);
            EnforceRichDetailBudget(snapshot);
        }
        void UpdateCallFromCompletedItem(Dictionary<string,object> item,ActivitySnapshot s,ActivityAgent a,DateTime tm,bool command)
        {
            string callId=SafeField(Text(item,"call_id")??Text(item,"id"),128);ToolInvocation context=FindToolCall(a.Id,callId);ActivityEvent e=context==null?null:context.Event;
            string recorded=ExtractRecordedCommand(item,Get(item,"arguments") as Dictionary<string,object>);
            if(e==null)e=new ActivityEvent{TimeUtc=tm,AgentId=a.Id,CallId=callId};
            if(!String.IsNullOrEmpty(recorded))e.Command=BoundedRecorded(recorded,8000);
            if(command&&String.IsNullOrEmpty(e.Command)){e.Text="Command finished; command text not recorded";e.Tool="exec_command";}
            else if(command)e.Tool="exec_command";
            if(!command){string server=Text(item,"server")??Text(item,"server_name"),tool=Text(item,"tool")??Text(item,"tool_name");if(!String.IsNullOrEmpty(server)&&!String.IsNullOrEmpty(tool))e.Tool=SafeField(RedactSensitive(server+"."+tool),160);}
            ApplyToolOutcome(e,Get(item,"result")??Get(item,"output")??(object)item,item);e.TimeUtc=tm;
            bool failed=IsFailedOutcome(e.Outcome);e.Kind=command?(failed?"command_failed":"command_complete"):(failed?"tool_failed":"tool_complete");
            e.Text=command&&String.IsNullOrEmpty(e.Command)?"Command finished; command text not recorded":command?EventOutcomeText(e):"Tool "+(failed?"failed":"completed")+" · "+(e.Tool??"tool");
            if(context==null)AddEvent(s,e);else SyncRecentEvent(context.Snapshot,context.Agent,e);
            a.Action=Limit(RedactSensitive(e.Text),240);
        }
        static int? FindExitCode(object value)
        {
            var codes=new List<int>();CollectExitCodes(value,codes,0);int nonzero=codes.FirstOrDefault(x=>x!=0);if(nonzero!=0)return nonzero;return codes.Count==0?(int?)null:codes[0];
        }
        static void CollectExitCodes(object value,List<int> found,int depth)
        {
            if(value==null||depth>12)return;var d=value as Dictionary<string,object>;
            if(d!=null){foreach(string key in new[]{"exit_code","exitCode"}){int code;object raw=Get(d,key);if(raw!=null&&Int32.TryParse(Convert.ToString(raw,CultureInfo.InvariantCulture),out code))found.Add(code);}foreach(var pair in d)if(pair.Key!="exit_code"&&pair.Key!="exitCode")CollectExitCodes(pair.Value,found,depth+1);return;}
            var items=value as System.Collections.IEnumerable;
            if(items!=null&&!(value is string)){foreach(object item in items)CollectExitCodes(item,found,depth+1);return;}
            if(value is string){var text=(string)value;if(text.Length>0&&text[0]=='{')try{CollectExitCodes(Parse(text),found,depth+1);}catch{}}
        }
        static string FindStatus(object value)
        {return FindStringField(value,new[]{"status","state","finish_reason"},0,true)??FindStringField(value,new[]{"status","state","finish_reason"},0,false);}
        static bool FindErrorFlag(object value){return FindBoolField(value,new[]{"isError","is_error","error"},0);}
        static string FindStringField(object value,string[] keys,int depth,bool failuresOnly=false)
        {
            if(value==null||depth>12)return null;var d=value as Dictionary<string,object>;
            if(d!=null){foreach(string key in keys){object raw=Get(d,key);if(raw is string&&!String.IsNullOrWhiteSpace((string)raw)&&(!failuresOnly||IsFailureStatus((string)raw)))return (string)raw;}foreach(var pair in d){string found=FindStringField(pair.Value,keys,depth+1,failuresOnly);if(found!=null)return found;}return null;}
            var items=value as System.Collections.IEnumerable;
            if(items!=null&&!(value is string)){foreach(object item in items){string found=FindStringField(item,keys,depth+1,failuresOnly);if(found!=null)return found;}return null;}
            if(value is string){try{return FindStringField(Parse((string)value),keys,depth+1,failuresOnly);}catch{}}
            return null;
        }
        static bool FindBoolField(object value,string[] keys,int depth)
        {
            if(value==null||depth>12)return false;var d=value as Dictionary<string,object>;
            if(d!=null){foreach(string key in keys){object raw=Get(d,key);if(raw is bool&&Convert.ToBoolean(raw))return true;if(key=="error"&&raw is string&&!String.IsNullOrWhiteSpace((string)raw))return true;}foreach(var pair in d)if(FindBoolField(pair.Value,keys,depth+1))return true;return false;}
            var items=value as System.Collections.IEnumerable;
            if(items!=null&&!(value is string)){foreach(object item in items){if(FindBoolField(item,keys,depth+1))return true;}return false;}
            if(value is string){try{return FindBoolField(Parse((string)value),keys,depth+1);}catch{}}
            return false;
        }
        static object FindOutput(object value,int depth=0)
        {
            if(value==null||depth>12)return null;var d=value as Dictionary<string,object>;
            if(d!=null){foreach(string key in new[]{"aggregated_output","formatted_output","output","stdout","stderr","text","content","message","result","structuredContent","error"}){object raw=Get(d,key);if(raw==null)continue;if(raw is string&&String.IsNullOrWhiteSpace((string)raw))continue;object found=FindOutput(raw,depth+1);if(found!=null&&!String.IsNullOrWhiteSpace(Convert.ToString(found,CultureInfo.InvariantCulture)))return found;}return null;}
            if(value is string){string text=(string)value;if(String.IsNullOrWhiteSpace(text))return null;if(text.Length>0&&text[0]=='{')try{var parsed=Parse(text);object found=FindOutput(parsed,depth+1);if(found!=null)return found;}catch{}return text;}
            var items=value as System.Collections.IEnumerable;if(items!=null){var parts=new List<string>();foreach(object item in items){object found=FindOutput(item,depth+1);if(found!=null&&!String.IsNullOrWhiteSpace(Convert.ToString(found,CultureInfo.InvariantCulture)))parts.Add(Convert.ToString(found,CultureInfo.InvariantCulture));if(String.Join("\n",parts).Length>=12000)break;}return parts.Count==0?null:String.Join("\n",parts);}
            return null;
        }
        static string SafeField(string text,int max){return String.IsNullOrWhiteSpace(text)?null:Limit(text.Trim(),max);}
        static string BoundedRecorded(string text,int max){if(String.IsNullOrEmpty(text))return text;const string marker=" [truncated]";if(text.Length<=max)return text;return text.Substring(0,Math.Max(0,max-marker.Length))+marker;}
        static string RedactSensitive(string text)
        {
            if(String.IsNullOrEmpty(text))return text;
            string safe=Regex.Replace(text,@"(?i)\b(Bearer\s+)[A-Za-z0-9._~+/-]+=*","$1[REDACTED]");
            safe=Regex.Replace(safe,"(?i)([\\\"']?(?:api[_-]?key|access[_-]?token|refresh[_-]?token|auth(?:orization)?|password|passwd|secret|client[_-]?secret)[\\\"']?\\s*[:=]\\s*[\\\"']?)([^\\\"'\\s,;}\\]]+)","$1[REDACTED]");
            safe=Regex.Replace(safe,"(?i)(--(?:api[_-]?key|token|password|secret)\\s+)([^\\s\\\"']+)","$1[REDACTED]");
            safe=Regex.Replace(safe,@"(?i)(https?://[^/:\s]+:)[^@/\s]+@","$1[REDACTED]@");
            return safe;
        }
        string CompletionSummary(ActivitySnapshot snapshot,ActivityAgent a)
        {
            var publicResult=snapshot.Events.LastOrDefault(e=>String.Equals(e.AgentId,a.Id,StringComparison.OrdinalIgnoreCase)&&(e.Kind=="final"||e.Kind=="commentary")&&!String.IsNullOrWhiteSpace(e.Text));
            if(publicResult!=null)return "Task completed · "+Limit(RedactSensitive(publicResult.Text),190);
            if(!String.IsNullOrWhiteSpace(a.Action))return "Task completed after: "+Limit(RedactSensitive(a.Action),180);
            return SafeLabel("task_complete");
        }
        void EnforceRichDetailBudget(ActivitySnapshot s)
        {
            const int MaxRichChars=256*1024; // About 512 KiB of UTF-16 command/detail bodies across the returned snapshot.
            var all=s.Events.Concat(s.Agents.SelectMany(a=>a.RecentEvents)).OrderBy(e=>e.TimeUtc).ToList();
            int total=all.Sum(e=>(e.Command==null?0:e.Command.Length)+(e.Detail==null?0:e.Detail.Length));
            foreach(var e in all){if(total<=MaxRichChars)break;if(!String.IsNullOrEmpty(e.Command)){total-=e.Command.Length;e.Command="[truncated: activity detail limit]";total+=e.Command.Length;}if(total>MaxRichChars&&!String.IsNullOrEmpty(e.Detail)){total-=e.Detail.Length;e.Detail="[truncated: activity detail limit]";total+=e.Detail.Length;}}
            foreach(var e in all){if(total<=MaxRichChars)break;if(e.Command=="[truncated: activity detail limit]"){total-=e.Command.Length;e.Command="[truncated]";}if(total>MaxRichChars&&e.Detail=="[truncated: activity detail limit]"){total-=e.Detail.Length;e.Detail="[truncated]";}}
        }
        void Touch(ActivitySnapshot s,ActivityAgent a,DateTime tm){if(tm>s.UpdatedUtc)s.UpdatedUtc=tm;if(tm>a.UpdatedUtc)a.UpdatedUtc=tm;}
        void ParseCompletedItem(Dictionary<string,object> item,ActivitySnapshot s,ActivityAgent a,DateTime tm)
        {
            if(item==null)return;string type=Text(item,"type");
            string kind=null,text=null;
            if(type=="CommandExecution"){UpdateCallFromCompletedItem(item,s,a,tm,true);Touch(s,a,tm);return;}
            else if(type=="McpToolCall"){UpdateCallFromCompletedItem(item,s,a,tm,false);Touch(s,a,tm);return;}
            else if(type=="FileChange"){string status=Text(item,"status");bool failed=String.Equals(status,"failed",StringComparison.OrdinalIgnoreCase)||ExitCode(item)!=null&&ExitCode(item).Value!=0;AddCompletedFiles(s,a,item,tm,failed);kind=failed?"file_change_failed":"file_change_complete";var changes=Get(item,"changes") as Dictionary<string,object>;if(changes==null)return;var safe=changes.Keys.Select(SafeChangePath).Where(path=>path!=null).Select(path=>new{Path=path,Name=SafeFileName(path)}).ToList();var names=safe.Select(x=>x.Name).Where(x=>!String.IsNullOrEmpty(x)).Take(10).ToList();text="File change "+(failed?"failed":"completed")+" ("+safe.Count+")"+(names.Count==0?"":": "+String.Join(", ",names));}
            else if(type=="AgentMessage"){string phase=Text(item,"phase"),message=Text(item,"message")??Text(item,"text");if(phase=="commentary"){kind="progress_update";text=String.IsNullOrWhiteSpace(message)?"Progress update delivered":Limit(RedactSensitive(message),240);}else if(phase=="final_answer"){kind="answer_delivered";text=String.IsNullOrWhiteSpace(message)?"Answer delivered":Limit(RedactSensitive(message),240);}else return;}
            else return;
            Touch(s,a,tm);a.Action=Limit(text,240);AddEvent(s,new ActivityEvent{TimeUtc=tm,AgentId=a.Id,Kind=kind,Text=Limit(text,240)});
        }
        static int? ExitCode(Dictionary<string,object> item){object value=Get(item,"exit_code");int code;return value!=null&&Int32.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),out code)?(int?)code:null;}
        static string SafeLabelPart(string x){if(String.IsNullOrWhiteSpace(x))return null;string y=Regex.Replace(x.Trim(),@"[^A-Za-z0-9_.-]","_");return Limit(y,80);}
        static string SafeFileName(string path){if(String.IsNullOrWhiteSpace(path))return null;string value=path.Replace('\\','/');return Limit(value.Substring(value.LastIndexOf('/')+1),120);}
        static void AddCompletedFiles(ActivitySnapshot s,ActivityAgent a,Dictionary<string,object> item,DateTime tm,bool failed)
        {
            var changes=Get(item,"changes") as Dictionary<string,object>;if(changes==null)return;
            foreach(var change in changes){string path=SafeChangePath(change.Key);if(path==null)continue;string fileKind=failed?"change requested":"changed";
                var existing=a.Files.FirstOrDefault(f=>f.Path==path);if(existing!=null)existing.Kind=fileKind;else if(a.Files.Count<MaxFilesPerAgent)a.Files.Add(new ActivityFile{Path=path,Kind=fileKind});
            }
        }
        static string SafeChangePath(string raw){string path=NormalizePath(raw);if(path==null||path.Any(Char.IsControl)||path.Split(new[]{'/','\\'}).Any(part=>part==".."))return null;return path;}
        static bool IsPublicMessage(Dictionary<string,object> p,string x){string phase=Text(p,"phase");return !String.IsNullOrEmpty(x) && (phase=="commentary" || phase=="final");}
        string ClassifyTool(Dictionary<string,object> p)
        {
            string name=Text(p,"name")??"tool"; string input=Text(p,"arguments")??Text(p,"input")??"";
            if(name.IndexOf("exec",StringComparison.OrdinalIgnoreCase)>=0){
                string cmd=Regex.Match(input,"(?:cmd|command)\\s*[:=]\\s*[\\\"']([^\\\"']{1,500})",RegexOptions.IgnoreCase).Groups[1].Value;
                if(cmd.Length==0)cmd=input;
                string patch=ExtractPatchPath(input);if(patch!=null)return "edit requested · "+patch;
                string category=Regex.IsMatch(cmd,"\\b(test|check|build|compile)\\b",RegexOptions.IgnoreCase)?"test":Regex.IsMatch(cmd,"\\b(rg|findstr|Select-String|Get-ChildItem|dir)\\b",RegexOptions.IgnoreCase)?"search":Regex.IsMatch(cmd,"\\b(Set-Content|Out-File|Add-Content|Move-Item|Remove-Item)\\b",RegexOptions.IgnoreCase)?"edit":Regex.IsMatch(cmd,"\\b(Get-Content|type|cat)\\b",RegexOptions.IgnoreCase)?"read":"command";
                string path=ExtractStructuredPath(input)??ExtractCommandPath(cmd);
                return (category=="edit"?"edit requested":category)+(path==null?"":" · "+path);
            }
            return "tool: "+Limit(name,80);
        }
        void AddFiles(ActivitySnapshot s,ActivityAgent a,Dictionary<string,object> p,DateTime tm)
        {
            string name=Text(p,"name")??Text(p,"tool_name")??"";
            if(name=="spawn_agent"||name=="followup_task"||name=="send_message"||name=="wait_agent"||name=="wait")return;
            string raw=Text(p,"arguments")??Text(p,"input")??""; string cmd=ExtractCommand(raw); string action=ActionKind(cmd);
            string path=ExtractPatchPath(raw)??ExtractStructuredPath(raw);
            if(path==null && name.IndexOf("exec",StringComparison.OrdinalIgnoreCase)>=0)path=ExtractCommandPath(cmd);
            if(path==null)return;
            string label=action=="edit"?"edit requested":action;
            if(a.Files.Count<MaxFilesPerAgent && !a.Files.Any(f=>f.Path==path)) a.Files.Add(new ActivityFile {Path=path,Kind=label});
            AddEvent(s,new ActivityEvent{TimeUtc=tm,AgentId=a.Id,Kind=label,Path=path,Text=label+": "+path});
        }
        PendingAssignment FindAssignment(string id,string nickname,string role)
        {
            PendingAssignment x;if(assignments.TryGetValue(id,out x))return x;
            string key=Path.GetFileName((nickname??"").Replace('\\','/'));
            if(!String.IsNullOrEmpty(key) && assignments.TryGetValue(key,out x))return x;
            if(!String.IsNullOrEmpty(key)) { x=assignments.Values.FirstOrDefault(v=>String.Equals(v.TaskName,key,StringComparison.OrdinalIgnoreCase));if(x!=null)return x; }
            return null;
        }
        void TrackAssignment(Dictionary<string,object> p)
        {
            string name=Text(p,"name")??Text(p,"tool_name"); if(name!="spawn_agent"&&name!="followup_task")return;
            var args=Get(p,"arguments") as Dictionary<string,object>;
            if(args==null){string raw=Text(p,"arguments")??Text(p,"input");if(raw!=null)try{args=Parse(raw);}catch{return;}}
            if(args==null)return;
            string task=Text(args,"task_name"), target=Text(args,"target"), message=Text(args,"message");
            if(String.IsNullOrEmpty(target)){var td=Get(args,"target") as Dictionary<string,object>;target=Text(td,"agent_path")??Text(td,"task_name")??Text(td,"agent_id");}
            if(name=="followup_task" && String.IsNullOrEmpty(task)) task=target;
            if(String.IsNullOrEmpty(task))return;
            var x=new PendingAssignment{TaskName=Limit(task,120),Role=Text(args,"agent_type"),Model=Text(args,"model"),Effort=Text(args,"reasoning_effort"),Text=Limit(FirstLine(message),800)};
            string call=Text(p,"call_id")??Text(p,"id");if(!String.IsNullOrEmpty(call))pending[call]=x;
            assignments[task]=x;
            if(!String.IsNullOrEmpty(target))assignments[target]=x;
            foreach(var snap in snapshots.Values)foreach(var a in snap.Agents)if(String.Equals(a.Name,task,StringComparison.OrdinalIgnoreCase)||String.Equals(a.Name,target,StringComparison.OrdinalIgnoreCase)||String.Equals(a.Id,target,StringComparison.OrdinalIgnoreCase)) { a.Assignment=x.Text;a.RequestedModel=x.Model??a.RequestedModel;a.RequestedEffort=x.Effort??a.RequestedEffort;a.Role=x.Role??a.Role;a.Action="assignment updated"; }
        }
        void CorrelateOutput(Dictionary<string,object> p)
        {
            string call=Text(p,"call_id");PendingAssignment x;if(String.IsNullOrEmpty(call)||!pending.TryGetValue(call,out x))return;
            string output=Text(p,"output")??Text(p,"text")??OutputText(Get(p,"output"));
            foreach(Match m in Regex.Matches(output,@"(?:threadId|thread_id|agent_id)\s*[:=]\s*[""']?([A-Za-z0-9_-]{8,80})",RegexOptions.IgnoreCase))assignments[m.Groups[1].Value]=x;
            foreach(Match m in Regex.Matches(output,@"(?:agent_path|task_name)\s*[:=]\s*[""']?([A-Za-z0-9_/-]{2,120})",RegexOptions.IgnoreCase))assignments[Path.GetFileName(m.Groups[1].Value.Replace('/','\\'))]=x;
            pending.Remove(call);
        }
        static string OutputText(object value)
        {
            var sb=new StringBuilder();var items=value as System.Collections.IEnumerable;if(items==null)return "";
            foreach(object item in items){var d=item as Dictionary<string,object>;string t=Text(d,"text");if(t!=null&&sb.Length<4000)sb.Append(Limit(t,1000));}
            return sb.ToString();
        }
        static string FirstLine(string x){if(String.IsNullOrEmpty(x))return x;int i=x.IndexOfAny(new[]{'\r','\n'});return Limit(i<0?x:x.Substring(0,i),800);}
        static string ExtractCommand(string raw)
        {
            if(String.IsNullOrEmpty(raw))return "";
            try{var d=Parse(raw);string c=Text(d,"cmd")??Text(d,"command");if(!String.IsNullOrEmpty(c))return c;}catch{}
            Match m=Regex.Match(raw,"tools\\.exec_command\\s*\\(\\s*\\{\\s*cmd\\s*:\\s*[\\\"']((?:\\\\.|[^\\\"']){1,1000})",RegexOptions.IgnoreCase);if(m.Success)return m.Groups[1].Value;
            m=Regex.Match(raw,"(?:cmd|command)\\s*[:=]\\s*[\\\"']([^\\\"']{1,1000})",RegexOptions.IgnoreCase);return m.Success?m.Groups[1].Value:raw;
        }
        static string ActionKind(string cmd)
        {
            if(Regex.IsMatch(cmd,"\\b(test|check|build|compile)\\b",RegexOptions.IgnoreCase))return "test";
            if(Regex.IsMatch(cmd,"\\b(rg|findstr|Select-String|Get-ChildItem|dir)\\b",RegexOptions.IgnoreCase))return "search";
            if(Regex.IsMatch(cmd,"\\b(Set-Content|Out-File|Add-Content|Move-Item|Remove-Item|WriteAllText|WriteAllBytes)\\b",RegexOptions.IgnoreCase))return "edit";
            if(Regex.IsMatch(cmd,"\\b(Get-Content|type|cat|ReadAllText)\\b",RegexOptions.IgnoreCase))return "read";
            return "command";
        }
        static string ExtractPath(string x)
        {
            if(String.IsNullOrEmpty(x))return null;
            x=Regex.Replace(x,@"https?://[^\s""'<>]+","",RegexOptions.IgnoreCase);
            Match m=Regex.Match(x,"(?<![\\w])(?:(?:[A-Za-z]:[\\\\/]|\\\\\\\\|/|\\./|\\.\\./|[\\w.-]+[\\\\/])[\\w .-]+(?:[\\\\/][\\w .-]+)*\\.(?:cs|ps1|py|ts|tsx|js|jsx|toml|md|json|jsonl|txt|yml|yaml|html|css|sql|sh|bat|csproj|sln))(?=$|[\\s\\\"'<>|,;\\)\\]\\}])",RegexOptions.IgnoreCase);
            return m.Success?NormalizePath(m.Value):null;
        }
        static string ExtractStructuredPath(string raw)
        {
            try{var d=Parse(raw);foreach(string k in new[]{"path","file","file_path","filename"}){string p=Text(d,k);if(!String.IsNullOrWhiteSpace(p))return NormalizePath(p);}}catch{}
            return null;
        }
        static string ExtractCommandPath(string cmd){return ExtractPath(cmd);}
        static string ExtractPatchPath(string raw)
        {
            if(String.IsNullOrEmpty(raw))return null;
            raw=raw.Replace("\\n","\n");
            Match m=Regex.Match(raw,@"\*\*\* (?:Add|Update|Delete) File:\s*([^\r\n]{1,260})");
            if(!m.Success)return null;string p=NormalizePath(m.Groups[1].Value);return IsExplicitPathSafe(p)?p:null;
        }
        static string NormalizePath(string p)
        {
            if(String.IsNullOrWhiteSpace(p))return null;
            p=p.Trim().Trim('"','\'').Replace("\\\\","\\");
            if(!IsExplicitPathSafe(p))return null;return Limit(p,260);
        }
        static bool IsExplicitPathSafe(string p)
        {
            if(String.IsNullOrWhiteSpace(p)||p.Length>260||p.IndexOf('\r')>=0||p.IndexOf('\n')>=0||Regex.IsMatch(p,@"\\n",RegexOptions.IgnoreCase))return false;
            return !p.StartsWith("http://",StringComparison.OrdinalIgnoreCase)&&!p.StartsWith("https://",StringComparison.OrdinalIgnoreCase)&&!p.Contains("://");
        }
        static void CopyEventFields(ActivityEvent source,ActivityEvent target)
        {target.TimeUtc=source.TimeUtc;target.AgentId=source.AgentId;target.Kind=source.Kind;target.Text=source.Text;target.Path=source.Path;target.CallId=source.CallId;target.Tool=source.Tool;target.Command=source.Command;target.Purpose=source.Purpose;target.Outcome=source.Outcome;target.Detail=source.Detail;target.ExitCode=source.ExitCode;}
        void AddEvent(ActivitySnapshot s,ActivityEvent e)
        {e.Text=Limit(RedactSensitive(e.Text),240);e.Purpose=BoundedRecorded(RedactSensitive(e.Purpose),256);e.Command=BoundedRecorded(RedactSensitive(e.Command),8000);e.Detail=BoundedRecorded(RedactSensitive(e.Detail),12000);e.Tool=SafeField(RedactSensitive(e.Tool),160);e.Outcome=SafeField(RedactSensitive(e.Outcome),128);s.Events.Add(e);var agent=s.Agents.FirstOrDefault(a=>String.Equals(a.Id,e.AgentId,StringComparison.OrdinalIgnoreCase));if(agent!=null){var copy=new ActivityEvent();CopyEventFields(e,copy);agent.RecentEvents.Add(copy);if(agent.RecentEvents.Count>20)agent.RecentEvents.RemoveRange(0,agent.RecentEvents.Count-20);}if(s.Events.Count>MaxEvents*2)s.Events=s.Events.OrderByDescending(x=>x.TimeUtc).Take(MaxEvents).ToList();EnforceRichDetailBudget(s);}
        static Dictionary<string,object> Parse(string x){return new JavaScriptSerializer{MaxJsonLength=2*1024*1024,RecursionLimit=40}.Deserialize<Dictionary<string,object>>(x);}
        static object Get(Dictionary<string,object>d,string k){object v;return d!=null&&d.TryGetValue(k,out v)?v:null;}
        static string Text(Dictionary<string,object>d,string k){object v=Get(d,k);return v is string?(string)v:null;}
        static DateTime Date(Dictionary<string,object>d,string k,DateTime fallback){DateTime v;return DateTime.TryParse(Text(d,k),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out v)?v:fallback;}
        static string SafeProject(string cwd){try{return String.IsNullOrEmpty(cwd)?null:new DirectoryInfo(cwd).Name;}catch{return null;}}
        static string SafeLabel(string s){return s=="task_started"?"Task started":s=="task_complete"?"Task completed":s=="turn_aborted"?"Turn aborted":"Activity";}
        static string Limit(string s,int n){return String.IsNullOrEmpty(s)?s:(s.Length>n?s.Substring(0,n):s);}
        static ActivitySnapshot Clone(ActivitySnapshot x)
        {
            var y=new ActivitySnapshot {SessionId=x.SessionId,Project=x.Project,Task=x.Task,State=x.State,Notice=x.Notice,UpdatedUtc=x.UpdatedUtc};
            foreach(var a in x.Agents){var b=new ActivityAgent {Id=a.Id,ParentId=a.ParentId,Name=a.Name,Role=a.Role,Model=a.Model,Effort=a.Effort,RequestedModel=a.RequestedModel,RequestedEffort=a.RequestedEffort,State=a.State,Action=a.Action,Assignment=a.Assignment,StartedUtc=a.StartedUtc,UpdatedUtc=a.UpdatedUtc};foreach(var f in a.Files)b.Files.Add(new ActivityFile{Path=f.Path,Kind=f.Kind});foreach(var e in a.RecentEvents){var copy=new ActivityEvent();CopyEventFields(e,copy);b.RecentEvents.Add(copy);}y.Agents.Add(b);}
            foreach(var e in x.Events){var copy=new ActivityEvent();CopyEventFields(e,copy);y.Events.Add(copy);}return y;
        }
        static List<T> TakeLastCompat<T>(IEnumerable<T> source,int n){var l=source.ToList();return l.Skip(Math.Max(0,l.Count-n)).ToList();}
        void EnsureOpen(){if(disposed)throw new ObjectDisposedException("CodexActivityObserver");}
        public void Dispose(){lock(gate){disposed=true;tails.Clear();snapshots.Clear();}}
    }
}
