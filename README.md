<img src="docs/images/laica-dog.png" alt="LAICA dog icon" width="104">

# LAICA

**Light Weight AI Container for Agents**

A Windows workspace for arranging agent teams and following their work. Built by **Charlie Frater**.

LAICA brings the team graph, model choices, assignments and recorded activity into one place. The aim is simple: make it clear who is working on a task, what they have done, and what needs attention. The interface uses smoked glass, restrained purple accents and a space backdrop, while keeping commands and results readable.

**Status: 0.7.0 source preview.** The source is public and can be built locally. There is no trusted signed Windows release available yet. Unsigned local builds may be blocked by Smart App Control. Keep Windows security enabled; signing is still part of the release work.

**[Start here: step-by-step setup guide](docs/getting-started.md)** — get the source, build the app, select a team, connect Codex and read recorded work.

## Screenshots

Captured from the 0.7.0 frontend development preview. Activity uses example records; no live agents or private chats are shown.

**Teams — arrange the crew and inspect each agent's assignment.**

![LAICA Teams graph](docs/images/teams.jpg)

**Activity — see recent work across chats and inspect the team.**

![LAICA Activity work overview](docs/images/activity.jpg)

<details>
<summary>Recorded commands and model connections</summary>

**Recorded logs — expand a step to see its command, purpose and result.**

![LAICA recorded logs](docs/images/recorded-logs.jpg)

**Services — choose Codex or an OpenAI-compatible model connection.**

![LAICA Services](docs/images/services.jpg)

</details>

## What it does

- **Teams:** edit a node graph with one supervisor, worker assignments, reporting lines, model and reasoning choices. Save and switch team profiles.
- **Graph editing:** move nodes, pan and zoom, frame a selection, search, duplicate, undo and redo. Pull a wire from either socket and release on empty space to disconnect it.
- **Codex handoff:** select a team in LAICA, then ask Codex to use it. A small, read-only MCP bridge supplies the selected configuration. The task and final answer stay in your Codex chat.
- **Local services:** connect an OpenAI-compatible API, including loopback servers exposed by Ollama or LM Studio and remote HTTPS services. Run a text-based team and read its reviewed answer in LAICA.
- **Activity:** browse chat teams, see the supervisor and three recent workers, and inspect commands, files, browser/computer actions and handoffs when those actions are recorded.
- **Recorded logs:** filter by agent or type of work, search commands and results, and expand a step for the recorded command, purpose, output and outcome.

The Activity overview displays up to four recent agent cards. Earlier agent threads remain available in logs. This presentation limit does not delete history or prevent a Codex host from creating more threads over a chat's lifetime. Reuse existing workers where possible; the selected team asks for at most three concurrent workers.

LAICA groups agents in a workspace. Execution, file access and sandbox permissions come from the host running the agents. It does not grant additional computer access.

## Build and run

The desktop host currently supports **Windows x64**. You need:

- Node.js **22.13 or newer**, and pnpm **11.19.0**.
- The Windows .NET Framework C# compiler at `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`.
- Microsoft's [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/).
- An installed, signed-in Codex CLI/app for Codex integration, or a running local API service for local teams.

From PowerShell:

```powershell
git clone https://github.com/Blacksheep909/LAICA.git
cd LAICA
npm install --global pnpm@11.19.0
./Build.ps1 -Development
./Test.ps1
./build/LAICA.exe
```

The build uses the pinned frontend lockfile and the three vendored WebView2 SDK files. SDK hashes are checked before compilation. Generated files go under `build/`, `frontend/dist/` and `checks/bin/`; they are ignored by Git.

If Node or pnpm is outside PATH, pass `-NodePath` and `-PnpmPath` to `Build.ps1`. `PnpmPath` can point to `pnpm.cmd` or the package's JavaScript entry point.

To install an explicitly unsigned local build with Desktop and Start menu shortcuts:

```powershell
./Install.ps1 -Development
```

The per-user program location is `%LOCALAPPDATA%\Programs\LAICA`. The executable has the stable name **LAICA.exe**; the version is recorded in its file metadata and in the app. Installation never edits Codex configuration or restarts Codex. Workspace data stays separate from program files.

The installer checks the payload against its hash manifest. Updates make a verified recovery backup and remove obsolete files recorded by the previous installation. Files outside that managed inventory are preserved. Close the installed app and its bridge before updating.

## Connect Codex

1. Build or install LAICA, then open it.
2. Under **Services**, refresh models. Choose models and reasoning settings actually available to your account. The starter graph is a draft, and its example model IDs may need changing.
3. In **Teams**, edit the supervisor and workers, save a profile if wanted, and choose **Use this team**.
4. Register `LAICA.Bridge.exe` as a stdio MCP server. For the default installation:

```powershell
codex mcp add laica -- "$env:LOCALAPPDATA\Programs\LAICA\LAICA.Bridge.exe"
codex mcp list
```

5. Reopen Codex as needed to load the MCP server, start a chat, and ask: **“Use my LAICA team for this task: …”**

The supervisor model and reasoning setting must match the active Codex chat. Worker models and delegation tools must also be available to that host. LAICA reports the requested team; the host remains responsible for applying it. Configure Codex's own delegation setting in Codex, then restart it manually and use a fresh chat when required.

The bridge exposes only `get_team` and `get_activity`. See the [official MCP configuration documentation](https://learn.chatgpt.com/docs/extend/mcp?surface=cli) for stdio server options. Custom `LAICA_HOME` and `CODEX_HOME` values must be consistent in the GUI and bridge processes.

## Run a local team

1. Start your model application's OpenAI-compatible API server.
2. In **Services**, add its loopback base URL and refresh the model list.
3. Apply a discovered model to the team, then refine each agent's model and job in the inspector.
4. Enter a task in the Input stage and choose **Run team**.
5. Follow the result under **Activity → Local runs**. Stop cancels the current run.

Runs started in LAICA send text context to the chosen API. HTTP is allowed only for loopback addresses; remote services require HTTPS. They do not give those models browser or computer-use tools. The adapter uses compatible model-list and chat-completions endpoints; compatibility with every server/version has not been established.

## Reading activity

The work overview and recorded logs use the same retained evidence. A command is shown when its text is available; a purpose is shown only when explicitly recorded. Missing exit status, output or command text is labelled rather than guessed. New activity is polled, so the view can arrive a little after the action.

“No recent update” means the recorded log has been quiet for over a minute. It does not prove an agent has stopped. The observer retains a bounded history, and some tools or record formats may not expose enough detail for a useful description.

Browser development previews contain clearly labelled example activity. Those examples are excluded from the compiled desktop UI.

## Data and privacy

Profiles, selected teams and local service settings are stored in `%LOCALAPPDATA%\LAICA\workspace`. Set `LAICA_HOME` to override that directory. `--data-dir` is available for explicit test workspaces; use the same override for the GUI and bridge.

Activity reads local Codex records under `CODEX_HOME`, defaulting to `%USERPROFILE%\.codex`. LAICA has no analytics endpoint or background upload of those records. Obvious credential patterns are redacted in retained event details, but logs can still contain private commands, file paths and conversation content. Review anything you export or post in an issue.

Local service keys are protected with Windows DPAPI for the current user. A configured API service receives the task context needed for a run. Choose a loopback service for local processing, and check its own forwarding behavior. See [Privacy and data flow](docs/privacy.md) for the exact boundaries.

## Development and contributions

```powershell
cd frontend
pnpm install --frozen-lockfile --ignore-scripts
pnpm dev
```

The browser preview is for frontend work. The desktop app is required to test saved profiles, the C# backend and real activity records. See [Architecture](docs/architecture.md), [Contributing](CONTRIBUTING.md) and [Release signing](docs/releases.md).

Tests cover backend persistence, local API contracts, cancellation/recovery, graph connections and cycles, observer event parsing, privacy filters, MCP bridge behavior and signing rejection. Local API tests use controlled loopback fixtures; they are not a test of every live model service.

Useful next work includes wider native interaction testing, more record-format coverage, account/model compatibility, an accessible installer, and a trusted signed Windows release. Please report a concrete trigger, expected result and actual result, with sensitive details removed.

## License and credits

LAICA is released under the [MIT License](LICENSE), copyright **Charlie Frater**. The interface uses [Open Glass UI](https://github.com/moekoelueker/open-glass-ui). Other dependencies and their notices are listed in [Third-party notices](THIRD_PARTY_NOTICES.md).

LAICA is an independent project. OpenAI, Microsoft and the local model providers do not maintain or endorse it.
