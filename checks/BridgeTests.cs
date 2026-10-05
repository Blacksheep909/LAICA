using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

// Runs the actual standalone bridge process, passing its executable path on the command line.
static class BridgeTests
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static int Main(string[] args)
    {
        try { return RunAll(args); }
        catch(Exception ex) { Console.Error.WriteLine("Bridge test failed: "+ex.Message); return 1; }
    }
    static int RunAll(string[] args)
    {
        if(args.Length!=1 || !File.Exists(args[0])) throw new Exception("Pass the built LAICA.Bridge.exe path.");
        string dir=Path.Combine(Path.GetTempPath(),"laica-bridge-test-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        string originalCodexHome=Environment.GetEnvironmentVariable("CODEX_HOME");
        string originalLaicaHome=Environment.GetEnvironmentVariable("LAICA_HOME");
        try
        {
            string exe=Path.Combine(dir,"LAICA.Bridge.exe"); File.Copy(args[0],exe);
            string bridgeData=Path.Combine(dir,"bridge-data");Directory.CreateDirectory(bridgeData);
            string codexHome=Environment.GetEnvironmentVariable("CODEX_HOME"); if(String.IsNullOrWhiteSpace(codexHome))codexHome=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
            string config=Path.Combine(codexHome,"config.toml"); string configBefore=HashFile(config);
            string fixtureCodexHome=Path.Combine(dir,"codex-home"); Directory.CreateDirectory(fixtureCodexHome);
            string fixtureConfig=Path.Combine(fixtureCodexHome,"config.toml"); File.WriteAllText(fixtureConfig,"model = \"fixture-codex-model\"\r\n\r\n[agents]\r\nenabled = true\r\n",new UTF8Encoding(false));
            string fixtureConfigHash=HashFile(fixtureConfig); Environment.SetEnvironmentVariable("CODEX_HOME",fixtureCodexHome);
            string clean="{\"Version\":1,\"Goal\":\"test\",\"Nodes\":[{\"Id\":\"root\",\"ParentId\":null,\"Name\":\"Supervisor\",\"Role\":\"supervisor\",\"Model\":\"fixture-root-model\",\"Effort\":\"low\",\"ConnectionId\":\"codex\",\"Job\":\"review\"},{\"Id\":\"worker_1\",\"ParentId\":\"root\",\"Name\":\"Implementer\",\"Role\":\"implementer\",\"Model\":\"fixture-worker-model\",\"Effort\":\"medium\",\"ConnectionId\":\"codex\",\"Job\":\"bounded task\"},{\"Id\":\"api\",\"ParentId\":\"root\",\"Name\":\"API node\",\"Role\":\"worker\",\"Model\":\"model-x\",\"Effort\":\"medium\",\"ConnectionId\":\"api-1\",\"Job\":\"unsupported\"}]}";
            File.WriteAllText(Path.Combine(bridgeData,"active-team.json"),clean,new UTF8Encoding(false));
            object[] replies=Run(exe,new[]{Req(1,"initialize","{\"protocolVersion\":\"2025-03-26\"}"),Req(2,"tools/list","{}"),Req(3,"tools/call","{\"name\":\"get_team\",\"arguments\":{}}"),Req(4,"tools/call","{\"name\":\"get_activity\",\"arguments\":{\"session_id\":\"../../bad\"}}"),"{bad json",Req(5,"unknown/method","{}"),"{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"});
            Check(replies.Length==6,"notification ignored and six request replies returned");
            Check(Get(replies[0],"result")!=null && Text(Get(replies[0],"result"),"protocolVersion")=="2025-03-26","initialize negotiates supported version"); Check(Text(Get(Get(replies[0],"result"),"serverInfo"),"version")=="0.7.0","bridge reports public runtime version");
            object list=Get(replies[1],"result"); IList tools=Get(list,"tools") as IList; Check(list!=null && tools!=null && tools.Count==2,"only two read-only tools exposed");
            object call=Get(replies[2],"result"); IList content=Get(call,"content") as IList; string teamText=Text(content[0],"text"); object team=Json.DeserializeObject(teamText);
            Check(Text(team,"mode")=="solo"||Text(team,"mode")=="multi"||Text(team,"mode")=="unknown","mode read-only state returned");
            IList nodes=Get(team,"nodes") as IList; Check(nodes.Count==3 && Text(nodes[1],"id")=="worker_1","selected team nodes returned");
            Check(Text(nodes[2],"execution")=="unsupported_service","API node identified as unsupported");
            string instructions=Text(team,"native_instructions"); Check(instructions.Contains("Effective solo mode suppresses all service workers") && instructions.Contains("native Codex solo mode disables native delegation") && instructions.Contains("current supervisor") && instructions.Contains("matching model and effort"),"native policy is model-neutral and includes effective solo constraints");
            Check(Text(team,"supervisor_model")=="fixture-root-model" && Text(team,"supervisor_effort")=="low" && Equals(Get(team,"requires_matching_codex_chat"),true),"discovered supervisor choice and active-chat matching requirement are preserved");
            Check(HashFile(config)==configBefore,"Codex config hash remains unchanged");
            Check(Get(replies[3],"error")!=null,"path-like session id rejected"); Check(Get(replies[4],"error")!=null,"malformed JSON reported"); Check(Convert.ToInt32(Get(Get(replies[5],"error"),"code"))==-32601,"unknown method reported");
            File.WriteAllText(Path.Combine(bridgeData,"active-team.json"),clean.Replace("worker_1","../escape"),new UTF8Encoding(false));
            object invalid=Run(exe,new[]{Req(10,"tools/call","{\"name\":\"get_team\",\"arguments\":{}}")})[0]; Check(Get(invalid,"error")!=null,"unsafe agent id rejected");
            File.WriteAllText(Path.Combine(bridgeData,"active-team.json"),"{broken",new UTF8Encoding(false));
            invalid=Run(exe,new[]{Req(11,"tools/call","{\"name\":\"get_team\",\"arguments\":{}}")})[0]; Check(Get(invalid,"error")!=null,"malformed plan rejected clearly");
            File.WriteAllText(Path.Combine(bridgeData,"active-team.json"),clean,new UTF8Encoding(false));
            object multiReply=Run(exe,new[]{Req(12,"tools/call","{\"name\":\"get_team\",\"arguments\":{}}")})[0]; Check(Get(multiReply,"result")!=null,"valid selected team remains readable");
            string custom=clean.Replace("fixture-root-model","fixture-alternate-model").Replace("\"Effort\":\"low\"","\"Effort\":\"medium\"");
            File.WriteAllText(Path.Combine(bridgeData,"active-team.json"),custom,new UTF8Encoding(false));
            object customReply=Run(exe,new[]{Req(13,"tools/call","{\"name\":\"get_team\",\"arguments\":{}}")})[0];
            IList customContent=Get(Get(customReply,"result"),"content") as IList; object customTeam=Json.DeserializeObject(Text(customContent[0],"text"));
            Check(Text(customTeam,"supervisor_model")=="fixture-alternate-model" && Text(customTeam,"supervisor_effort")=="medium","alternate discovered-style native supervisor model and effort are accepted and preserved");
            string apiRoot=custom.Replace("\"ConnectionId\":\"codex\"","\"ConnectionId\":\"api-1\"");
            File.WriteAllText(Path.Combine(bridgeData,"active-team.json"),apiRoot,new UTF8Encoding(false));
            object apiRootReply=Run(exe,new[]{Req(14,"tools/call","{\"name\":\"get_team\",\"arguments\":{}}")})[0]; Check(Get(apiRootReply,"error")!=null,"API root rejected pending service adapter");
            Check(HashFile(config)==configBefore,"custom team lookup leaves Codex config hash unchanged");
            File.WriteAllText(Path.Combine(bridgeData,"active-team.json"),clean,new UTF8Encoding(false));
            string selectedTeam=Path.Combine(bridgeData,"active-team.json"), selectedTeamHash=HashFile(selectedTeam);
            string modePath=Path.Combine(bridgeData,"harness-mode.json");
            File.WriteAllText(modePath,"{\"Mode\":\"solo\"}",new UTF8Encoding(false));
            object modeTeam=GetTeam(exe,15); Check(Text(modeTeam,"native_mode")=="multi"&&Text(modeTeam,"harness_mode")=="solo"&&Text(modeTeam,"mode")=="solo","harness solo overrides native multi (native="+Text(modeTeam,"native_mode")+", harness="+Text(modeTeam,"harness_mode")+", effective="+Text(modeTeam,"mode")+")");
            File.WriteAllText(modePath,"{\"Mode\":\"multi\"}",new UTF8Encoding(false));
            modeTeam=GetTeam(exe,16); Check(Text(modeTeam,"native_mode")=="multi"&&Text(modeTeam,"harness_mode")=="multi"&&Text(modeTeam,"mode")=="multi","harness and native multi enable effective multi");
            File.Delete(modePath);
            modeTeam=GetTeam(exe,17); Check(Text(modeTeam,"native_mode")=="multi"&&Text(modeTeam,"harness_mode")=="multi"&&Text(modeTeam,"mode")=="multi","missing harness mode falls back to native mode");
            File.WriteAllText(modePath,"{broken",new UTF8Encoding(false));
            modeTeam=GetTeam(exe,18); Check(Text(modeTeam,"native_mode")=="multi"&&Text(modeTeam,"harness_mode")=="unknown"&&Text(modeTeam,"mode")=="unknown","malformed harness mode fails closed");
            File.WriteAllText(modePath,new string('x',4097),new UTF8Encoding(false));
            modeTeam=GetTeam(exe,19); Check(Text(modeTeam,"harness_mode")=="unknown"&&Text(modeTeam,"mode")=="unknown","oversized harness mode fails closed");
            Check(HashFile(fixtureConfig)==fixtureConfigHash&&HashFile(config)==configBefore,"mode reads leave native Codex config hashes unchanged");
            Check(HashFile(selectedTeam)==selectedTeamHash,"mode reads leave selected team hash unchanged");
            // The same bridge default follows LAICA_HOME when no explicit CLI override is supplied.
            string envData=Path.Combine(dir,"env-data");Directory.CreateDirectory(envData);File.WriteAllText(Path.Combine(envData,"active-team.json"),clean,new UTF8Encoding(false));
            Environment.SetEnvironmentVariable("LAICA_HOME",envData);
            object envTeam=GetTeam(exe,20, "");Check(Text(envTeam,"supervisor_model")=="fixture-root-model","LAICA_HOME supplies the bridge default workspace");
            Console.WriteLine("Bridge tests passed."); return 0;
        }
        finally { Environment.SetEnvironmentVariable("CODEX_HOME",originalCodexHome); Environment.SetEnvironmentVariable("LAICA_HOME",originalLaicaHome); try { Directory.Delete(dir,true); } catch { } }
    }
    static object GetTeam(string exe,int id,string dataDir=null){object reply=Run(exe,new[]{Req(id,"tools/call","{\"name\":\"get_team\",\"arguments\":{}}")},dataDir)[0];Check(Get(reply,"error")==null,"get_team mode fixture succeeds");IList content=Get(Get(reply,"result"),"content") as IList;return Json.DeserializeObject(Text(content[0],"text"));}
    static string Req(int id,string method,string p){return "{\"jsonrpc\":\"2.0\",\"id\":"+id+",\"method\":\""+method+"\",\"params\":"+p+"}";}
    static object[] Run(string exe,string[] lines,string dataDir=null)
    {
        var si=new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,StandardOutputEncoding=Encoding.UTF8};
        if(dataDir==null)dataDir=Path.Combine(Path.GetDirectoryName(exe),"bridge-data");
        if(dataDir.Length>0)si.Arguments="--data-dir \""+dataDir+"\"";
        using(var proc=Process.Start(si)){foreach(string l in lines)proc.StandardInput.WriteLine(l);proc.StandardInput.Close();string output=proc.StandardOutput.ReadToEnd();string error=proc.StandardError.ReadToEnd();proc.WaitForExit();if(proc.ExitCode!=0)throw new Exception("Bridge failed: "+error);var result=new System.Collections.Generic.List<object>();using(var reader=new StringReader(output)){string line;while((line=reader.ReadLine())!=null)result.Add(Json.DeserializeObject(line));}return result.ToArray();}
    }
    static object Get(object o,string k){var d=o as System.Collections.Generic.Dictionary<string,object>;object v;return d!=null&&d.TryGetValue(k,out v)?v:null;}
    static string Text(object o,string k){return Get(o,k) as string;}
    static string HashFile(string path){if(!File.Exists(path))return null;using(var sha=SHA256.Create())using(var stream=File.OpenRead(path)){return Convert.ToBase64String(sha.ComputeHash(stream));}}
    static void Check(bool ok,string message){if(!ok)throw new Exception("Failed: "+message);}
}
