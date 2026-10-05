# Builds and release signing

`Build.ps1 -Development` produces an explicitly unsigned local build. `Install.ps1 -Development` installs it only when requested with that flag. Neither command changes Windows trust, firewall settings or Smart App Control.

An unsigned application may be blocked. The public source preview does not yet include a trusted signed installer or a promise that every Windows application-control policy will allow it.

For a trusted release, maintainers need an appropriate code-signing identity and timestamp service:

```powershell
./Build.ps1 -CertificateThumbprint YOUR_CERTIFICATE_THUMBPRINT
./Test.ps1
./Install.ps1
```

The signing helper requires a current non-self-signed certificate with a private key and code-signing use. It signs both the desktop GUI and MCP bridge, requires a timestamp and checks Authenticode validity. A signed build records `ReleaseSigned=true`; the normal installer verifies both executables again.

Use `-CertificateStore LocalMachine` if the identity is in that store. `-SigntoolPath` enables the SignTool timestamp path. Certificate files, private keys and signing credentials belong outside the repository and must never be committed.

A release package should contain only the verified `build/` payload and its license notices. Run tests and inspect the actual Windows app before publication. Keep unsigned CI artifacts clearly marked as development builds; do not relabel them as trusted releases.

See Microsoft's [Smart App Control signing guidance](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control).
