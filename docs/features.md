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

## Updates

- **LAICA** checks GitHub for a newer release every few hours. A pill in the sidebar footer offers the update; clicking it downloads the installer, verifies its SHA-256 against the release's sums file, installs over the current folder and restarts. Nothing installs without a click, and Settings > Agents > Updates switches the automatic checks off.
- **MCP servers** installed for Claude Code or Codex through npx or uvx are set to fetch their latest version each time they start (`pkg@latest`). LAICA does this once a day and on Check now, backs up the config files first and leaves servers you pinned to a version alone. New installs from the plugin library already use `@latest`.

## Commits and credit

- Commits made by agents through LAICA, and commits made from the Changes panel, get `Co-authored-by: LAICA <email>`. It is added by a git hook that first runs the repository's own hook of the same name. Repositories that set their own `core.hooksPath` are left untouched. On by default; switch it off globally or per project in Settings > Agents > Commits. See [getting started](getting-started.md#8-laica-as-a-co-author) for choosing an email GitHub will show an avatar for.

## Look and feel

- A Liquid Glass interface: glass only on the floating control layer (navigation, menus, sheets, composer), flat content, a luminous starfield, SVG-lens refraction on the specular rim.
- Glass styles (Smoked, Clear, Frosted, Liquid, Lensed), accent colour, opacity, blur, contrast and starfield are adjustable under Settings > Appearance.
