using System;
using System.IO;
using System.Text;
using System.Linq;
using Laica;

internal static class ObserverTests
{
    static void Must(bool value,string message){if(!value)throw new Exception(message);}
    static int Main(string[] args)
    {
        try{return RunAll(args);}catch(Exception ex){Console.Error.WriteLine("ObserverTests FAIL: "+ex.Message);return 1;}
    }
    static int RunAll(string[] args)
    {
        if(args.Length==3 && args[0]=="--live")
        {
            try{using(var o=new CodexActivityObserver(args[1])){var s=o.Read(args[2]);Console.WriteLine("SESSION="+s.SessionId+" PROJECT="+s.Project+" STATE="+s.State+" EVENTS="+s.Events.Count);foreach(var a in s.Agents)Console.WriteLine("AGENT="+a.Id+" ROLE="+a.Role+" MODEL="+a.Model+" EFFORT="+a.Effort+" STATE="+a.State+" FILES="+a.Files.Count);return 0;}}
            catch(Exception ex){Console.Error.WriteLine("LIVE READ FAIL: "+ex.GetType().Name);return 1;}
        }
        string root=Path.Combine(Path.GetTempPath(),"laica-observer-"+Guid.NewGuid().ToString("N"));
        string dir=Path.Combine(root,"sessions","2026","10","02"); Directory.CreateDirectory(dir);
        string id="01abcde0-0000-0000-0000-000000000001", file=Path.Combine(dir,"rollout-"+id+".jsonl");
        string staleId="01abcde0-0000-0000-0000-000000000003", staleFile=Path.Combine(dir,"rollout-"+staleId+".jsonl");
        string childId="01abcde0-0000-0000-0000-000000000002", childFile=Path.Combine(dir,"rollout-"+childId+".jsonl");
        try
        {
            File.WriteAllText(file,"{\"type\":\"session_meta\",\"payload\":{\"id\":\""+id+"\",\"session_id\":\""+id+"\",\"cwd\":\"C:\\\\work\\\\demo\",\"creator_user_id\":\"MUST_NOT_APPEAR\"}}\n"+
                "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-6-luna\",\"effort\":\"medium\",\"summary\":\"SECRET SUMMARY\"}}\n"+
                "{\"type\":\"world_state\",\"payload\":{\"full\":\""+new string('x',2*1024*1024+32)+"\"}}\n"+
                "{\"type\":\"world_state\",\"payload\":{\"full\":\"SECRET WORLD\"}}\n"+
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"analysis\",\"content\":[{\"type\":\"output_text\",\"text\":\"SECRET REASONING\"}]}}\n"+
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"final\",\"content\":[{\"type\":\"output_text\",\"text\":\"Public completion\"}]}}\n"+
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"id\":\"spawn1\",\"name\":\"spawn_agent\",\"arguments\":\"{\\\"task_name\\\":\\\"Scout\\\",\\\"agent_type\\\":\\\"investigator\\\",\\\"model\\\":\\\"gpt-6-luna\\\",\\\"reasoning_effort\\\":\\\"medium\\\",\\\"message\\\":\\\"Inspect public session links\\nIgnore private data\\\"}\"}}\n"+
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call_output\",\"call_id\":\"spawn1\",\"output\":[{\"type\":\"text\",\"text\":\"{\\\"threadId\\\":\\\""+childId+"\\\"}\"}]}}\n"+
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"name\":\"exec\",\"arguments\":\"tools.exec_command({cmd: \\\"rg TODO work/laica/Laica.Observer.cs\\\", workdir: \\\"work/laica\\\"})\"}}\n"+
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"name\":\"exec\",\"arguments\":\"{\\\"cmd\\\":\\\"Get-Content C:\\\\\\\\Users\\\\\\\\Example\\\\\\\\Live File.cs\\\"}\"}}\n"+
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"work/laica/Structured File.cs\\\"}\"}}\n"+
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"name\":\"exec\",\"arguments\":\"tools.exec_command({cmd: \\\"inspect /Users/example/Live.cs\\\\n\\\\n+ extra\\\"})\"}}\n"+
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"name\":\"exec\",\"arguments\":\"{\\\"cmd\\\":\\\"curl https://example.com/not-a-file.cs\\\"}\"}}\n"+
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"name\":\"exec\",\"arguments\":\"tools.exec_command({cmd: \\\"*** Add File: work/laica/New File.cs\\\\n+class NewFile {}\\\\n*** End Patch\\\"})\"}}\n"+
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"name\":\"followup_task\",\"arguments\":\"{\\\"target\\\":\\\"/root/laica_visual\\\",\\\"message\\\":\\\"Inspect the UI\\\"}\"}}\n"+
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"timestamp\":\"2026-10-02T00:00:00Z\"}}\n");
            File.WriteAllText(childFile,"{\"type\":\"session_meta\",\"payload\":{\"id\":\""+childId+"\",\"timestamp\":\"2026-10-02T00:00:01Z\",\"source\":{\"subagent\":{\"thread_spawn\":{\"parent_thread_id\":\""+id+"\",\"agent_nickname\":\"Scout\",\"agent_role\":\"investigator\"}}}}}\n{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-6-luna\",\"effort\":\"medium\"}}\n");
            File.WriteAllText(staleFile,"{\"type\":\"session_meta\",\"payload\":{\"id\":\""+staleId+"\",\"timestamp\":\"2026-10-02T00:00:00Z\",\"cwd\":\"C:\\\\work\\\\old\"}}\n{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-6.1-sol\",\"effort\":\"high\"}}\n{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"timestamp\":\"2026-10-02T00:00:01Z\"}}\n");
            using(var o=new CodexActivityObserver(root))
            {
                var choices=o.ListSessions(); Must(choices.Count==2 && choices.Exists(c=>c.Id==id),"session discovery excludes child logs");
                var s=o.Read(id); Must(s.Agents[0].Model=="gpt-6-luna" && s.Agents[0].Effort=="medium","native model and effort");
                Must(s.Agents[0].StartedUtc.Year==2026,"bounded historical task recovery");
                var stale=o.Read(staleId);if(stale.State!="stale"||stale.Agents[0].Model!="gpt-6.1-sol"||stale.Agents[0].Effort!="high")throw new Exception("stale/context: "+stale.State+" "+stale.Agents[0].State+" "+stale.Agents[0].Model+" "+stale.Agents[0].Effort+" "+stale.Agents[0].UpdatedUtc.ToString("o"));
                Must(s.Events.Exists(e=>e.Kind=="started"&&e.TimeUtc==new DateTime(2026,10,2,0,0,0,DateTimeKind.Utc)),"historical payload timestamp is preserved");
                string exported=new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(s);
                Must(!exported.Contains("SECRET")&&!exported.Contains("MUST_NOT_APPEAR"),"all exported fields exclude private records");
                if(!s.Events.Exists(e=>e.Text=="Task started") || !s.Events.Exists(e=>e.Text=="Public completion"))throw new Exception("public allowlist: "+String.Join("|",s.Events.ConvertAll(e=>e.Kind+":"+e.Text).ToArray()));
                Must(s.Events[0].Text.IndexOf("SECRET",StringComparison.Ordinal)<0,"private content leaked");
            Must(s.Agents.Count==2 && s.Agents[1].ParentId==id && s.Agents[1].Role=="investigator" && s.Agents[1].Model=="gpt-6-luna" && s.Agents[1].Effort=="medium" && s.Agents[1].Assignment=="Inspect public session links","native child relationship/model/role/assignment");
                Must(s.Agents[0].Action.Length<200&&!s.Agents[0].Action.Contains("\n")&&s.Agents[0].Files.Exists(f=>f.Path.Contains("Laica.Observer.cs")&&f.Kind=="search"),"safe action and explicit relative file path");
                Must(s.Agents[0].Files.Exists(f=>f.Path==@"C:\Users\Example\Live File.cs"),"escaped structured command path normalization");
                Must(s.Agents[0].Files.Exists(f=>f.Path=="work/laica/Structured File.cs"),"structured JSON path argument");
                if(!s.Agents[0].Files.Exists(f=>f.Path=="work/laica/New File.cs"))throw new Exception("explicit patch header path with spaces: "+String.Join("|",s.Agents[0].Files.ConvertAll(f=>f.Path+"="+f.Kind).ToArray()));
                Must(!s.Agents[0].Files.Exists(f=>f.Path.Contains("/Users/example/Live.cs")||f.Path.Contains("/root/laica_visual")||f.Path.Contains("example.com")),"escaped newline, URL, and agent paths are excluded");
                using(var w=new FileStream(file,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete))using(var sw=new StreamWriter(w,new UTF8Encoding(false))){sw.Write("{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"timestamp\":\"2026-10-02T00:00:02Z\"}}\n{\"type\":\"event_msg\",\"payload\":");}
                var partial=o.Read(id); if(!partial.Events.Exists(e=>e.Kind=="complete"&&e.Text.Contains("Task completed")&&e.Text.Contains("Public completion")) || partial.State!="complete" || partial.Events.Count>120)throw new Exception("incremental append and partial-line tolerance: "+partial.State+" | "+String.Join(" || ",partial.Events.Select(e=>e.Kind+":"+e.Text).ToArray()));
                using(var w=new FileStream(file,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete))using(var sw=new StreamWriter(w,new UTF8Encoding(false))){sw.Write("{}\n{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"id\":\"follow1\",\"name\":\"followup_task\",\"arguments\":\"{\\\"target\\\":\\\"Scout\\\",\\\"model\\\":\\\"gpt-6-luna\\\",\\\"reasoning_effort\\\":\\\"high\\\",\\\"message\\\":\\\"Inspect the followup mapping\\\"}\"}}\n");}
                var followed=o.Read(id);var scout=followed.Agents.First(a=>a.Id==childId);Must(scout.Model=="gpt-6-luna"&&scout.Effort=="medium"&&scout.RequestedEffort=="high"&&scout.Assignment=="Inspect the followup mapping","followup preserves actual context and updates requested assignment");
                File.WriteAllText(file,"{\"type\":\"session_meta\",\"payload\":{\"id\":\""+id+"\",\"cwd\":\"C:\\\\work\\\\demo\"}}\n{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-6-sol\",\"effort\":\"high\"}}\n");
                var trunc=o.Read(id); Must(trunc.Agents[0].Model=="gpt-6-sol","truncation reset");
                Must(s.Notice.IndexOf("best effort",StringComparison.OrdinalIgnoreCase)>=0,"monitoring notice");
            }
            Console.WriteLine("ObserverTests PASS"); return 0;
        }
        catch(Exception ex){Console.Error.WriteLine("ObserverTests FAIL: "+ex.Message);return 1;}
        finally { try{Directory.Delete(root,true);}catch{} }
    }
}
