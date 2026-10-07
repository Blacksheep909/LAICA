# Building, packaging and releasing

## Requirements

- Windows 10 or 11 (64-bit). The C# compiler used is `csc.exe` from the .NET Framework 4 that ships with Windows, so no SDK is needed.
- Node.js 22 or newer and pnpm 11 for the interface (`corepack enable` gives you pnpm).
- Git, for the checks that exercise worktrees and commits.

## Build and test

```powershell
./Build.ps1 -Development        # interface, desktop app, MCP bridge and check programs -> build/ and checks/
./Test.ps1                      # runs every check; prints "All LAICA checks passed."
```

`Build.ps1` accepts `-NodePath` and `-PnpmPath` if Node and pnpm are not on PATH. `-Development` produces an **unsigned** build; without it the script refuses to run unless you pass a code-signing certificate (`-CertificateThumbprint`, see below).

The build fails if the payload exceeds 10 MB, if the vendored WebView2 binaries do not match their pinned hashes, or if any compiler warning appears (warnings are errors).

## Package

```powershell
./tools/Package.ps1
```

writes to `dist/`:

| File | What |
| --- | --- |
| `LAICA-<version>-win-x64.zip` | the program folder, ready to unzip and run |
| `LAICA-Setup-<version>.exe` | a self-contained per-user installer (the zip is embedded) |
| `SHA256SUMS-<version>.txt` | hashes of both |

The installer supports `/S` (silent) and `/D=<folder>` (install location). Upgrades overwrite program files and leave the `harness` data folder alone; the uninstaller (`Uninstall.exe /uninstall`, also listed in *Installed apps*) does the same. `checks/SetupTests.ps1` installs, upgrades and uninstalls into a temporary folder to prove it.

## Signing

Releases are **not code-signed** because there is no certificate. Windows SmartScreen will warn on first run. To sign your own build, install a code-signing certificate and run:

```powershell
./Build.ps1 -CertificateThumbprint <thumbprint>
```

which signs `LAICA.exe` and `LAICA.Bridge.exe` with `signtool` and a timestamp server (see `Signing.ps1`). Sign `LAICA-Setup-<version>.exe` yourself after packaging.

## Versioning

The version lives in `VERSION` (MAJOR.MINOR.PATCH) and is stamped into the executables, the interface package and the release notes. Every release needs a section in [CHANGELOG.md](../CHANGELOG.md); the GitHub release notes are copied from it.

## Publishing a release

1. Update `VERSION` and `CHANGELOG.md`, run `./Build.ps1 -Development` and `./Test.ps1`.
2. `./tools/Package.ps1`.
3. Tag the commit `v<version>` and push the tag.
4. Create a GitHub release for the tag, paste that version's changelog section as the notes, mark it as unsigned, and attach the three files from `dist/`.

## Continuous integration

`.github/workflows/checks.yml` builds and runs `Test.ps1` on `windows-latest` for every push and pull request.
