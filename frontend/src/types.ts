export interface AgentNode { Id:string; ParentId:string|null; Name:string; Role:string; Model:string; Effort:string; Job:string; ConnectionId:string; CanEdit?:boolean; X:number; Y:number }
export interface AgentPlan { Version:number; Goal:string; Nodes:AgentNode[]; Stages:{Id:string;X:number;Y:number}[]; HiddenStageLinks:{FromId:string;ToId:string}[] }
export interface Model { Id:string; Name:string; ConnectionId:string; Efforts:string[] }
export interface Service { Id:string; Name:string; BaseUrl:string; Provider:string; KeyEnvironmentVariable:string; HasKey:boolean }
export interface Profile { Id:string; Name:string; Mode:string; SnapToGrid:boolean; Plan:AgentPlan }
export interface Session { Id:string; Title:string; Project:string; UpdatedUtc:string }
export interface ActivityEvent { TimeUtc:string; AgentId:string; Kind:string; Text:string; Path:string; CallId?:string; Tool?:string; Command?:string; Purpose?:string; Outcome?:string; Detail?:string; ExitCode?:number|null }
export interface ActivityAgent { Id:string; ParentId:string|null; Name:string; Role:string; Model:string; Effort:string; State:string; Action:string; Assignment:string; StartedUtc?:string; UpdatedUtc?:string; Files:{Path:string;Kind:string}[]; RecentEvents:ActivityEvent[] }
export interface Activity { SessionId:string; Project:string; Task:string; State:string; Notice:string; UpdatedUtc?:string; Agents:ActivityAgent[]; Events:ActivityEvent[] }
export interface TeamSummary extends Session { Task:string; State:string; Action:string; ObservedUtc:string; RecentWorkers?:number }
export interface WorkspaceState { Plan:AgentPlan; Models:Model[]; Services:Service[]; Profiles:Profile[]; SelectedProfileId:string|null; Mode:string; SnapToGrid:boolean; Busy:boolean; Refreshing:boolean; Answer:string; Status:string; Version:string; WorkingDirectory:string; RunEvents:ActivityEvent[]; NodeStates:Record<string,string> }
