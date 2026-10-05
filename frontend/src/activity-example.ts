import type {Activity,ActivityAgent,ActivityEvent,TeamSummary} from './types';
// Imported only in Vite development. Production builds remove this module.
const base=Date.now(),at=(seconds:number)=>new Date(base-seconds*1000).toISOString();
const agents:ActivityAgent[]=[
 {Id:'example-chat',ParentId:null,Name:'Sol',Role:'supervisor',Model:'gpt-6.1-sol',Effort:'high',State:'running',UpdatedUtc:at(3),Action:'Reviewing command results',Assignment:'Coordinate the Activity update and review the implementation.',Files:[],RecentEvents:[]},
 {Id:'example-investigator',ParentId:'example-chat',Name:'Investigator',Role:'investigator',Model:'gpt-6-luna',Effort:'medium',State:'complete',UpdatedUtc:at(12),Action:'Traced the event parser',Assignment:'Trace how command text and outcomes reach Activity.',Files:[{Path:'host/WorkspaceBackend.cs',Kind:'read'}],RecentEvents:[]},
 {Id:'example-implementer',ParentId:'example-chat',Name:'Implementer',Role:'implementer',Model:'gpt-6-luna',Effort:'medium',State:'running',UpdatedUtc:at(8),Action:'Checking the event fixtures',Assignment:'Preserve recorded commands and correlate tool outcomes.',Files:[{Path:'Laica.Observer.cs',Kind:'changed'},{Path:'checks/ObserverWorkTests.cs',Kind:'changed'}],RecentEvents:[]},
 {Id:'example-verifier',ParentId:'example-chat',Name:'Verifier',Role:'verifier',Model:'gpt-6-luna',Effort:'medium',State:'idle',UpdatedUtc:at(21),Action:'Inspected the guide button',Assignment:'Check layout, log filters and the graph context menu.',Files:[],RecentEvents:[]},
 {Id:'example-earlier',ParentId:'example-chat',Name:'Earlier investigator',Role:'investigator',Model:'gpt-6-luna',Effort:'medium',State:'complete',UpdatedUtc:at(320),Action:'Reviewed signing prerequisites',Assignment:'Earlier task in this chat.',Files:[],RecentEvents:[]},
];
const events:ActivityEvent[]=[
 {TimeUtc:at(3),AgentId:'example-chat',Kind:'tool_complete',Text:'Read the team configuration',Path:'',Tool:'laica.get_team',Outcome:'Team configuration returned',Detail:'mode: multi\nsupervisor: gpt-6.1-sol / high\nworkers: investigator, implementer, verifier'},
 {TimeUtc:at(8),AgentId:'example-implementer',Kind:'command_complete',Text:'Ran ObserverWorkTests.exe · exit 0',Path:'',Command:'.\\checks\\ObserverWorkTests.exe',Tool:'exec_command',Purpose:'Check command correlation, failure outcomes and credential redaction.',Outcome:'Process exited with code 0',ExitCode:0,Detail:'Observer work checks passed.'},
 {TimeUtc:at(12),AgentId:'example-investigator',Kind:'command_complete',Text:'Searched the Activity event serializer · exit 0',Path:'host/WorkspaceBackend.cs',Command:'rg -n "EventDto|ActivityDto" host/WorkspaceBackend.cs',Tool:'exec_command',Purpose:'Locate the fields returned to the Activity view.',Outcome:'Process exited with code 0',ExitCode:0,Detail:'260: static object EventDto(ActivityEvent e)\n261: static object ActivityDto(ActivitySnapshot a)'},
 {TimeUtc:at(21),AgentId:'example-verifier',Kind:'tool_complete',Text:'Captured the guide-button layout',Path:'',Tool:'browser.getScreenshot',Outcome:'Screenshot recorded'},
 {TimeUtc:at(39),AgentId:'example-implementer',Kind:'file_change_complete',Text:'Updated Laica.Observer.cs',Path:'Laica.Observer.cs',Tool:'apply_patch',Outcome:'File change recorded'},
 {TimeUtc:at(54),AgentId:'example-chat',Kind:'command_failed',Text:'Type check found one error',Path:'',Command:'pnpm check',Tool:'exec_command',Purpose:'Check the frontend types after the Activity extraction.',ExitCode:2,Outcome:'Process exited with code 2',Detail:'ActivityPage.tsx: selected agent may be undefined.'},
 {TimeUtc:at(320),AgentId:'example-earlier',Kind:'command_complete',Text:'Checked installed signing certificates · exit 0',Path:'',Command:'Get-ChildItem Cert:\\CurrentUser\\My',Tool:'exec_command',ExitCode:0,Outcome:'Process exited with code 0',Detail:'No code-signing identity was found in this fixture.'},
];
agents.forEach(a=>a.RecentEvents=events.filter(e=>e.AgentId===a.Id));
export const exampleActivity:Activity={SessionId:'example-chat',Project:'LAICA',Task:'Make agent work clear: preserve commands, outcomes and a readable team overview.',State:'running',Notice:'Example data',UpdatedUtc:at(3),Agents:agents,Events:events};
export const exampleTeams:TeamSummary[]=[
 {Id:'example-chat',Title:'Activity and graph improvements',Project:'LAICA',Task:'Show recorded agent work and command outcomes.',State:'running',Action:'Reviewing event fixtures',UpdatedUtc:at(3),ObservedUtc:at(3),RecentWorkers:2},
 {Id:'example-export',Title:'Export validation',Project:'Sample workspace',Task:'Check exported team settings survive reimport.',State:'complete',Action:'Export checks finished',UpdatedUtc:at(640),ObservedUtc:at(640),RecentWorkers:0},
 {Id:'example-service',Title:'Local service connection',Project:'LAICA',Task:'Inspect the model discovery response.',State:'idle',Action:'Waiting for the next task',UpdatedUtc:at(990),ObservedUtc:at(990),RecentWorkers:0},
];
export const exampleActivities:Record<string,Activity>={
 'example-chat':exampleActivity,
 ...Object.fromEntries(exampleTeams.slice(1).map(t=>[t.Id,{SessionId:t.Id,Project:t.Project,Task:t.Task,State:t.State,UpdatedUtc:t.ObservedUtc,Notice:'Example data',Agents:[{...agents[0],Id:t.Id,State:t.State,Action:t.Action,UpdatedUtc:t.ObservedUtc,Assignment:t.Task,RecentEvents:[]}],Events:[]}]))
};
