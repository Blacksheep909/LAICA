# Features

Everything LAICA does as of version 0.17.0, grouped by area. The [changelog](../CHANGELOG.md) lists what arrived when.

## Agents and chats

- **Many vendors, one place.** Codex, Claude Code and Gemini CLI are detected automatically, including when installed with npm, bun or scoop. Add any other command-line agent as a custom agent. API models (OpenAI-compatible and others) connect through your own keys under Settings > Services.
- **Your history, in sync.** LAICA reads the local history of Codex and Claude Code and shows it with the same project folders and thread titles. Claude chats use the Claude desktop app's real titles and folders. It rescans on every launch and when the window regains focus.
- **Recent.** A Recent section lists your six most recently active chats from every project (shown again in their project, like Codex), and every list is ordered by real last activity.
- **Projects.** Pin projects and chats, rename, close (closed chats are restorable for 14 days) and search. Scratch-folder chats do not turn into fake projects.
- **Per-chat drafts.** Each chat keeps its unsent text. Up-arrow recalls your last message, Esc stops a running turn, and messages typed while an agent is busy are queued.
- **Composer.** Attach files and folders (button, drag-and-drop or Ctrl+V for images), sketch, **Plan mode**, **Goal**, dictation by microphone (Windows speech recognition, offline), copy buttons on messages and code, and a Markdown export of any chat.
- **Pause and resume.** Suspend a running agent (and a whole team) without losing its work.
- **Resizable sidebar:** drag its edge from 320 px out to 460 px; double-click to reset.
- **Command palette (Ctrl+K)**, keyboard shortcuts (Ctrl+N, Ctrl+L, Ctrl+F, Ctrl+1 to 9, Ctrl+,) and **undo (Ctrl+Z)** across renames, pins, closes and project removal.

## Seeing what happened

- **Plain-sentence activity.** Steps read like "Ran 2 commands (1 failed)" and expand to the code and output. A live working line shows what the agent is doing now, with an animated glyph and a work timer in the sidebar.
- **Files changed.** Each turn ends with per-file cards (+/- lines) and a summary pill. The Changes panel shows full diffs and lets you commit.
- **Git worktrees.** A chat or teammate can run in its own worktree and branch so parallel agents never collide, then be merged back.

## Teams

- A **leader** plans, **teammates** (up to eight) work in parallel in their own chats, the leader reviews and writes the answer.
- **Every member can be a different vendor and model.** Mix Codex, Claude Code, Gemini and API models freely, and pin a model per teammate.
- **Seamless fallback.** If an agent runs out of usage mid-run, its task moves to another agent that still has usage, with a note to continue rather than restart. A leader that runs out is replaced the same way.
- **Pause, stop, run again** from the team page. The final result and each task's output are kept.

## Usage, limits and handoff

- **Usage meter** at the bottom right for every vendor: Codex 5-hour and weekly plan percentages read from its local logs, Claude token totals for today and this week, optional token budgets, and warnings as you approach a limit.
- **Limit detection** recognises "usage limit" errors from each vendor and parks that vendor until it resets.
- **Workflow handoff** (Settings > Agents or the workflow designer): when a vendor runs out, hand the chat or team to a backup team that does not rely on that vendor, to a single agent, or choose manually. Work moves at most twice so it cannot loop.

## Tag-team continuity

Two agents from different vendors can work one chat in turns, over the same project folder, one at a time.

- **Start one** from the model picker (for example *Codex + Claude Code*), or switch an open chat with the **Tag-team** pill. Modes: *Off*, *Assisted* (the default: LAICA keeps `HANDOFF.md` up to date, you hand over by hand) and *Automatic*. Settings > Agents > Continuity sets the defaults.
- **Handing over.** When the working agent runs out of usage LAICA hands the work to the other one by itself, with only what it has not seen: your latest request, the other agent's last message, the files changed, the commands run, and any file that was being edited when the agent stopped (marked *verify first*). Each agent resumes its own earlier session with its own CLI. The first time an agent joins it gets a fuller briefing from `HANDOFF.md`. If it cannot resume, it is started fresh from the notes.
- **Handing back.** When the first agent has reset (LAICA uses the reset times in the limit message and in the plan windows it already reads), the work goes back, as many times as it takes. You choose the return threshold, whether to also return at a task boundary, and a minimum gap between optional switches.
- **Both out of usage.** The chat pauses, tells you when it will carry on, and resumes by itself when the first agent resets.
- **Loop guard.** Switching happens only between turns. If there are too many switches in a short time without any file changing, automatic switching stops and asks you. **Switch now** carries on.
- **No model needed.** The handoff is built by LAICA from the chat's event log and the folder, so it works when an agent has no usage left. Secrets are redacted. Without git, file times are used; LAICA never runs `git init`.
- **HANDOFF.md** has a managed block (goal, stage, done, next steps, files touched, verify-first, recent commands, constraints, who updated it) and a **Pinned** section that is yours and never overwritten. The previous copy is kept as `HANDOFF.prev.md`. The workspace panel's **Handoff** tab shows it.
- **Cost.** Hover the header chip to see what the next switch would send and its estimated cost. Every switch is logged with its estimated (and, when known, actual) cost.
- The agent that receives the work runs in its normal permission mode with the normal approval prompts. Workflow-designer nodes stay read-only unless you set a node to *Can edit*.
- Not covered: two agents editing at once, file locks, and Fusion (cloud designs are not isolated by worktrees, so let only one agent drive Fusion at a time).
## Analytics

Open it from the chart icon at the bottom left. Tabs for All, Codex and Claude; ranges All, 30d and 7d.

- **Overview:** sessions, messages, tokens, estimated cost, active days, peak hour, favourite model, streaks, busiest day, a 26-week heatmap, the split between vendors and most-used tools.
- **Charts:** tokens per day by vendor, estimated cost per day, running total, messages by weekday and hour, share by model.
- **Models:** per model messages, tokens and estimated cost.
- **Teams:** for every team you have run, success rate, average time and cost per run, cost per task, parallel speedup (work time versus clock time), leader overhead, agent swaps, a per-teammate table (agent, time, tokens, cost, share) and recent runs, with badges for the most reliable, fastest and cheapest designs.
- **Prices:** the cost estimate uses public API list prices per model family (input, output, cache read and cache write per million tokens). Edit them here. The estimate is not a bill; flat plans cost what the plan costs.

The index is read once in the background, cached on disk and only re-read for logs that changed.

## Plugins

- A **plugin library** with MCP servers, skills, agent CLIs and runtimes (Node.js, uv), installable in one click through winget or npm.
- **Suggested for this computer:** LAICA looks for installed programs (Blender, VS Code, browsers, ...) and suggests matching plugins first.
- Installed MCP servers and skills appear in the composer's **+** menu.

## Automation and reach

- **Scheduled tasks** run an agent or team on a timetable.
- **Workflow designer** for multi-step graphs of agents.
- **Assistants** are reusable agent presets.
- **Channels:** Telegram is two-way (pair a chat with a one-time code and talk to an agent from your phone). Slack, Discord, Lark, DingTalk, WeCom and generic webhooks receive a notification when a scheduled task or team finishes.
- **Remote access** (off by default) serves the same interface to a browser on your network behind a password, over Server-Sent Events.
- A read-only **Codex bridge** (`LAICA.Bridge.exe`, an MCP server) lets Codex read LAICA's observed work.

## Computer use and the Screen tab

- **Screen tab.** When an agent uses a browser (Playwright, Chrome DevTools or other browser tools) or LAICA computer use, the panel on the right of the chat gets a **Screen** tab. It shows the latest picture the agent saw, in a slim lavender frame while it is live, a Stop button and the steps it took (opened a page, clicked, typed, scrolled). It opens by itself when an agent starts working in a browser.
- **LAICA computer use.** A small MCP server (`LAICA.Computer.exe`) that lets Claude Code and Codex take screenshots of your main screen and use the mouse and keyboard. **Off by default**: switch it on under Settings > Agents > Computer use, which also registers it with the agents you have installed. While an agent is acting a slim lavender outline is drawn around your screen, and **pressing Esc stops it at once**; the agents using it are stopped and it stays blocked until you switch it on again. Esc, the Windows key and Alt+F4 can never be pressed by an agent. Only the main screen is shown and controlled.

## Pictures and prices

- **Pictures.** Screenshots and images an agent sees or makes (Claude tool results, MCP image content, Codex image items) are stored with the chat and shown as thumbnails on the activity line. Click one for a full-size viewer (arrows to move, Esc to close).
- **Verified prices.** Cost estimates use the exact per-model prices from Anthropic's, OpenAI's and Google's own pricing pages, read every time LAICA starts. The Analytics Prices tab shows when each vendor was last checked and lists the verified models; an editable family table covers models a vendor doesn't list.

## Updates

- **LAICA** checks GitHub for a newer release every few hours. A pill in the sidebar footer offers the update; clicking it downloads the installer, verifies its SHA-256 against the release's sums file, installs over the current folder and restarts. Nothing installs without a click, and Settings > Agents > Updates switches the automatic checks off.
- **MCP servers** installed for Claude Code or Codex through npx or uvx are set to fetch their latest version each time they start (`pkg@latest`). LAICA does this once a day and on Check now, backs up the config files first and leaves servers you pinned to a version alone. New installs from the plugin library already use `@latest`.

## Commits and credit

- Commits made by agents through LAICA, and commits made from the Changes panel, get `Co-authored-by: LAICA <email>`. It is added by a git hook that first runs the repository's own hook of the same name. Repositories that set their own `core.hooksPath` are left untouched. On by default; switch it off globally or per project in Settings > Agents > Commits. See [getting started](getting-started.md#8-laica-as-a-co-author) for choosing an email GitHub will show an avatar for.

## Look and feel

- A Liquid Glass interface: glass only on the floating control layer (navigation, menus, sheets, composer), flat content, a luminous starfield, SVG-lens refraction on the specular rim.
- Glass styles (Smoked, Clear, Frosted, Liquid, Lensed), accent colour, opacity, blur, contrast and starfield are adjustable under Settings > Appearance.
