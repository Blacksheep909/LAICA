# Third-party software

LAICA's own source is licensed under MIT. Its dependencies retain their own licenses and copyright notices.

| Component | Version | License | Source |
| --- | --- | --- | --- |
| Open Glass UI | 0.4.0 | MIT | https://github.com/moekoelueker/open-glass-ui |
| React / React DOM | 19.2.0 | MIT | https://github.com/facebook/react |
| Lucide React | 0.468.0 | ISC | https://github.com/lucide-icons/lucide |
| Microsoft WebView2 SDK | 1.0.4258.31 | Microsoft redistribution license | https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4258.31 |

The three required WebView2 SDK binaries are vendored with Microsoft's license under `vendor/webview2`. Their SHA-256 hashes and package provenance are recorded in `manifest.json`. The shared WebView2 Runtime is installed separately by Microsoft.

Build-time packages, including TypeScript, Vite and pnpm, are pinned by the frontend manifest and lockfile. The build copies notices for bundled runtime dependencies into `build/licenses`. Preserve that folder when redistributing a compiled application.
