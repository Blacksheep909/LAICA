# Contributing to LAICA

LAICA is maintained by Charlie Frater. Bug reports, focused fixes and practical usability improvements are welcome.

Please open an issue with the steps that trigger the problem, what you expected, what happened, and your Windows/LAICA versions. Use synthetic task data where possible. Screenshots help with layout problems, but remove private chat content and file paths first.

For a code change:

1. Fork the repository and make a focused branch.
2. Restore the pinned frontend dependencies and build the Windows host.
3. Run `./Test.ps1` and check the affected flow in the actual desktop app.
4. Open a pull request explaining the problem, behavior change and validation.

Keep work displays grounded in recorded actions. Do not invent progress percentages, live agent status or computer actions when the available records do not establish them. Prefer readable commands and outcomes to decorative motion.

Keep credentials, user profiles, session logs, generated binaries and machine-specific installer paths out of source changes. Preserve third-party notices. A parser or backend change should have a meaningful regression check; a small visual adjustment usually needs a visual pass rather than a test that mirrors CSS.

The public package is standalone. Do not reintroduce dependencies on a maintainer's personal Codex helpers or saved settings.
