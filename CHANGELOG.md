# Changelog

LAICA follows [Semantic Versioning](https://semver.org/). The `VERSION` file is the single source of truth: the build stamps it into the executable, `release.json`, `package.json` and the backend state.
Every release is made with `Release-Edition.ps1`, which builds, runs every test suite, snapshots the source into `editions\v<version>\source.zip`, archives the previously installed edition into `editions\v<old>\installed.zip`, then overwrites the installed program. User data is never touched. See `RELEASE.md`.

## [0.19.0]

### Added
- **Pictures in chats.** Screenshots and images an agent sees or makes (Claude tool results, MCP image content, Codex image items) are stored with the chat and shown as small thumbnails on the activity line, like Claude and Codex do. Click one to open a full-size viewer with arrows and Esc.
- **Verified prices.** Every time LAICA starts it reads Anthropic's, OpenAI's and Google's own pricing pages and uses their exact per-model prices for the cost estimates. The Prices tab shows when each vendor was last checked, lists the verified models and has a Check now button. A vendor whose page can't be read keeps its last verified prices. The editable family table is only the fallback for models a vendor doesn't list. (The built-in fallback prices were out of date and are corrected.)
- **Resizable sidebar.** It is wider by default (320 px) so File, Edit, View and Help sit inside it with room to spare, and can be dragged out to 460 px. Double-click the edge to reset.

### Changed
- Analytics is a fixed-size panel, so switching app or tab no longer resizes it.
- Every page uses the same 28 px side gutters as its title (Settings, Services, Assistants, Scheduled tasks, Activity and the workflow designer were uneven or reached the window edge).
- The workflow designer no longer has the Team / Solo switch, and the separate "Choose a service and model" bar on the Services page is gone (the designer does it better). The AI service list shows only agents that are installed, marked Installed.
- The graph tools menu uses one consistent row style.
- Dictation options show which one is selected, the dialog close button is centred, and the Prices tab uses the app's own buttons without browser spinners.

### Fixed
- Claude Code is detected when the Claude desktop app is the Microsoft Store version (its data lives under Packages\Claude_*).
- The chat list no longer scrolls sideways; the glowing light that followed the mouse is gone; the glow behind the glass is subtler; the Analytics icon matches the others; costs are labelled US dollars.
## [0.18.2]

### Fixed
- The sidebar's chat list no longer shows a horizontal scrollbar (a few rows were 4 to 8 pixels too wide).
- The title strip (back, forward, File, Edit, View, Help) is now one continuous bar: the sidebar's glass starts below it, so there is no colour seam next to Help.

## [0.18.1]

### Fixed
- **Claude Code was not detected** when the Claude desktop app is the Microsoft Store version: the Store build keeps its data under %LOCALAPPDATA%\Packages\Claude_*, which LAICA did not look in, so Claude Code was missing from the team designer's AI service menu, the model picker and new chats. LAICA now searches that location too (for the program and for the desktop app's chat titles).
- The team designer lists agents it could not find as greyed-out entries with the reason, instead of hiding them, and re-detects agents when the page opens and every 30 seconds.

### Changed
- The glowing light that followed the mouse across the glass is gone.
- Developer option: the LAICA_WEBVIEW_CACHE environment variable moves the WebView cache folder, so a second preview instance can run beside the installed app.

## [0.18.0]

### Added
- **Recent** section in the sidebar: the six most recently active chats from every project, shown again in their projects like Codex does.
- **Updates.** LAICA checks GitHub for a newer release every few hours and shows an Update pill in the sidebar footer. Clicking it downloads the installer, verifies its SHA-256 against the release's sums file, installs over the current folder and restarts. Nothing installs without a click. Settings > Agents > Updates has a switch to turn automatic checks off and a Check now button.
- **MCP servers stay current.** Installed npx and uvx MCP servers (Claude Code and Codex) are set to fetch their latest version every time they launch (pkg@latest), once a day and on Check now; servers pinned to a version on purpose are left alone and the config files are backed up first. New installs from the plugin library use @latest.

### Fixed
- Chats are now ordered by their real last activity, so a LAICA chat no longer sits above newer Codex or Claude history.

## [0.17.3]

### Changed
- Analytics now says which currency costs are in: amounts show as US$ and the labels, notes and price table say US dollars (USD).

## [0.17.2]

### Changed
- The coloured glow behind the glass is much subtler: the scene colours and the pointer light are roughly halved in strength, so glass panels no longer pick up distracting blobs.

### Fixed
- The Analytics icon at the bottom left now matches the brightness of the other footer icons.

## [0.17.1]

### Added
- Sidebar sections (Teams, Pinned, Projects, Chats) can be collapsed with the small minus button beside each heading. The choice is remembered.

## [0.17.0]

### Added
- LAICA as a co-author: commits made by agents run through LAICA (and commits made from LAICA's own Changes panel) get a `Co-authored-by: LAICA <email>` trailer next to the agent's own credit. On by default; switch it off globally or per project in Settings > Agents. The repository's own git hooks keep running, and repositories that set their own `core.hooksPath` are left alone.
- The trailer email is a placeholder until a real GitHub account address is chosen (Settings > Agents > Commits).

### Changed
- The public build script no longer depends on a developer-specific Node path.

## [0.16.0]

### Added
- Team analytics (Analytics > Teams): per team success rate, time and cost per run, how parallel the work really was, leader overhead, per-teammate cost share, agent swaps after usage limits, and badges for the most reliable, fastest and cheapest design.
- Every finished team run is logged with each member's vendor, model, time, tokens and estimated cost.
- Teammates on any vendor can pin a model in the team designer.

## [0.15.0]

### Added
- Estimated cost in Analytics: an API-equivalent figure for all usage, per app, per model and per day, with an editable price table (Prices tab).

### Fixed
- Analytics no longer re-scans the logs on every refresh; the index is reused for 45 seconds and rebuilt automatically when the format changes.
- Favourite model ignores internal auto-review entries.

## [0.14.0] - 2026-10-07
### Added
- **Analytics** (chart icon, bottom left of the sidebar): sessions, messages, total tokens, active days, peak hour and favourite model across everything you have done with Codex and Claude Code, an activity heatmap, streaks, busiest day, tokens per message, a split by app, most used tools and a per-model breakdown. A tab per app or all combined, for all time, 30 days or 7 days, plus a Charts view: tokens per day split by app, a running total, messages by weekday and hour, and model share. Your history is indexed once in the background and cached.
- **Workflow handoff**: choose what happens when a vendor runs out of usage. Ask me, or hand the work to a backup team that does not use the vendor that ran out, or to one agent, with the conversation or the team's progress carried over. A team can override the default. Work moves at most twice. Set it in Settings, in the Workflow designer's toolbar, or per team.
- **Files changed**: each edited file gets a card with its added and removed lines and an openable diff, and every turn ends with "N files changed +A -D", which opens the Changes panel.
- **Working indicator**: an animated mark on a chat's sidebar row, its project and running teams, a live timer on the row and in the chat, and "Worked for 4m 12s" when a turn finishes.
### Changed
- Calmer home screen and composer: borderless pills and icon buttons, a softer rim, airier glass, matching the sidebar.
## [0.13.1] - 2026-10-07
### Fixed
- The usage panel no longer says "no activity yet" for an app you have been using elsewhere: Claude Code now shows how many replies it gave today, counted from its transcripts.
- The usage panel opens above its button instead of on top of it.
- Claude sessions with nothing in them (a command run on its own) no longer appear as empty chats.
### Verified
- A team can mix vendors: a supervisor from one vendor with Gemini teammates (each optionally pinned to a Gemini model) and a teammate from a third, running in parallel, covered by a test.
## [0.13.0] - 2026-10-07
### Added
- **Real plan usage**: the usage panel now shows live percentages per app for the daily (5-hour) window and the weekly window, with reset times. Codex's numbers are read from the rate limits it records in its own session logs. Claude Code does not publish plan percentages locally, so LAICA totals the tokens in all your Claude transcripts for today and the last seven days, shows Claude's percentage as soon as it reports one during a chat, and lets you set your own daily and weekly limits for a percentage. Warnings appear at 80% and 95%. No credentials are read.
- **Activity view like Claude's**: tool calls are now plain sentences ("Read Settings.tsx", "Ran npm test") grouped as "Ran 2 commands, edited a file (1 failed)", each opening to the exact command, edit or output. A live line shows what the agent is doing and for how long.
### Fixed
- The sidebar divider no longer cuts through the Help menu.
## [0.12.0] - 2026-10-07
### Changed
- **Liquid Glass redesign**, researched from Apple's guidance. Glass is now only the floating control layer (the chat header, the message box, menus, dialogs, toasts) while content (chat, cards, the file inspector) is calm and flat. The sidebar is connected to the window instead of floating as a bubble, with plain rows, a hairline divider and soft capsule selection. A luminous scene sits behind the glass, floating surfaces refract what is behind them at their bezel (generated SVG displacement maps), carry a lit specular rim, and catch a light that follows the pointer. Chat scrolls under the floating header and composer with soft edges.
- A UI kit in the style of shadcn/ui on Radix primitives (dropdown and context menus, popover, command, dialog and sheet, tabs, switch, slider, tooltip), styled as glass. The command palette now uses it.
### Added
- **+ menu** like Codex: Add (files and folders, sketch, Plan mode, Goal), your installed MCP servers and skills (drop one into the message), and suggested plugins. Search by typing.
- **Ctrl+V pastes images** (and files) into the chat, with thumbnails. Sketch pad that attaches a drawing. Plan mode and Goal as one-shot message modes.
- **Undo (Ctrl+Z)** across the app: closing a chat (kept for two weeks and restorable), renaming, pinning, removing a project, deleting a team, removing an attachment or a queued message. Closing no longer asks for confirmation because you can take it back.
- Pinned chats, right-click menus on chats and projects (rename, pin, save as Markdown, close, show in File Explorer), a draft that stays with each chat, Up arrow to recall your last message, Esc to stop the running agent, Ctrl+L, Ctrl+F, Ctrl+, and Ctrl+1 to 9, a jump-to-latest button, copy buttons on code blocks, tooltips.
### Fixed
- Claude Code chats now take the name and project folder the Claude desktop app recorded, instead of landing in a fake "scratch" project.
- Hidden folders no longer appear as skills.
## [0.11.0] - 2026-10-07
### Added
- **Plugin library** (sidebar, Plugins): one-click install of MCP servers, starter skills and agent CLIs from a built-in catalogue. LAICA looks at the programs on the computer (Blender, Figma, Obsidian, Notion, Chrome, Docker, Git, Postgres, VS Code ...) and suggests the tools that fit first. Every install shows the exact command beforehand. Missing Node.js or uv can be installed with one click through winget; LAICA refreshes its PATH afterwards. Gemini CLI, Qwen Code, OpenCode, Aider, Claude Code and Codex install as agents and appear as vendors straight away.
- **Pause and resume**: pause any running chat or a whole team. A command-line agent is frozen (its process tree is suspended) so nothing is lost; the built-in agent holds at its next step. Stop works on a paused chat.
- **Add files**: the + button picks files, and files can be dragged onto the home composer or a chat, or pasted (including screenshots). Files inside the project are referenced in place; others are copied into the project's .laica/attachments (kept out of git). Attachments show as chips and are sent to the agent as paths.
- **Dictation**: a microphone button in the message box. Offline Windows speech recognition by default, or cloud transcription through an OpenAI or Groq service when you have one. Settings, Appearance chooses.
- **Queued messages**: type while an agent is working and the message is sent when it finishes.
- **Command palette** (Ctrl+K): jump to any chat, team or page and run common actions.
- Copy button on messages, and save a chat as Markdown.
### Changed
- Microphone permission is granted to LAICA's own page only.
## [0.10.1] - 2026-10-07
### Changed
- **Premium polish pass**: soft ambient light behind the glass so it has something to bend, thinner translucent panels with a hairline edge and top highlight, quieter flat controls, assistant replies without boxes, compact tool chips, springy hover and press motion, refined scrollbars and toasts.
### Fixed
- Choosing a glass style in Settings sent you back to the new-chat page (it remounted the whole app). It now updates in place.
- Settings sliders overflowed their card.
- The usage panel scrolls instead of growing past the window.

## [0.10.0] - 2026-10-06
### Added
- **Usage meter** (bottom right): per-vendor turns and tokens today, optional daily token budget with a warning at 80%, and a limited state with the expected reset time.
- **Usage-limit handling**: when Codex, Claude Code, Gemini or an API service reports a usage/rate limit, LAICA parks that vendor, warns with a toast, and shows a banner in the chat to continue on another agent (with the transcript) or hand the request to a team.
- **Seamless teams**: a teammate or leader that runs out of usage is replaced by an agent that still has some, the unfinished task continues there, and the Team page notes the switch. Parked vendors are skipped up front on later runs.
- Connections page lists every detected agent (Codex, Claude Code, Gemini CLI, ...) automatically and re-detects when the window regains focus. Gemini gets model choices and a model flag; agents installed in npm/bun/scoop/volta folders are found even when PATH misses them.
### Changed
- Settings is now a cog beside the Services and Guide icons at the bottom of the sidebar.

## [0.9.0] - 2026-10-06
### Added
- Settings > Appearance > Glass style: choose Smoked, Clear, Frosted, Liquid or Lensed glass, an accent colour, panel opacity, blur, contrast and starfield on/off.
### Fixed
- Thick grey window border: the resize padding showed the Windows Mica backdrop; the window now has no backdrop and the padding matches the app colour.
- Removed the service name from the bottom-right of the status bar.

## [0.8.9] - 2026-10-06
### Fixed
- Removed the grey 1px window outline (DWM border colour set to none).
- History rescans Codex and Claude chats on every launch (bypassing the cache) and again when the window regains focus.

## [0.8.8] - 2026-10-06
### Fixed
- Fix: the background never animated on PCs where Windows 'Show animations' is off (the OS reduced-motion flag froze it). It now follows only the in-app 'Animate the background' setting.

## [0.8.7] - 2026-10-06
### Changed
- **Background now behaves like the Firefox "Dark space" theme** (matched against a screen recording of it): pure black, a dense field of 1px specks that never move, nearly all of them fading out and back in place on slow cycles, very faint, many tinted warm orange or cool blue. Removed the drift, pointer parallax and shooting stars added in 0.8.4.

## [0.8.6] - 2026-10-06
Superseded by 0.8.7: this build was packaged before the background rewrite landed and is identical to 0.8.5.

## [0.8.5] - 2026-10-06
### Changed
- The speck field is about twice as dense and brighter, closer to the Firefox theme it copies.

## [0.8.4] - 2026-10-06
### Changed
- **Dark space background** in the style of the Firefox "Dark space full transparent" theme: near-black with a dense field of tiny pinpoint specks (a third of them shimmer, the whole sky creeps slowly upward and shifts a little with the pointer, an occasional faint shooting star), the darker base colour with soft violet and blue corner light, and the sidebar, composer and chat panels made more see-through so the specks show behind them. Replaces the GPU nebula.
- New **Animate the background** switch in Settings → Appearance (also off under Reduce transparency or reduced-motion).
## [0.8.3] - 2026-10-06
### Fixed
- **Projects now match Codex exactly**: LAICA reads Codex's own project list (names and order), which project each thread belongs to, and Codex's thread titles, so your own project names appear with the same conversation names you see in Codex. Claude Code conversations use their own custom titles. Threads Codex keeps outside any project go under "Chats" instead of becoming bogus projects.
- Opening an old Codex conversation whose folder was deleted now falls back to the project's real folder.

### Added
- **Provider badges** on every chat and in the chat header: GPT, Claude, Gemini, another agent, or Team (agent teams and workflow teams).
- The sidebar is now a project tree like Codex's: each project expands to its conversations; the "Work in" control on the home screen is a searchable dropdown of your projects (with Browse and Type a path).
- Effort picker, nebula background, new lavender New chat row, lavender send / stop button, page-button toggle-back and full-screen fix (see 0.8.2).
## [0.8.2] - 2026-10-06
### Added
- **Reasoning effort** for chats: an Effort picker next to the model picker (Codex: the levels each model supports; Claude Code: low to max). It reaches the agent as `--effort` / `model_reasoning_effort`, is remembered per chat and shown in the chat header. Claude Code nodes in the Workflow designer can now choose a reasoning level too.
- **Nebula background**: a GPU-rendered deep-space nebula (domain-warped clouds in LAICA's violet / indigo / teal, a drifting aurora curtain, breathing corner light, mouse parallax) behind an upgraded starfield with depth layers, coloured stars, flared bright stars and shooting stars. Falls back to the starfield alone without WebGL; still under Reduce motion.
- **New chat** is now a slim lavender row in the style of Codex's sidebar (compose icon, circled plus on the right).
- **Send button**: lavender, glowing, arrow when idle; becomes a rounded square stop button while the agent is working.

### Fixed
- Clicking the button of the page you are already on (Services, Settings, Tasks, Assistants, Workflow designer, Activity) now returns you to where you came from. This applies to every sidebar button and the phone top bar.
- Leaving full screen from the window button took two clicks; it now exits in one and restores the previous (maximised or normal) window.
- The Workflow designer inspector's glass outline no longer scrolls across the fields.
- Garbled characters ("Â·", "â€¦") in the File menu and the Effort picker.
## [0.8.1] - 2026-10-06
Polish and fixes after hands-on use.

### Added
- **Model picker on the main screen**: one grouped, searchable dropdown with every Codex model, Claude Code (default / Opus / Sonnet / Haiku), other installed agents, every model from each connected API service, and your designed teams (Workflow-designer teams and Team-mode teams). Choosing a workflow team turns each message into that team's goal.
- **Codex-style top bar** replacing the native Windows banner: back / forward, sidebar toggle, File / Edit / View / Help menus, drag area, minimize / maximize / close, and F11 full screen. Keeps native edge resizing, snapping and the window shadow. Shortcuts: Ctrl+N, Ctrl+B, Alt+←/→.
- **Projects**: "+" to add a project folder (with a native Browse dialog); every folder from your Codex and Claude Code history appears automatically, with a "new chat in this project" button.
- **History**: your existing Codex and Claude Code conversations are listed and open as chats you can read and continue with each tool's own resume feature. Large and in-use files are read safely.
- **Workflow designer**: Claude Code and any other installed agent can now be the "AI service" of a node, so one team can mix Codex, Claude and API models (read-only, text answers). New button, "New team" starts empty (supervisor only), template for three workers, Delete button and menu command, Ctrl+A / Ctrl+D, right-click selects the worker under the pointer, left-drag pans, clicking empty space clears the selection.
- **Live background**: a real starfield (random placement, depth drift, twinkle, occasional shooting stars) and slowly moving aurora light; pauses when the window is hidden and respects Reduce transparency / reduced motion.
- **Frosted-glass dropdowns** for every select in the app (grouped sections, descriptions, search, keyboard control) instead of the browser's grey list.

### Changed
- The old "Codex mode" page is gone: Team / Solo moved to the Workflow designer toolbar; Reduce transparency and Snap to grid moved to Settings → Appearance.
- Designer messages (what Delete did or refused to do) now appear as notifications instead of a tiny footer line.
- Send button, New chat button, form alignment and dialog fields reworked; page scrollbars removed.
- Services page offers OpenAI, Anthropic, Gemini, DeepSeek, OpenRouter, Groq, Mistral, xAI, Kimi and Qwen presets.

### Fixed
- Deleting the Supervisor, a fixed stage, or with nothing selected used to do nothing silently; it now explains why. Delete now works whenever the designer is visible, not only while it holds keyboard focus, and deletes multi-selections.
- Cancel on a new team now leaves the editor.
- Profile and service dialogs use a stable text input.
## [0.8.0] - 2026-10-06
A full multi-agent workspace modelled on AionUi's feature set, in LAICA's smoked-glass design.

### Added
- **New shell**: sidebar with New chat, Search, Scheduled tasks, Assistants, Teams, Projects and Conversations; a "what's your plan for today?" home with an agent picker, composer, project folder and assistants grid; slim chat view with a Files / Changes panel. The previous Teams graph is now the **Workflow designer**.
- **Approval prompts**: Claude Code (stdio permission protocol) and Codex ("Ask first" mode over the app-server protocol) now ask before running commands or editing files, with Allow / Always allow / Deny in the chat.
- **LAICA Agent** (built-in): works with any API key — OpenAI-compatible services (OpenAI, Gemini, DeepSeek, OpenRouter, Groq, Mistral, xAI, Kimi, Qwen, Ollama, LM Studio) and Anthropic. File, search, command, web-fetch and image-generation tools, all sandboxed to the chat folder and behind the same approvals. Conversations survive restarts.
- **Git worktrees**: any chat can run in its own isolated worktree and branch, so agents never disturb your main checkout and always return to the same files. Changes tab: status, diffs, commit, **Apply to main** (refuses when anything could be lost) and remove.
- **"While you were away" notices**: when you return to a chat, LAICA tells the agent (and shows you) which files and commits changed outside the conversation.
- **Team mode**: a leader plans, up to 8 teammates work in parallel (optionally each in a worktree), the leader reviews and writes the final answer.
- **Remote access** (off by default): password-protected web interface for a phone or another PC, with live updates, rate-limited sign-in and a phone layout.
- **Channels**: two-way Telegram bot with one-time pairing; notifications to Slack, Discord, Lark/Feishu, DingTalk, WeCom and generic webhooks when tasks and teams finish. Secrets are encrypted for the Windows account.
- **MCP and skills management**: add/remove MCP servers for Claude Code and Codex through their own CLIs; create, edit and archive skills.
- **Previews** for Word, Excel and PowerPoint files and PDFs, alongside code, Markdown, HTML, images and diffs.
- Scheduled tasks can run each fresh chat in a worktree and send notifications.
- `Release-Edition.ps1`: versioned release pipeline with source snapshots, installed-edition archives, protected user data and rollback.

### Changed
- Version is now read from `VERSION`; git repository with a tag per release.
- Frontend sends real booleans (fixes the task on/off switch and isolate options).

### Tests
- Backend suites: Harness (approvals, worktrees, drift, built-in agent, teams, skills/MCP, Office), Channels, Remote, Backend, Observer — 100+ assertions, all required to pass before a release is archived or installed.

## [0.7.1] - 2026-10-06
### Added
- Harness page: run Codex and Claude Code chats side by side as child processes with streamed output, tool cards and a per-chat permission mode.
- Chats persist across restarts and resume the underlying Codex / Claude Code session.
- Auto-detection of Gemini CLI, Qwen Code, OpenCode, Goose, Kimi, Aider and Copilot CLI, plus user-defined custom CLI agents.
- Assistants: 12 built-in presets and custom ones whose rules are sent with a chat's first message.
- Scheduled tasks (interval, cron, one-time) that start or continue chats.
- Workspace file tree with tabbed preview (code, Markdown, HTML, images, diffs) and in-place editing, sandboxed to the chat folder.
- Tools page: detected agents, MCP servers, skills (read-only) and custom CSS.
- Backend checks (`checks/HarnessTests.cs`, 19 assertions).
### Changed
- Glass UI reworked to the Open Glass "Smoked" look: removed the opaque `!important` panel overrides and added a lit ambient backdrop so the glass has something to refract.
- Build script silences the harmless `"use client"` bundler warning that aborted the build.

## [0.7.0] - 2026-10-05
- Open Glass UI desktop interface (WebView2 host + React) with Teams, Activity, Services and Mode pages. Baseline edition; its installed program files are archived in `editions\v0.7.0\installed.zip` when 0.8.0 is installed.
