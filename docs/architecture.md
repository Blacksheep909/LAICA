# Architecture

LAICA is a native Windows program with a web interface inside it. There is no server and no database: everything is local files and child processes.

```
 ┌────────────────────────── LAICA.exe (WinForms, .NET Framework 4) ───────────────────────────┐
 │  GlassHost            window, WebView2, file pickers, dictation, window effects              │
 │  WorkspaceBackend     one command table: every UI action is a named command + JSON payload   │
 │  HarnessManager       sessions, agent processes, teams, usage, handoff, analytics, plugins   │
 │  RemoteServer         optional HTTP + Server-Sent Events + bearer token (off by default)     │
 └───────────────▲──────────────────────────────────────────────────────────▲──────────────────┘
                 │ WebView2 message bridge                                  │ child processes
 ┌───────────────┴────────────┐                      ┌──────────────────────┴───────────────────┐
 │ ui/  React + TypeScript    │                      │ codex · claude · gemini · custom CLIs     │
 │ (Vite build, local files)  │                      │ git · winget · npm                        │
 └────────────────────────────┘                      └───────────────────────────────────────────┘
```

## Layout

| Path | What is there |
| --- | --- |
| `core/` | Shared C#: runtime, services (API connections), profiles, the observer that reads Codex work, paths, and the read-only MCP bridge (`Laica.Bridge.cs`). |
| `host/` | The desktop host and backend. `HarnessManager.cs` is the centre; the rest of its partial class is split by feature: `Teams`, `Usage`, `PlanUsage`, `Analytics`, `Handoff`, `CoAuthor`, `Pause`, `Attach`, `Plugins`, `History`, `Workflows`, `Channels`, `Extensions`, `CliNodes`, `BuiltinAgent`, `GitTools`. `WorkspaceBackend.cs` maps command names to calls. |
| `frontend/` | The interface: React 19, Vite, TypeScript, Radix primitives and `cmdk` behind a small UI kit (`src/ui/kit.tsx`), plus the design layers (`Liquid.css`, `lens.ts`). |
| `checks/` | C# check programs for the backend, harness, channels, remote server and observer, plus signing checks. Frontend model tests are in `frontend/tests/`. |
| `vendor/webview2/` | The three pinned WebView2 SDK binaries, hash-checked at build time. |
| `licenses/` | Licence texts for bundled third-party code. |
| `tools/` | `Package.ps1` and `Setup.cs`, which make the zip and the installer. |

The build uses only the C# compiler that ships with Windows (`csc.exe`, C# 5) and Node for the frontend. There is no solution file; `Build.ps1` lists the sources.

## How an action flows

1. The UI calls `request('name', payload)` (`frontend/src/bridge.ts`).
2. In the desktop app that is a WebView2 message to `GlassHost`; in a browser (remote access) it is an HTTP request to `RemoteServer`. Both land in `WorkspaceBackend.Invoke`, a single `switch` over command names.
3. The backend calls into `HarnessManager`, which returns plain dictionaries and arrays serialised as JSON.
4. Live changes (new messages, usage, team progress) are pushed back as events: WebView2 messages locally, Server-Sent Events remotely.

## Agents are child processes

A chat is a `Session` with a working folder and a harness definition (executable, arguments, parser). Each turn starts the agent CLI with its streaming-JSON output mode, parses its output into events (assistant text, tool use, tool results, errors, limits) and stores them in `harness\sessions\<id>.json`. There are dedicated parsers for Codex and Claude Code; Gemini and other CLIs use the plain-text mode. The built-in "LAICA agent" is a small tool-using loop for API models.

Pausing suspends the process tree (`NtSuspendProcess`); stopping kills it.

## Teams

`Teams.cs` runs a team in a background thread: ask the leader for a JSON plan, create one session per task (optionally in its own git worktree), send each its brief, wait for all, then ask the leader to review. Fallback (`Usage.Fallback`, `MemberWait`, `AskLeader`) swaps in another available agent when a limit error appears. Each finished run is appended to `teamruns.json` with every member's vendor, model, time, tokens and estimated cost, which is what the Teams analytics read.

## Usage, plan windows and analytics

- `Usage.cs` counts tokens per vendor as results stream in, detects limit messages and parks a vendor until it resets.
- `PlanUsage.cs` reads Codex's `rate_limits` records (5-hour and weekly percentages) and Claude's token totals from their local logs. It does not call any vendor API and reads no credentials.
- `Analytics.cs` scans `~/.codex/sessions/**/rollout-*.jsonl` and `~/.claude/projects/*/*.jsonl` in the background, summarises each file (per day, per model, per hour, tools), caches the summary in `harness\analytics.json` and only re-reads files whose size or timestamp changed. Costs are computed from the per-day, per-model token split and an editable price table (`prices.json`).

## Computer use

`core/Laica.Computer.cs` builds `LAICA.Computer.exe`, a stdio MCP server (JSON-RPC, one message per line) with tools for screenshots, mouse and keyboard. It reads `harness\computer-use.json` before every call: `Enabled` (set by the Settings switch), `Epoch` (raised each time the user switches it on) and `StoppedEpoch` (set to `Epoch` when the user presses Esc, which blocks the agent until the next switch-on). While it acts it shows a click-through, always-on-top layered window with a lavender outline (excluded from screen capture) and listens for a real Esc key press with a low-level keyboard hook (presses it injected itself are ignored). The desktop app watches the file and stops any busy chat that was using the server when an Esc is recorded.

## Co-author hook

`CoAuthor.cs` writes small shell hooks into `harness\hooks`. When LAICA starts an agent in a git repository that does not set its own `core.hooksPath`, it passes `GIT_CONFIG_COUNT/KEY/VALUE` so git uses that folder, and `LAICA_COAUTHOR` with the trailer value. The `commit-msg` hook first runs the repository's own hook of the same name, then adds the trailer with `git interpret-trailers --if-exists addIfDifferent`, so existing vendor trailers are kept and nothing is duplicated.

## Interface

The UI is a single-page app loaded from local files with a strict Content-Security-Policy (no remote scripts). Glass is applied only to the floating control layer; content stays flat. The refraction effect is an SVG displacement filter used inside `backdrop-filter`, which is Chromium-only (WebView2 is Chromium), generated in `lens.ts`.

## Data on disk

Everything is under the install folder's `harness\` directory (and a WebView2 cache under `%LOCALAPPDATA%\LAICA`). See [privacy](privacy.md) for the full list.
