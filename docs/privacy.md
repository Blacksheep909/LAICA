# Privacy

LAICA runs entirely on your computer. It has no accounts, no analytics or telemetry, no crash reporting and no LAICA server. This page lists what it reads, what it stores and what can leave your machine.

## What LAICA reads

| Source | Why |
| --- | --- |
| `~/.codex/sessions`, `session_index.jsonl`, the Codex project list | to show your Codex chats, titles and projects, and the Codex 5-hour and weekly usage percentages |
| `~/.claude/projects` and the Claude desktop app's session metadata (`%APPDATA%\Claude\claude-code-sessions`) | to show your Claude Code chats with their real titles and folders, and token totals |
| Your project folders | to show files, diffs and git status for the folder you opened |
| The list of installed programs and PATH | to detect agent CLIs and suggest plugins |

LAICA **never reads login tokens, API keys or credential files** belonging to Codex, Claude, Gemini or any other tool. Plan percentages come from usage records those tools write into their local logs, not from any account API. Claude plan percentages are not available locally and are not shown.

## What LAICA stores

Under the install folder, in `harness\` (survives upgrades and uninstalls):

| File | Contents |
| --- | --- |
| `sessions\*.json` | chats you start in LAICA: messages, tool events, settings |
| `trash\` | chats you closed, kept 14 days so you can restore them |
| `teams.json`, `teamruns.json` | your teams and one record per finished team run (vendors, models, times, tokens, estimated cost) |
| `usage.json`, `analytics.json` | token counters and the cached summary of your Codex and Claude logs (counts only, no message text) |
| `prices.json`, `handoff.json`, `coauthor.json`, `agents.json`, `tasks.json` | your settings |
| `hooks\` | the small git hooks used for the co-author trailer |

API keys you enter for API models under Settings > Services are encrypted with Windows DPAPI for your user account before they are written to disk. Remote access, if you turn it on, stores a salted password hash.

Elsewhere: a WebView2 cache in `%LOCALAPPDATA%\LAICA`, and your interface preferences in the browser storage of that WebView.

To delete everything LAICA stored, close it and delete the `harness` folder and `%LOCALAPPDATA%\LAICA`.

## What leaves your computer

- **The agents you run.** Codex, Claude Code, Gemini and other CLIs send your prompts and files to their own providers exactly as they do in a terminal. LAICA just launches them. Their privacy policies apply.
- **API models** you configure send prompts to the endpoint you entered.
- **Plugin installs** call winget or npm, which download from their registries.
- **Channels** (optional) post to Telegram, Slack and similar services when you configure them.
- **Remote access** is off by default. When on, it listens on localhost, or on your local network if you choose that, and requires a password. Nothing is exposed to the internet by LAICA.
- **Git.** Only the git commands you or an agent run. LAICA does not push anything by itself.

LAICA contacts no other host. The interface is loaded from local files under a Content-Security-Policy that blocks remote scripts and connections.

## Co-author trailer

If enabled, commits made by agents through LAICA include `Co-authored-by: LAICA <email>`. The email is whatever you set (a placeholder by default). It is public wherever those commits are pushed.

## Screenshots and examples

The screenshots in this repository use invented projects, chats and usage numbers. No personal data is included in the source, tests or documentation.
