# Slate

A native Windows browser prototype built with C#, .NET 8, WinUI 3, Windows App SDK, and Microsoft WebView2. No AI features.

## Run

The ready-to-run build is in **`artifacts/Slate/Slate.exe`**. Keep the whole folder together. The .NET and Windows App SDK runtimes are included; Microsoft's Evergreen WebView2 Runtime must be installed. Target: Windows 11 x64 (Windows 10 1809+ API minimum; Windows 11 is the tested environment).

From source, use PowerShell:

```powershell
.\scripts\build.ps1
.\scripts\run.ps1
```

The scripts use the workspace SDK under `.tools/dotnet` when present, otherwise an installed .NET 8 SDK. Dependency restore needs access to NuGet. Visual Studio is optional. Open `Slate.sln` and choose x64 when using it.

```powershell
# Release folder with all application dependencies
.\scripts\build.ps1 -Publish

# Headless domain and security checks (104 checks)
.tools\dotnet\dotnet.exe run --project Slate.Tests/Slate.Tests.csproj

# Real WinUI/WebView2 integration checks, with a fresh isolated profile (319 checks)
.\scripts\smoke-test.ps1
```

## What works

- **Native shell & theming**: Native Mica shell, custom title bar, dark/light/system appearance, six restrained built-in accent palettes (Slate, Cobalt, Moss, Plum, Clay, Amber) plus Windows accent support, compact vertical tabs, animated 64 px sidebar rail, compact toolbar, and native new-tab page.
- **Distraction-free new-tab page**: Clean new-tab composition with an integrated search field, local time/date, and frequently visited sites derived from browsing history; no remote content or promotional copy.
- **Omnibox & autocomplete**: URL and search resolution; back, forward, reload, hard reload, and stop; page titles, favicons, loading states, and WebView process recovery. Unified autocomplete suggestions rank search engine queries, open tabs, bookmarks, and history entries without duplicates.
- **Tab & workspace lifecycle**: Create, close, duplicate, pin, move up/down, reorder, switch, and restore tabs. Close other tabs or close tabs below. Search tabs across workspaces using the command palette. Create, rename, switch, and remove workspaces. Removing a workspace preserves its tabs in another workspace.
- **Session recovery**: JSON session recovery with atomic writes and a backup; persistent windows, tabs, workspaces, history, downloads, bookmarks, and settings.
- **Memory & performance**: Lazy WebViews. Inactive tabs suspend (sleep) after a configurable interval (0–120 minutes), preserving live DOM state. Under gaming mode or memory pressure, inactive background tabs are discarded in Least Recently Used (LRU) order, completely disposing WebView2 runtimes and terminating OS renderer processes (>70% RAM reduction). Visible, split, loading, audible, downloading, prompt-active, and recently used tabs (<30s) are strictly protected.
- **Gaming efficiency service**: Automatic and manual gaming detection deprioritizes background activities, suspends non-essential timers, discards eligible background tabs, runs immediate garbage collection, and trims the process working set.
- **Side-by-side split browsing**: Side-by-side browsing with equal pane widths. Toolbar navigation targets the focused pane. Use a tab's context menu or `Ctrl+Shift+S` to split.
- **Bookmarks system**: Quick bookmark toggle (`Ctrl+D`), dedicated Bookmarks manager (`Ctrl+Shift+O`) with live filtering, and an optional native bookmarks bar below the toolbar toggleable via settings.
- **InPrivate browsing**: Dedicated InPrivate tabs (`Ctrl+Alt+N`) backed by isolated WebView2 profiles (`IsInPrivateModeEnabled = true`). Completely omitted from history, session persistence, and recently closed lists, with password capture disabled.
- **In-page search**: Native find bar (`Ctrl+F`, `F3`, `Shift+F3`) using the `CoreWebView2Find` API with highlight-all match indicators, current index tracking, and keyboard navigation.
- **Page zoom & display**: Zoom in/out/reset (`Ctrl++`, `Ctrl+-`, `Ctrl+0`), configurable default zoom level (25%–500%), and interactive toolbar zoom percentage badge. Full screen mode toggle (`F11`) with automatic handling for HTML5 full-screen media elements.
- **Local files & inspection**: Safe local file opening (`Ctrl+O`) with strict blocking of dangerous executable/script extensions and UNC network shares. Save complete page to HTML (`Ctrl+S`), native print dialog (`Ctrl+P`), view source (`Ctrl+U` / `view-source:`), and Developer Tools (`F12`, `Ctrl+Shift+I`).
- **Security & TLS inspection**: Security toolbar button with connection status, origin details, and dedicated TLS certificate viewer dialog showing X.509 Subject, Issuer, Validity dates, Thumbprint, and Signature algorithm.
- **Downloads manager**: Native WebView download UI plus a dedicated downloads dialog (`Ctrl+J`) with progress tracking, pause/resume/cancel, and open-folder actions. Download safety validates destination paths against traversal, NTFS alternate data streams, and Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`).
- **Granular data clearing**: Clear browsing data modal with 5 time ranges (Last hour, Last 24 hours, Last 7 days, Last 4 weeks, All time) and selective checkboxes for history, downloads, cookies/site data, cached images/files, and saved passwords.
- **Local first-party passwords**: Exact-origin single-account autofill with an opt-out, native multi-account selection, confirmed save/update, secure CSPRNG generation, and searchable management with deliberate reveal, copy, edit, and deletion. Windows current-user DPAPI protects each secret in a separate vault.
- **Safe browser-data import**: Settings can import Chrome, Edge, and Firefox password CSV exports through a bounded parser, exact-origin validation, a metadata-only preview, default-preserve conflict policy, and one atomic revision-checked DPAPI vault write. The adapter boundary is ready for future bookmark/history/settings formats without adding parallel stores.
- **Organized settings**: Appearance, Browsing, Downloads, Tabs, Privacy / Data, Profile, and About sections expose theme/accent, default zoom, search, bookmark bar, developer tools, download behavior, sleeping/session restore, password autofill/management, clearing, import, and profile access.

## Keyboard

### Navigation & Addressing
| Shortcut | Action |
| --- | --- |
| Ctrl+L | Focus address / search omnibox |
| Alt+Left | Go back |
| Alt+Right | Go forward |
| Ctrl+R / F5 | Reload page |
| Ctrl+F5 / Ctrl+Shift+R | Hard reload (bypass cache) |
| Escape | Stop loading (or close find bar / dismiss dialog) |

### Tab & Workspace Management
| Shortcut | Action |
| --- | --- |
| Ctrl+T | New tab |
| Ctrl+W | Close active tab |
| Ctrl+Shift+T | Reopen recently closed tab |
| Ctrl+Shift+D | Duplicate active tab |
| Ctrl+Shift+PageUp | Move tab up in list |
| Ctrl+Shift+PageDown | Move tab down in list |
| Ctrl+Tab | Next tab |
| Ctrl+Shift+Tab | Previous tab |
| Ctrl+1…8 | Select tab 1 through 8 |
| Ctrl+9 | Select last tab |
| Ctrl+Shift+S | Toggle split view |
| Ctrl+Shift+N | New temporary tab |
| Ctrl+Alt+N | New InPrivate tab |

### View & Layout
| Shortcut | Action |
| --- | --- |
| Ctrl+B | Collapse / expand sidebar |
| F11 | Toggle full screen |
| Ctrl++ / Ctrl+= | Zoom in |
| Ctrl+- | Zoom out |
| Ctrl+0 | Reset zoom to default |

### Page Actions & Tools
| Shortcut | Action |
| --- | --- |
| Ctrl+F | Find in page |
| F3 | Find next match |
| Shift+F3 | Find previous match |
| Ctrl+D | Bookmark current page |
| Ctrl+Shift+O | Open bookmarks manager |
| Ctrl+O | Open local file |
| Ctrl+S | Save page to file |
| Ctrl+U | View page source |
| Ctrl+P | Print page |

### System & Inspection
| Shortcut | Action |
| --- | --- |
| Ctrl+K / Ctrl+Shift+P | Command palette |
| Ctrl+H | History |
| Ctrl+J | Downloads |
| Ctrl+, | Settings |
| F12 / Ctrl+Shift+I | Open Developer Tools |

In the command palette, type to filter; use Up/Down and Enter. Right-click any tab for context actions: pinning, duplication, moving up/down, sleeping, split view, closing others, and closing tabs below.

## State and architecture

`Slate.Core` is a pure .NET 8 domain library (zero UI or OS dependencies) containing data models, session lifecycle, atomic JSON state storage, favicon caching, URL resolution, download safety, omnibox ranking, CSPRNG password generation, browser-data import policy, and encrypted credential vault policies.

`Slate` is the WinUI 3 desktop application layer built with Windows App SDK and WebView2. It contains the code-constructed shell (no XAML for `MainWindow`), multi-layer keyboard command routing (`WH_KEYBOARD_LL` + `PreviewKeyDown` + WinUI accelerators), procedural theming, modal dialogs, and isolated CDP password controllers.

`Slate.Tests` is a dependency-free console test harness verifying core invariants headlessly. The application's explicit `--smoke-test=<output.json>` mode launches an in-process GUI integration test harness using loopback HTTP fixtures and isolated profile directories.

### Storage & Profile Layout

Normal data lives in `%LOCALAPPDATA%/Slate`:
- `session.json` and `session.json.bak`: Persistent state (workspaces, tabs, history, bookmarks, downloads, settings, window placement) written atomically via temporary files with write-through flush and backup rotation.
- `credentials.v1.json` and `credentials.v1.json.bak`: Encrypted password vault. Each entry holds metadata (origin, username, created, updated, last used, revision) and a DPAPI-encrypted envelope binding origin, username, ID, and revision to the secret.
- `favicons/`: Disk-based cache of validated origin-keyed PNG files.
- `WebView2/`: Standard Evergreen Chromium user data directory (cookies, storage, cache).
- `crash.log`: Startup and runtime exception log with automated redaction of credentials, auth tokens, and secret parameters.

A named per-user mutex (`Local\Slate.Browser.<username>`) prevents two normal instances from writing the same profile concurrently.

## Prototype limits

- **Temporary tabs vs. InPrivate tabs**: Temporary tabs (`Ctrl+Shift+N`) share regular cookies and session storage; they are merely omitted from history and restart restoration. InPrivate tabs (`Ctrl+Alt+N`) run in an isolated WebView2 profile with separated storage and disabled credential capture.
- **Session restore & tab state**: Session restore recovers the last navigated URL, not deep backward/forward history stacks, unsaved form contents, or scroll positions. Live tab switching and background suspension (sleep) preserve full DOM and execution state. Tab discard (triggered under memory pressure or gaming mode) fully tears down the underlying WebView2 runtime; only the `BrowserTab` model (`Url`, `Title`, `Favicon`, `IsPinned`, `IsPrivate`) survives in memory. Reactivating a discarded tab re-creates the WebView2 runtime and reloads its URL fresh.
- **Downloads**: Download records persist across restarts, but interrupted downloads cannot resume across application restart boundaries. WebView2 manages live download resumption within a session.
- **Window opening & external protocols**: Popup URLs open as new tabs with rate-limiting and user-gesture checks; window-opener-dependent payment or OAuth popup flows may require additional handling. External protocol handlers are strictly blocked; only HTTP, HTTPS, safe local files, and `view-source:` are handled.
- **Split panes**: Split panes use fixed 50/50 equal width distributions.
- **Extensions & packaging**: Browser extensions, default-browser protocol association, MSIX signing, auto-update, and cross-device synchronization are outside the prototype scope.
- **Import scope**: Slate currently imports passwords from exported Chrome, Edge, and Firefox CSV files. It does not read live browser profiles or yet import bookmarks, history, settings, passkeys, or password exports from Slate.

## Validation

- **Core domain suite**: **104 checks passed**. Tests cover navigation resolution, search generation, URL scheme security, frame filtering, `BrowserSession` lifecycle and caps, atomic persistence and backup recovery, `FaviconStore` concurrency and cache clearing, `BrowserCommandRegistry` shortcut matching, `DownloadSafety` filename sanitization and traversal prevention, `OmniboxService` ranking, browser-data import parsing/policy, and `CredentialVault` cryptographic integrity and tamper resistance.
- **Native GUI integration suite**: **319 checks passed**. Exercises the full WinUI 3 and WebView2 runtime: DPAPI user-bound protection, atomic password import, exact-origin autofill and opt-out, strict annotated-form gating, CDP isolated world verification, SPA navigation and race defenses, credential editing, WCAG AA contrast ratios across all 6 accent themes and light/dark modes, visual snapshots, navigation, split view, sleeping/resumption, real local downloads, seven-section Settings, modal dialogs, and workspace switching.

Implementation references: [unpackaged WinUI deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app), [WebView2 lifecycle](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/environment-controller-core), [WinUI visibility implementation](https://github.com/microsoft/microsoft-ui-xaml/blob/main/controls/dev/WebView2/WebView2.cpp), and [keyboard accelerators](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-accelerators).
