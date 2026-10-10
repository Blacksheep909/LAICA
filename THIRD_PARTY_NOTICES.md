# Third-party notices

LAICA's own code, documentation and images are released into the public domain under [The Unlicense](LICENSE). That does not and cannot change the licences of the software below. Each component keeps its own licence and copyright notice; copies of the licence texts are in [licenses/](licenses/) and are shipped inside every LAICA build (`build/licenses`).

## Bundled in the program

| Component | Version | Licence | Source |
| --- | --- | --- | --- |
| Microsoft WebView2 SDK (`Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.WinForms.dll`, `WebView2Loader.dll`) | 1.0.4258.31 | [Microsoft WebView2 SDK licence](licenses/Microsoft.Web.WebView2-1.0.4258.31.txt) | https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4258.31 |

The three SDK binaries are vendored in `vendor/webview2` with their SHA-256 hashes and provenance in `manifest.json`. The shared WebView2 Runtime is installed separately by Microsoft and is not redistributed here. The .NET Framework compiler and class libraries are part of Windows and are not redistributed.

## Bundled in the interface (npm packages)

These are compiled into `ui/` by Vite. The list is the full production dependency tree of `frontend/package.json`, so it can include packages whose code is only partly used.

| Package | Version | Licence | Source |
| --- | --- | --- | --- |
| @floating-ui/core | 1.8.0 | [MIT](licenses/floating-ui_core-1.8.0.txt) | https://github.com/floating-ui/floating-ui |
| @floating-ui/dom | 1.8.0 | [MIT](licenses/floating-ui_dom-1.8.0.txt) | https://github.com/floating-ui/floating-ui |
| @floating-ui/react-dom | 2.1.9 | [MIT](licenses/floating-ui_react-dom-2.1.9.txt) | https://github.com/floating-ui/floating-ui |
| @floating-ui/utils | 0.2.12 | [MIT](licenses/floating-ui_utils-0.2.12.txt) | https://github.com/floating-ui/floating-ui |
| @radix-ui/number | 1.1.3 | [MIT](licenses/radix-ui_number-1.1.3.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/primitive | 1.1.7 | [MIT](licenses/radix-ui_primitive-1.1.7.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-arrow | 1.1.15 | [MIT](licenses/radix-ui_react-arrow-1.1.15.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-avatar | 1.2.6 | [MIT](licenses/radix-ui_react-avatar-1.2.6.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-collapsible | 1.1.20 | [MIT](licenses/radix-ui_react-collapsible-1.1.20.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-collection | 1.1.15 | [MIT](licenses/radix-ui_react-collection-1.1.15.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-compose-refs | 1.1.5 | [MIT](licenses/radix-ui_react-compose-refs-1.1.5.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-context-menu | 2.3.7 | [MIT](licenses/radix-ui_react-context-menu-2.3.7.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-context | 1.2.2 | [MIT](licenses/radix-ui_react-context-1.2.2.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-dialog | 1.1.23 | [MIT](licenses/radix-ui_react-dialog-1.1.23.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-direction | 1.1.4 | [MIT](licenses/radix-ui_react-direction-1.1.4.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-dismissable-layer | 1.1.19 | [MIT](licenses/radix-ui_react-dismissable-layer-1.1.19.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-dropdown-menu | 2.1.24 | [MIT](licenses/radix-ui_react-dropdown-menu-2.1.24.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-focus-guards | 1.1.6 | [MIT](licenses/radix-ui_react-focus-guards-1.1.6.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-focus-scope | 1.1.16 | [MIT](licenses/radix-ui_react-focus-scope-1.1.16.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-id | 1.1.4 | [MIT](licenses/radix-ui_react-id-1.1.4.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-menu | 2.1.24 | [MIT](licenses/radix-ui_react-menu-2.1.24.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-popover | 1.1.23 | [MIT](licenses/radix-ui_react-popover-1.1.23.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-popper | 1.3.7 | [MIT](licenses/radix-ui_react-popper-1.3.7.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-portal | 1.1.17 | [MIT](licenses/radix-ui_react-portal-1.1.17.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-presence | 1.1.10 | [MIT](licenses/radix-ui_react-presence-1.1.10.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-primitive | 2.1.10 | [MIT](licenses/radix-ui_react-primitive-2.1.10.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-progress | 1.1.16 | [MIT](licenses/radix-ui_react-progress-1.1.16.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-roving-focus | 1.1.19 | [MIT](licenses/radix-ui_react-roving-focus-1.1.19.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-separator | 1.1.15 | [MIT](licenses/radix-ui_react-separator-1.1.15.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-slider | 1.4.7 | [MIT](licenses/radix-ui_react-slider-1.4.7.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-slot | 1.3.3 | [MIT](licenses/radix-ui_react-slot-1.3.3.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-switch | 1.3.7 | [MIT](licenses/radix-ui_react-switch-1.3.7.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-tabs | 1.1.21 | [MIT](licenses/radix-ui_react-tabs-1.1.21.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-toggle-group | 1.1.19 | [MIT](licenses/radix-ui_react-toggle-group-1.1.19.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-toggle | 1.1.18 | [MIT](licenses/radix-ui_react-toggle-1.1.18.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-tooltip | 1.2.16 | [MIT](licenses/radix-ui_react-tooltip-1.2.16.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-use-callback-ref | 1.1.4 | [MIT](licenses/radix-ui_react-use-callback-ref-1.1.4.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-use-controllable-state | 1.2.6 | [MIT](licenses/radix-ui_react-use-controllable-state-1.2.6.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-use-effect-event | 0.0.5 | [MIT](licenses/radix-ui_react-use-effect-event-0.0.5.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-use-is-hydrated | 0.1.3 | [MIT](licenses/radix-ui_react-use-is-hydrated-0.1.3.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-use-layout-effect | 1.1.4 | [MIT](licenses/radix-ui_react-use-layout-effect-1.1.4.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-use-previous | 1.1.4 | [MIT](licenses/radix-ui_react-use-previous-1.1.4.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-use-rect | 1.1.4 | [MIT](licenses/radix-ui_react-use-rect-1.1.4.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-use-size | 1.1.4 | [MIT](licenses/radix-ui_react-use-size-1.1.4.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/react-visually-hidden | 1.2.11 | [MIT](licenses/radix-ui_react-visually-hidden-1.2.11.txt) | https://github.com/radix-ui/primitives |
| @radix-ui/rect | 1.1.3 | [MIT](licenses/radix-ui_rect-1.1.3.txt) | https://github.com/radix-ui/primitives |
| aria-hidden | 1.2.6 | [MIT](licenses/aria-hidden-1.2.6.txt) | https://github.com/theKashey/aria-hidden |
| cmdk | 1.1.1 | [MIT](licenses/cmdk-1.1.1.txt) | https://github.com/pacocoursey/cmdk |
| detect-node-es | 1.1.0 | [MIT](licenses/detect-node-es-1.1.0.txt) | https://github.com/thekashey/detect-node |
| get-nonce | 1.0.1 | [MIT](licenses/get-nonce-1.0.1.txt) | git@github.com:theKashey/get-nonce |
| lucide-react | 0.468.0 | [ISC](licenses/lucide-react-0.468.0.txt) | https://github.com/lucide-icons/lucide |
| open-glass-ui | 0.4.0 | [MIT](licenses/open-glass-ui-0.4.0.txt) | https://github.com/moekoelueker/open-glass-ui |
| react-dom | 19.2.0 | [MIT](licenses/react-dom-19.2.0.txt) | https://github.com/facebook/react |
| react-remove-scroll-bar | 2.3.8 | MIT | https://github.com/theKashey/react-remove-scroll-bar |
| react-remove-scroll | 2.7.2 | [MIT](licenses/react-remove-scroll-2.7.2.txt) | https://github.com/theKashey/react-remove-scroll |
| react-style-singleton | 2.2.3 | [MIT](licenses/react-style-singleton-2.2.3.txt) | https://github.com/theKashey/react-style-singleton |
| react | 19.2.0 | [MIT](licenses/react-19.2.0.txt) | https://github.com/facebook/react |
| scheduler | 0.27.0 | [MIT](licenses/scheduler-0.27.0.txt) | https://github.com/facebook/react |
| tslib | 2.8.1 | [0BSD](licenses/tslib-2.8.1.txt) | https://github.com/Microsoft/tslib |
| use-callback-ref | 1.3.3 | [MIT](licenses/use-callback-ref-1.3.3.txt) | https://github.com/theKashey/use-callback-ref/ |
| use-sidecar | 1.1.3 | [MIT](licenses/use-sidecar-1.1.3.txt) | https://github.com/theKashey/use-sidecar |


The licence of `react-remove-scroll-bar` is declared as MIT in its package metadata; the package ships no separate licence file.

## Development-only tools

TypeScript, Vite, the React plugin and pnpm are used to build the interface and are not part of the shipped program. They are pinned by `frontend/pnpm-lock.yaml`.

## Inspiration

LAICA's feature set was inspired by [AionUi](https://github.com/iOfficeAI/AionUi) and its interface by Apple's published Liquid Glass design guidance. No AionUi or Apple source code or assets are included.

## Skills in the plugin library

The **Ponytail** skill offered in the plugin library is a condensed copy of [Ponytail](https://github.com/DietrichGebert/ponytail) by Dietrich Gebert, used under the MIT licence.

## Trademarks

Codex, OpenAI, Claude, Anthropic, Gemini, Google, Windows, WebView2 and other names are trademarks of their owners. LAICA is an independent project, not affiliated with or endorsed by them; it launches tools you install yourself.