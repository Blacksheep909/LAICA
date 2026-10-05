using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Laica;

internal static class ObserverWorkTests
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    static int Main()
    {
        string home=Path.Combine(Path.GetTempPath(),"laica-observer-work-"+Guid.NewGuid().ToString("N"));
        try { Run(home); Console.WriteLine("ObserverWorkTests PASS"); return 0; }
        catch(Exception ex) { Console.Error.WriteLine("ObserverWorkTests FAIL: "+ex); return 1; }
        finally { try { if(Directory.Exists(home))Directory.Delete(home,true); } catch { } }
    }
    static void Run(string home)
    {
        string sessions=Path.Combine(home,"sessions","2026","10","05");Directory.CreateDirectory(sessions);
        string root="root-session-0001", child="child-session-0001";
        var rootLines=new List<string>();
        rootLines.Add(Envelope("session_meta",Obj("id",root,"cwd","C:\\work\\project","timestamp","2026-10-05T10:00:00Z"),"2026-10-05T10:00:00Z"));
        rootLines.Add(Envelope("event_msg",Obj("type","task_started"),"2026-10-05T10:00:01Z"));
        rootLines.Add(Envelope("response_item",Obj("role","user","content",new[]{Obj("type","input_text","text","## My request: Inspect the local fixture run")} ),"2026-10-05T10:00:01Z"));
        rootLines.Add(Envelope("response_item",Obj("type","function_call","name","exec_command","call_id","root-ok","arguments",Obj("cmd","dotnet build","justification","Verify the project build")),"2026-10-05T10:00:02Z"));
        rootLines.Add(Envelope("response_item",Obj("type","function_call_output","call_id","root-ok","output",Obj("exit_code",0,"output","Build completed successfully")),"2026-10-05T10:00:03Z"));
        rootLines.Add(Envelope("response_item",Obj("type","function_call","name","exec","call_id","root-bad","arguments","tools.exec_command({cmd: \"danger --api-key=hidden-secret\"})"),"2026-10-05T10:00:04Z"));
        rootLines.Add(Envelope("response_item",Obj("type","function_call_output","call_id","root-bad","output",Obj("isError",false,"result",Obj("exit_code",7,"stderr","Authorization: Bearer abcdefghijk"))),"2026-10-05T10:00:05Z"));
        rootLines.Add(Envelope("response_item",Obj("type","function_call","name","exec","call_id","root-batch","arguments","tools.exec_command({cmd: \"first batch command\"}); tools.exec_command({cmd: \"second batch command\"})"),"2026-10-05T10:00:05Z"));
        rootLines.Add(Envelope("response_item",Obj("type","function_call_output","call_id","root-batch","output",Obj("isError",false,"result",Obj("exit_code",2,"output","second command failed"))),"2026-10-05T10:00:05Z"));
        rootLines.Add(Envelope("response_item",Obj("type","custom_tool_call","name","mcp__fixture__zero_error","server","fixture","tool","zero_error","call_id","root-zeroerr"),"2026-10-05T10:00:05Z"));
        rootLines.Add(Envelope("response_item",Obj("type","custom_tool_call_output","call_id","root-zeroerr","output","{\"isError\":true,\"exit_code\":0,\"content\":\"stringified failure\"}"),"2026-10-05T10:00:05Z"));
        rootLines.Add(Envelope("response_item",Obj("type","custom_tool_call","name","mcp__fixture__mixed","server","fixture","tool","mixed","call_id","root-mixed"),"2026-10-05T10:00:05Z"));
        rootLines.Add(Envelope("response_item",Obj("type","custom_tool_call_output","call_id","root-mixed","output",new[]{Obj("isError",false,"status","completed","exit_code",0),Obj("isError",true,"status","failed","exit_code",0)}),"2026-10-05T10:00:05Z"));
        rootLines.Add(Envelope("response_item",Obj("type","custom_tool_call","name","mcp__fixture__status_mix","server","fixture","tool","status_mix","call_id","root-status-mix"),"2026-10-05T10:00:05Z"));
        rootLines.Add(Envelope("response_item",Obj("type","custom_tool_call_output","call_id","root-status-mix","output",new[]{Obj("status","completed"),Obj("status","failed"),Obj("exit_code",0)}),"2026-10-05T10:00:05Z"));
        rootLines.Add(Envelope("response_item",Obj("type","custom_tool_call","name","mcp__fixture__json_status","server","fixture","tool","json_status","call_id","root-json-status"),"2026-10-05T10:00:05Z"));
        rootLines.Add(Envelope("response_item",Obj("type","custom_tool_call_output","call_id","root-json-status","output","{\"status\":\"failed\",\"exit_code\":7}"),"2026-10-05T10:00:05Z"));
        rootLines.Add(Envelope("event_msg",Obj("type","item_completed","item",Obj("type","CommandExecution","call_id","root-aggregate","command","aggregate command","exit_code",0,"aggregated_output","aggregated fixture output")),"2026-10-05T10:00:05Z"));
        rootLines.Add(Envelope("event_msg",Obj("type","item_completed","item",Obj("type","CommandExecution","call_id","root-stderr","command","stderr command","exit_code",0,"stdout","","stderr","stderr fixture output")),"2026-10-05T10:00:05Z"));
        rootLines.Add(Envelope("response_item",Obj("type","function_call","name","exec","call_id","root-purpose","arguments","tools.exec_command({cmd: \"purpose command\", justification: \"Collect fixture evidence\"})"),"2026-10-05T10:00:05Z"));
        rootLines.Add(Envelope("response_item",Obj("type","function_call_output","call_id","root-purpose","output",Obj("exit_code",0)),"2026-10-05T10:00:05Z"));
        rootLines.Add(Envelope("response_item",Obj("type","function_call","name","exec_command","call_id","root-missing","arguments",Obj("title","Run without command text")),"2026-10-05T10:00:06Z"));
        rootLines.Add(Envelope("event_msg",Obj("type","item_completed","item",Obj("type","CommandExecution","call_id","root-missing","exit_code",0)),"2026-10-05T10:00:07Z"));
        rootLines.Add(Envelope("response_item",Obj("type","custom_tool_call","name","mcp__local__lookup","server","local.fixture","tool","lookup","call_id","root-mcp","arguments",Obj("query","safe")),"2026-10-05T10:00:08Z"));
        rootLines.Add(Envelope("response_item",Obj("type","custom_tool_call_output","call_id","root-mcp","output",Obj("isError",true,"content",new[]{Obj("text","fixture failed") })),"2026-10-05T10:00:09Z"));
        rootLines.Add(Envelope("response_item",Obj("type","function_call","name","exec_command","call_id","root-large","arguments",Obj("cmd",new string('x',9000))),"2026-10-05T10:00:10Z"));
        rootLines.Add(Envelope("response_item",Obj("type","function_call_output","call_id","root-large","output",Obj("exit_code",0,"output",new string('y',13000))),"2026-10-05T10:00:11Z"));
        rootLines.Add(Envelope("response_item",Obj("type","function_call","name","exec_command","call_id","same-call","arguments",Obj("cmd","root-specific")),"2026-10-05T10:00:12Z"));
        rootLines.Add(Envelope("response_item",Obj("type","function_call_output","call_id","same-call","output",Obj("exit_code",0)),"2026-10-05T10:00:13Z"));
        rootLines.Add(Envelope("response_item",Obj("role","assistant","type","message","phase","final","content",new[]{Obj("type","output_text","text","Root finished the local fixture run.")} ),"2026-10-05T10:00:14Z"));
        rootLines.Add(Envelope("event_msg",Obj("type","task_complete"),"2026-10-05T10:00:15Z"));
        Write(Path.Combine(sessions,"root.jsonl"),rootLines);
        var childLines=new List<string>{Envelope("session_meta",Obj("id",child,"cwd","C:\\work\\child","timestamp","2026-10-05T10:00:00Z","source",Obj("subagent",Obj("thread_spawn",Obj("parent_thread_id",root,"agent_nickname","Worker")))),"2026-10-05T10:00:00Z")};
        childLines.Add(Envelope("event_msg",Obj("type","task_started"),"2026-10-05T10:00:01Z"));
        childLines.Add(Envelope("response_item",Obj("type","function_call","name","exec_command","call_id","same-call","arguments",Obj("cmd","child-specific")),"2026-10-05T10:00:02Z"));
        childLines.Add(Envelope("response_item",Obj("type","function_call_output","call_id","same-call","output",Obj("exit_code",9)),"2026-10-05T10:00:03Z"));
        Write(Path.Combine(sessions,"child.jsonl"),childLines);
        using(var observer=new CodexActivityObserver(home))
        {
            var overview=observer.ReadOverview(6);Must(overview.Count==1&&overview[0].Session.Id==root,"overview includes only root sessions");Must(overview[0].Activity.Task=="Inspect the local fixture run"&&overview[0].Activity.State=="complete","overview returns current root task and state");
            var snapshot=observer.Read(root);var rootAgent=snapshot.Agents.Single(a=>a.Id==root);var childAgent=snapshot.Agents.Single(a=>a.Id==child);
            var rootEvents=snapshot.Events.Where(e=>e.AgentId==root).ToList();
            var refreshedOverview=observer.ReadOverview(6);Must(refreshedOverview.Count==1&&refreshedOverview[0].Activity.Agents.Any(a=>a.Id==child),"overview retains previously discovered child details without listing child as a team");
            var ok=rootEvents.Single(e=>e.CallId=="root-ok");Must(ok.Command=="dotnet build"&&ok.Purpose=="Verify the project build"&&ok.ExitCode==0&&ok.Outcome=="Exited with code 0","direct command, explicit purpose, and successful result retained");Must(ok.Detail.Contains("Build completed successfully"),"command output detail retained");
            var bad=rootEvents.Single(e=>e.CallId=="root-bad");Must(bad.Command.Contains("danger")&&bad.ExitCode==7&&bad.Kind=="command_failed","raw orchestration command and nested nonzero status beat isError false");
            Must(!bad.Command.Contains("hidden-secret")&&!bad.Detail.Contains("abcdefghijk"),"API key and bearer token redacted");
            var batch=rootEvents.Single(e=>e.CallId=="root-batch");Must(batch.Command.Contains("first batch command")&&batch.Command.Contains("second batch command")&&batch.ExitCode==2&&batch.Kind=="command_failed","batched raw orchestration preserves all commands and aggregate failure");
            Must(rootEvents.Single(e=>e.CallId=="root-zeroerr").Kind=="tool_failed","stringified JSON error flag beats zero exit code");
            Must(rootEvents.Single(e=>e.CallId=="root-mixed").Kind=="tool_failed","mixed output statuses retain any nested failure");
            var statusMix=rootEvents.Single(e=>e.CallId=="root-status-mix");Must(statusMix.Kind=="tool_failed"&&statusMix.Outcome=="failed","later nested failure status overrides earlier completed status");
            var jsonStatus=rootEvents.Single(e=>e.CallId=="root-json-status");Must(jsonStatus.Kind=="tool_failed"&&jsonStatus.ExitCode==7&&jsonStatus.Outcome=="Exited with code 7","stringified JSON exit and failure status are parsed");
            Must(rootEvents.Single(e=>e.CallId=="root-aggregate").Detail.Contains("aggregated fixture output"),"aggregated command output is retained");
            Must(rootEvents.Single(e=>e.CallId=="root-stderr").Detail.Contains("stderr fixture output"),"nonempty stderr is retained when stdout is blank");
            Must(rootEvents.Single(e=>e.CallId=="root-purpose").Purpose=="Collect fixture evidence","explicit raw command justification is retained");
            var missing=rootEvents.Single(e=>e.CallId=="root-missing");Must(missing.Text=="Command finished; command text not recorded","missing command gets explicit fallback");
            var mcp=rootEvents.Single(e=>e.CallId=="root-mcp");Must(mcp.Tool=="local.fixture.lookup"&&mcp.Kind=="tool_failed"&&mcp.Text.Contains("local.fixture.lookup"),"MCP identifiers and failure retained");
            var rootCall=rootAgent.RecentEvents.Single(e=>e.CallId=="same-call");var childCall=childAgent.RecentEvents.Single(e=>e.CallId=="same-call");Must(rootCall.Command=="root-specific"&&rootCall.ExitCode==0&&childCall.Command=="child-specific"&&childCall.ExitCode==9,"call ids correlate within each agent");
            Must(snapshot.Events.Any(e=>e.Kind=="complete"&&e.Text.Contains("Root finished the local fixture run")),"task completion summarizes public final answer");
            var all=snapshot.Events.Concat(snapshot.Agents.SelectMany(a=>a.RecentEvents)).ToList();Must(all.All(e=>e.Command==null||e.Command.Length<=8000)&&all.All(e=>e.Detail==null||e.Detail.Length<=12000),"per-event rich strings stay bounded");
            Must(all.Sum(e=>(e.Command==null?0:e.Command.Length)+(e.Detail==null?0:e.Detail.Length))<=256*1024,"total snapshot rich details stay bounded");
            Must(all.Any(e=>(e.Command??"").Contains("truncated")||(e.Detail??"").Contains("truncated")),"oversized detail is visibly marked truncated");
        }
    }
    static Dictionary<string,object> Obj(params object[] args){var d=new Dictionary<string,object>();for(int i=0;i<args.Length;i+=2)d[(string)args[i]]=args[i+1];return d;}
    static string Envelope(string type,Dictionary<string,object> payload,string time){return Json.Serialize(Obj("type",type,"timestamp",time,"payload",payload));}
    static void Write(string path,IEnumerable<string> lines){File.WriteAllText(path,String.Join("\n",lines)+"\n",new UTF8Encoding(false));}
    static void Must(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
