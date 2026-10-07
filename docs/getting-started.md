# Getting started

This walks you from nothing to a first chat and a first mixed-vendor team. It takes about ten minutes.

## 1. Install LAICA

1. Download `LAICA-Setup-<version>.exe` from the [latest release](https://github.com/Blacksheep909/LAICA/releases/latest).
2. Run it. If Windows SmartScreen warns about an unrecognised app, that is because the build is not code-signed (see [releases](releases.md)). Choose **More info**, then **Run anyway**.
3. Setup installs to `%LOCALAPPDATA%\Programs\LAICA`, adds Start menu and desktop shortcuts and opens LAICA.

Upgrading is the same: run the newer setup. Your chats, teams and settings are kept. To remove LAICA use *Apps > Installed apps*; your data stays until you delete the `harness` folder inside the install folder.

You can also unzip `LAICA-<version>-win-x64.zip` anywhere and run `LAICA.exe`.

## 2. Install and sign in to at least one agent

LAICA runs the agent tools you already have; it does not log in for you.

| Agent | Install | Sign in |
| --- | --- | --- |
| Codex | `npm install -g @openai/codex` | run `codex` once and sign in |
| Claude Code | see the [Claude Code docs](https://docs.claude.com/en/docs/claude-code) | run `claude` once and sign in |
| Gemini CLI | `npm install -g @google/gemini-cli` | run `gemini` once and sign in |

Open **Plugins** in LAICA to install agent CLIs, runtimes (Node.js, uv) and more with one click. It suggests tools for programs it finds on your PC first.

When LAICA starts it detects installed agents automatically and lists them under **Settings > Agents**. Anything it did not find can be added as a custom agent (a command plus arguments).

## 3. Your first chat

1. Click **New chat** (Ctrl+N).
2. Pick a project folder with *Work in ...* under the composer, or add a project with the **+** next to *Projects*.
3. Choose the agent and model from the pill in the composer, then type and press Enter.

Things worth knowing right away:

- **+** in the composer adds files, a sketch, Plan mode or a Goal, and lists your MCP servers, skills and suggested plugins. You can also drag files in or paste an image with Ctrl+V.
- **Ctrl+K** opens the command palette. **Esc** stops the running turn. **Ctrl+Z** undoes the last rename, pin, close or similar action.
- Your existing Codex and Claude Code conversations appear under their projects automatically. Use the refresh icon next to *Projects* to rescan.
- The right-hand panel shows the project's files and the **Changes** the agent made, with per-file diffs. Commit from there.

## 4. Watch your usage

The usage button at the bottom right shows each vendor's plan windows (the 5-hour and weekly percentages for Codex, token totals for Claude) and warns before you run out. Set a daily or weekly token budget for any vendor in its popover.

## 5. Build a mixed-vendor team

1. Click **+** next to *Teams* in the sidebar.
2. Name the team, choose its working folder and pick the **leader**: the agent that plans the work and reviews the result.
3. Add teammates. For each, choose any agent and optionally pin a model, for example *Builder* on Claude Sonnet, *Reviewer* on Codex and *Docs* on Gemini. Up to eight teammates.
4. Turn on **isolation** to give each teammate its own git worktree so they cannot overwrite each other.
5. Type a goal and press **Run**. The leader splits the goal into tasks, the teammates work in parallel in their own chats, and the leader reviews and writes the final answer.

If a teammate's vendor runs out of usage mid-task, LAICA moves that task to another available agent with a note to continue the work already done.

## 6. Choose what happens when a vendor runs out

Open **Settings > Agents > Workflow handoff**:

- **Ask me**: the chat shows a banner and you pick where to continue.
- **A backup team**: work moves to a team that does not rely on the vendor that ran out.
- **One agent**: a single agent takes over with the full conversation.

Work moves at most twice, so it can never loop.

## 7. See which team designs work

Click the chart icon at the bottom left of the sidebar and open the **Teams** tab. Each team you have run gets a card with its success rate, time and cost per run, how parallel the work was, how much of the cost is the leader, agent swaps and a per-teammate breakdown. Badges mark the most reliable, fastest and cheapest designs once you have two or more teams. Use the range buttons (All, 30d, 7d) to compare recent runs.

The cost is an **estimate at API list prices**. If you are on a flat subscription you pay that instead, so read it as "the compute you got". Edit the price table on the **Prices** tab to match your provider.

## 8. LAICA as a co-author

Commits that agents make through LAICA get a `Co-authored-by: LAICA <email>` line next to the agent's own. Configure it in **Settings > Agents > Commits**: switch it off globally, off per project, or set the email.

GitHub only shows an avatar and links a co-author when the email belongs to a real account. Options:

1. **A dedicated bot account** (for example `laica-bot`). Use its noreply address, `ID+laica-bot@users.noreply.github.com`; the numeric ID is in `https://api.github.com/users/laica-bot`.
2. **A GitHub App** you register for LAICA. Its bot noreply address is `ID+app-slug[bot]@users.noreply.github.com`.
3. **A secondary account you own.**
4. **No email** (the default placeholder): commits are credited to "LAICA" by name only, with no avatar or profile link.

## Troubleshooting

- **An agent is missing.** Check it runs in a terminal (`codex --version`), then restart LAICA. PATH changes need a restart.
- **A blank window.** Install or repair the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).
- **Dictation does not work.** Dictation uses Windows speech recognition; check *Settings > Time & language > Speech*.
- **Something looks wrong after an upgrade.** Previous settings are untouched; delete the `harness\analytics.json` cache to force a clean re-index of usage.
