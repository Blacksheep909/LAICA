using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Laica;

internal static class ObserverFeedbackTests
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static int serial;
    static void Must(bool value,string message){if(!value)throw new Exception(message);}
    static Dictionary<string,object> D(params object[] values){var d=new Dictionary<string,object>();for(int i=0;i<values.Length;i+=2)d[(string)values[i]]=values[i+1];return d;}
    static string Line(string type,object payload){string time=new DateTime(2026,10,2,0,0,0,DateTimeKind.Utc).AddSeconds(++serial).ToString("o");return Json.Serialize(D("type",type,"timestamp",time,"payload",payload));}
    static string LineAt(string type,object payload,DateTime time){return Json.Serialize(D("type",type,"timestamp",time.ToString("o"),"payload",payload));}
    static string Completed(object item){return Line("event_msg",D("type","item_completed","item",item));}
    static int Main(){string root=Path.Combine(Path.GetTempPath(),"laica-observer-feedback-"+Guid.NewGuid().ToString("N"));try{Run(root);Console.WriteLine("ObserverFeedbackTests PASS");return 0;}catch(Exception ex){Console.Error.WriteLine("ObserverFeedbackTests FAIL: "+ex.Message);return 1;}finally{SafeDelete(root);}}
    static void Run(string root)
    {
        string sessions=Path.Combine(root,"sessions","2026","10","02");Directory.CreateDirectory(sessions);
        string id="01abcde0-1000-0000-0000-000000000001",childId="01abcde0-1000-0000-0000-000000000002",coldId="01abcde0-1000-0000-0000-000000000003";
        string file=Path.Combine(sessions,"rollout-"+id+".jsonl"),child=Path.Combine(sessions,"rollout-"+childId+".jsonl");
        File.WriteAllText(file,Line("session_meta",D("id",id,"cwd","C:\\work\\sample"))+"\n"+Line("event_msg",D("type","task_started"))+"\n",new UTF8Encoding(false));
        File.WriteAllText(child,Line("session_meta",D("id",childId,"source",D("subagent",D("thread_spawn",D("parent_thread_id",id,"agent_nickname","Worker")))))+"\n"+Completed(D("type","AgentMessage","phase","commentary","message","Worker reports fixture progress"))+"\n",new UTF8Encoding(false));
        string cold=Path.Combine(sessions,"rollout-"+coldId+".jsonl");
        File.WriteAllText(cold,Line("session_meta",D("id",coldId,"cwd","C:\\work\\cold"))+"\n"+Json.Serialize(D("type","response_item","timestamp","2099-01-01T00:00:00Z","payload",D("type","message","role","assistant","phase","analysis","content",new object[]{D("type","output_text","text","COLD SECRET REASONING")})))+"\n",new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddMinutes(-5));File.SetLastWriteTimeUtc(child,DateTime.UtcNow.AddMinutes(-4));
        using(var observer=new CodexActivityObserver(root))
        {
            var missing=observer.ListSessions();Must(missing.Count==2&&!missing.Any(x=>x.Id==childId)&&missing.Any(x=>x.Id==id&&x.Title.Contains(id.Substring(id.Length-12))),"missing index fallback title and child exclusion");
            string index=Path.Combine(root,"session_index.jsonl");
            File.WriteAllText(index,"malformed {\n"+Json.Serialize(D("id",id,"thread_name","First\nSession\u0001","updated_at","2099-01-01T00:00:00Z"))+"\n",new UTF8Encoding(false));
            DateTime logTime=File.GetLastWriteTimeUtc(file);
            var first=observer.ListSessions().Single(x=>x.Id==id);Must(first.Title=="First Session"&&first.UpdatedUtc==logTime,"index title is sanitized and rollout modification time controls recency");
            using(var append=new StreamWriter(new FileStream(index,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete),new UTF8Encoding(false))){append.WriteLine(Json.Serialize(D("id",id,"thread_name","Renamed session","updated_at","2001-01-01T00:00:00Z")));}
            var renamed=observer.ListSessions().Single(x=>x.Id==id);Must(renamed.Title=="Renamed session"&&renamed.UpdatedUtc==logTime,"latest appended title was not read or changed session recency");

            var baseline=observer.Read(id);var rootAgent=baseline.Agents[0];var childAgent=baseline.Agents.Single(x=>x.Id==childId);
            Must(childAgent.RecentEvents.Any(x=>x.Text=="Worker reports fixture progress"),"agent recent events omit safe child public commentary");
            Must(!rootAgent.RecentEvents.Any(x=>x.Text=="Worker reports fixture progress"),"child feedback leaked into supervisor per-agent event history");
            DateTime freshness=rootAgent.UpdatedUtc;int recent=rootAgent.RecentEvents.Count;
            Append(file,Line("response_item",D("type","message","role","assistant","phase","analysis","content",new object[]{D("type","output_text","text","SECRET REASONING")},"timestamp","2099-01-01T00:00:00Z")));
            var privateRead=observer.Read(id);Must(privateRead.Agents[0].UpdatedUtc==freshness&&privateRead.Agents[0].RecentEvents.Count==recent,"private reasoning changed visible activity freshness");
            var coldRead=observer.Read(coldId);
            Must(coldRead.Events.Count==0&&coldRead.Agents[0].RecentEvents.Count==0&&coldRead.Agents[0].UpdatedUtc.Year<2099&&coldRead.UpdatedUtc.Year<2099,
                "cold recovery promoted private future-dated reasoning into activity or freshness");

            Append(file,Completed(D("type","CommandExecution","status","completed","exit_code",0,"command","fixture command one","cwd","PRIVATE CWD","output","fixture output one")));
            Append(file,Completed(D("type","CommandExecution","status","failed","exit_code",1,"command","fixture command two","stderr","fixture stderr output")));
            Append(file,Completed(D("type","McpToolCall","status","completed","server","laica","tool","get_team","arguments","PRIVATE MCP ARGUMENT","result","fixture MCP result")));
            string absoluteFile="C:\\work\\staging\\AbsoluteAllowed.cs";
            Append(file,Completed(D("type","FileChange","status","completed","changes",D("work/laica/Changed.cs",D("type","update","content","SECRET DIFF"),absoluteFile,D("type","update"),"../outside.cs",D("type","add"),"https://host/private/Remote.cs",D("type","update"),"work\\bad\u0001name.cs",D("type","delete")))));
            Append(file,Completed(D("type","FileChange","status","failed","changes",D("work/laica/Requested.cs",D("type","update")))));
            Append(file,Completed(D("type","AgentMessage","phase","final_answer","message","The fixture completed its public answer.")));
            var feedback=observer.Read(id);var feedbackAgent=feedback.Agents[0];
            Must(feedback.Events.Any(x=>x.Kind=="command_complete"&&x.Command=="fixture command one"&&x.ExitCode==0&&x.Detail.Contains("fixture output one"))&&feedback.Events.Any(x=>x.Kind=="command_failed"&&x.Command=="fixture command two"&&x.ExitCode==1&&x.Detail.Contains("fixture stderr output")),"command details and completion status labels");
            Must(feedback.Events.Any(x=>x.Text=="Tool completed · laica.get_team"),"MCP safe tool label");
            Must(feedbackAgent.Files.Any(x=>x.Path=="work/laica/Changed.cs"&&x.Kind=="changed")&&feedbackAgent.Files.Any(x=>x.Path==absoluteFile&&x.Kind=="changed")&&feedbackAgent.Files.Any(x=>x.Path=="work/laica/Requested.cs"&&x.Kind=="change requested"),"file status or explicit local absolute path");
            Must(!feedbackAgent.Files.Any(x=>x.Path.Contains("outside")||x.Path.Contains("Remote")||x.Path.Contains("bad")),"traversal, URL, or control-character paths were exposed");
            var fileEvents=feedback.Events.Where(x=>x.Kind.StartsWith("file_change")).ToList();Must(fileEvents.Any(x=>x.Text.Contains("(2):")&&x.Text.Contains("Changed.cs")&&x.Text.Contains("AbsoluteAllowed.cs")&&!x.Text.Contains(absoluteFile))&&fileEvents.Any(x=>x.Text.Contains("(1): Requested.cs")),"file feedback does not show safe basenames and counts");
            Must(feedback.Events.Any(x=>x.Text=="The fixture completed its public answer."),"AgentMessage public final-answer text was not preserved");
            string feedbackJson=Json.Serialize(feedback);foreach(string secret in new[]{"SECRET REASONING","COLD SECRET REASONING","PRIVATE CWD","PRIVATE MCP ARGUMENT","SECRET DIFF","https://host/private/Remote.cs","bad\u0001name.cs"})Must(!feedbackJson.Contains(secret),"private field leaked: "+secret);
            DateTime completedAt=DateTime.UtcNow.AddMinutes(5);
            Append(file,LineAt("event_msg",D("type","task_complete"),completedAt));
            var completeRead=observer.Read(id);
            Must(completeRead.State=="complete"&&completeRead.UpdatedUtc==completedAt&&completeRead.Agents[0].UpdatedUtc==completedAt&&completeRead.Events.Any(x=>x.Kind=="complete"&&x.TimeUtc==completedAt),
                "incremental task_complete did not update state, event, and freshness from its timestamp");
            for(int i=0;i<130;i++)Append(file,Completed(D("type","CommandExecution","status","completed","exit_code",0,"command","fixture command "+i,"output","fixture output "+i)));
            var read=observer.Read(id);var agent=read.Agents[0];
            Must(agent.RecentEvents.Count==20&&agent.RecentEvents.All(x=>x.Kind=="command_complete"),"per-agent history was not bounded to newest 20 events");
            Must(read.Events.Count<=120&&read.Agents.Single(x=>x.Id==childId).RecentEvents.Any(x=>x.Text=="Worker reports fixture progress"),"global trimming erased another agent's recent history");
            int count=agent.RecentEvents.Count;var repeated=observer.Read(id);Must(repeated.Agents[0].RecentEvents.Count==count,"reading without an append duplicated recent events");
        }
    }
    static void Append(string path,string row){using(var w=new StreamWriter(new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete),new UTF8Encoding(false)))w.WriteLine(row);}
    static void SafeDelete(string path){string temp=Path.GetFullPath(Path.GetTempPath());string full=Path.GetFullPath(path);if(!full.StartsWith(temp,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Refusing to remove fixture outside the temp directory.");try{if(Directory.Exists(full))Directory.Delete(full,true);}catch{}}
}
