# Privacy and data flow

## Stored locally

The shared GUI/bridge workspace is `LAICA_HOME`, or `%LOCALAPPDATA%\LAICA\workspace` by default. It contains profiles, the active team, LAICA mode, workspace preferences, local service definitions and plan files. GUI assets and executables live separately.

Service keys are protected with Windows DPAPI for the current user. They are not intentionally included in frontend state or profile exports. Protect your Windows account and backups; encryption does not make arbitrary logs or task content safe to share.

The WebView2 cache is under `%LOCALAPPDATA%\LAICA\WebView2`.

## Read by Activity

The observer reads local Codex session/index records from `CODEX_HOME`, defaulting to the user's `.codex` directory. It extracts public commentary/final messages and recorded actions, not private analysis-phase messages. Retained command and output details are bounded and common credential patterns are redacted.

Commands, paths, user task text and tool results can still reveal private information. Redaction is a best-effort pattern filter, not a guarantee. Inspect screenshots, exports and issue attachments before sharing them. Avoid uploading session files or your workspace folder to this public repository.

## Sent to services

The MCP bridge sends the selected team and requested activity to the host that calls its tools. It cannot execute shell commands, start an additional supervisor, edit Codex configuration or start a model.

For a local run, the configured API receives the task text, worker context and prompts necessary to produce its result. Local applications may have their own storage or cloud forwarding behavior; their data policies are separate from LAICA.

The frontend dependency restore accesses the package registry. LAICA itself has no analytics service or automated upload of your local activity records.

## Sharing and troubleshooting

Report a minimal reproduction using synthetic data. Remove credentials, private paths, full commands containing personal information and unrelated chat content. A development preview's example activity can demonstrate layout without exposing real sessions.
