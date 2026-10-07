# Contributing

Thanks for helping. LAICA is public domain ([The Unlicense](LICENSE)); by contributing you agree your contribution is released the same way.

## Set up

```powershell
git clone https://github.com/Blacksheep909/LAICA.git
cd LAICA
./Build.ps1 -Development
./Test.ps1
```

You need Windows, Node.js 22+ and pnpm 11. The first build runs `pnpm install --frozen-lockfile`. See [docs/releases.md](docs/releases.md) for details and [docs/architecture.md](docs/architecture.md) for a tour of the code.

For interface work, `frontend/` has the usual Vite setup (`pnpm dev` serves the UI; the commands that need the Windows backend only work inside the app or through the remote-access server).

## Ground rules

- **Keep the host C# 5 compatible.** The build uses the compiler in Windows, so no `?.`, string interpolation (`$""`), `nameof` or local functions. Warnings fail the build.
- **New host files must be added to the source lists in `Build.ps1`** (the app and each check that compiles the backend).
- **Add a check.** Backend behaviour is covered by `checks/HarnessTests.cs` (one line per behaviour, `Check(condition, "area: what is true")`). Frontend logic that can be pure goes in `frontend/src/*-model.ts` with a test in `frontend/tests/`.
- **No personal data.** Never commit real chat titles, paths, usernames, emails, tokens or usage logs. Test fixtures must be invented. `.gitignore` already excludes the `harness` data folder.
- **Never read or store agent credentials.** LAICA reads usage records in local logs only.
- **Match the surrounding style** and keep comments about *why*, not *what*.
- **Interface changes:** glass is for the floating control layer only; content stays flat. Check the result in the real app (the blur and refraction need a GPU WebView2) as well as in a browser.

## Pull requests

1. Branch from `main`, make the change with its checks.
2. Run `./Build.ps1 -Development` then `./Test.ps1`; CI runs the same on Windows.
3. Add a line under the next version in `CHANGELOG.md`.
4. Open a pull request describing what changed and why. Screenshots should use demo data.

## Reporting problems

Open an issue with your Windows version, LAICA version (bottom left of the sidebar), which agent was involved and what you expected. Please do not paste logs that contain your prompts or file contents.
