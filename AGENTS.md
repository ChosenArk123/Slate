# AGENTS.md — Slate Browser

## What Slate Is

Slate is a privacy-focused Windows desktop web browser. It renders web content via **WebView2** (Chromium) and builds its shell UI programmatically in **WinUI 3 / Windows App SDK** on **.NET 8**. The entire UI is code-constructed — there is no XAML markup for `MainWindow`.

**Technology stack:** .NET 8 · WinUI 3 (Windows App SDK 1.8) · WebView2 · System.Text.Json · x64-only · self-contained deployment · unpackaged (`WindowsPackageType=None`).

---

## Solution Structure

```
Slate.sln
├── Slate/            WinUI 3 application (WinExe, net8.0-windows10.0.19041.0, win-x64)
├── Slate.Core/       Pure domain library (net8.0, zero UI/OS dependencies)
└── Slate.Tests/      Headless unit tests (console exe, references Slate.Core only)
```

### Slate.Core — Domain Layer

Pure .NET 8 class library. No NuGet packages. No WinUI or WebView2 references. Platform-agnostic.

| File | Key Class(es) | Purpose |
|---|---|---|
| `Models.cs` | `BrowserTab`, `Workspace`, `BookmarkEntry`, `BrowserSettings`, `HistoryEntry`, `DownloadEntry`, `WindowPlacement`, `BrowserState` | All serializable domain data models. `BrowserState` is the root aggregate. Includes InPrivate flags, bookmark entries, and extended browsing settings. |
| `BrowserSession.cs` | `BrowserSession` | In-memory session manager. Tab/workspace lifecycle, invariant enforcement (100-tab cap, history 2000-cap, bookmarks 1000-cap, downloads 100-cap, 30 recently-closed). Tab movement/reordering, bookmark CRUD, history clearing/deletion, and InPrivate/temporary tab isolation. |
| `StateStore.cs` | `StateStore` | Atomic JSON persistence for `BrowserState`. Write-through temp file → `File.Replace` → backup. Resilient load with fallback to `.bak`. |
| `FaviconStore.cs` | `FaviconStore` | Disk-based favicon cache keyed by SHA-256 of normalized origin. PNG validation (signature, IHDR, dimension cap 512px). Per-origin `SemaphoreSlim` locking. Generation counter for safe cache clearing. |
| `Navigation.cs` | `Navigation` (static) | URL resolution (`Resolve`), search query building, `IsWebUrl`, `IsAllowedFrameUrl`, `IsSecureOrigin`, `WebOrigin`, `DisplayHost`, `IsLocalFileUrl`, `IsDangerousExtension`, `IsViewSourceUrl`. Blocks `javascript:`, `file:`, user-info spoofing, and dangerous executable file extensions. |
| `BrowserCommands.cs` | `BrowserCommand` (enum), `BrowserCommandRegistry` (static) | 47 browser commands and 47 keyboard shortcut definitions. `TryMatch`, `IsTextEditingShortcut` (pass-through filter for Ctrl+C/V/A/Z/X/Redo), and `GetShortcutLabel`. |
| `CredentialOrigin.cs` | `CredentialOrigin` (static) | Exact web origin canonicalization for passwords. Stricter than display navigation: normalizes Punycode/IDN, strips default ports, preserves non-default ports, rejects userinfo, whitespace, backslashes, and trailing-dot hostnames. |
| `CredentialVault.cs` | `CredentialVault`, `ICredentialProtector` | Platform-independent encrypted credential store. Atomic replace writes, `.bak` fallback, strict JSON schema validation, envelope verification, and revision-bound tamper resistance. Decrypts single requested entry on demand; fails closed on corruption. |
| `DownloadSafety.cs` | `DownloadSafety` (static) | Windows filename and destination sanitizer. Neutralizes directory traversal, Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`), NTFS alternate data streams (`:`), and UNC paths (`\\server\share`). Generates collision-free filenames (`name (1).ext`) and clamps length for MAX_PATH. |
| `OmniboxService.cs` | `OmniboxService` (static), `OmniboxSuggestion` | Multi-source autocomplete provider combining search queries, open tabs, bookmarks, and browsing history with deduplication and priority ranking. |
| `PasswordGenerator.cs` | `PasswordGenerator` (static) | Cryptographically secure CSPRNG password generator using `RandomNumberGenerator`. Guarantees representation across all character classes (lowercase, uppercase, digits, symbols) with configurable length (16–128 chars, default 24). |

### Slate — UI / Application Layer

WinUI 3 app. References `Microsoft.WindowsAppSDK`, `Microsoft.Web.WebView2`, and `Slate.Core`.

| File | Lines | Purpose |
|---|---|---|
| `App.xaml` / `App.xaml.cs` | 89 | Application entry point. Single-instance mutex (`Local\Slate.Browser.<username>`) with window foregrounding. Startup environment security audit (strips `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`, rejects `--remote-debugging-port`, `--no-sandbox`, `--disable-web-security`, etc.). Crash logging with credential token redaction. |
| `MainWindow.cs` | 1,130 | Shell construction, layout, title bar, sidebar rail, tab list, theming, window placement, keyboard shortcut registration (`AddShortcuts`), favicon UI cache, Omnibox suggestions popup, find bar layout, bookmarks bar layout, and window lifecycle. |
| `MainWindow.Browser.cs` | 1,293 | **All WebView2 interaction.** `TabRuntime` state, `InitializeWebViewAsync`, `NavigateAsync`, all `CoreWebView2` events, security hardening, downloads, sleeping tabs, split view, zoom controls, full screen, in-page find, print, view source, local file loading, and process crash recovery. |
| `MainWindow.Dialogs.cs` | 829 | Modal dialogs (command palette, history with item deletion & filtered clear, bookmarks manager, recently closed, workspaces, settings with 5 sections, granular clear browsing data with 5 time ranges, site info, TLS certificate viewer with X.509 field display, downloads). `SemaphoreSlim(1,1)` guards against overlapping dialogs. |
| `MainWindow.Passwords.cs` | 58 | Toolbar password button, save/update InfoBar prompt, account autofill flyout, password generation action, and password manager launcher. |
| `MainWindow.PasswordSmokeTests.cs` | 215 | In-process GUI integration tests for the password manager: DPAPI encryption, isolated-world injection, SPA navigation, and race mitigation. |
| `MainWindow.SmokeTests.cs` | 763 | In-process GUI integration test runner launched via `--smoke-test=<path>`. HTTP fixture server, screenshot capture, WCAG AA contrast checks, and security port inspection. |
| `BrowserCommandRouter.cs` | 251 | Multi-layer keyboard routing (`WH_KEYBOARD_LL` global hook + `WebView2.PreviewKeyDown` + WinUI accelerators). 80ms command debouncing, modal dialog suppression, text-editing key passthrough (`Ctrl+C/V/A/Z/X/Redo`), and command execution filtering. |
| `PasswordController.cs` | 268 | Per-WebView controller leveraging Chrome DevTools Protocol (`Page.createIsolatedWorld`, `Runtime.addBinding`, `Runtime.callFunctionOn.uniqueContextId`) for credential operations. Prevents page-world access and manages transient candidate tickets. |
| `PasswordDocument.js` | 72 | Embedded resource JavaScript executed in an isolated world to inspect visible form fields, detect login/generate forms, fill without triggering page JS accessors, and capture trusted form submissions. |
| `PasswordManagerUi.cs` | 106 | Full modal UI for password management: search, list, reveal with 30s auto-masking timer, clipboard copy with history/cloud roaming exclusion, and two-step confirm delete. |
| `SlateTheme.cs` | 341 | Procedural design system. Generates `Palette` from 6 accent themes (`Slate`, `Cobalt`, `Moss`, `Plum`, `Clay`, `Amber`) or `System` + dark/light mode. Hue vectors tint neutral surfaces. WCAG AA contrast enforcement (7:1 text, 4.5:1 secondary & accents). Pushes 21 semantic tokens into WinUI `ResourceDictionary`. |
| `WindowsCredentialProtector.cs` | 45 | Win32 DPAPI (`CryptProtectData`/`CryptUnprotectData`) implementation with pinned memory and explicit buffer zeroing. |

### Slate.Tests — Headless Test Suite

Console executable using a lightweight `Check`/`Assert` harness (no external test framework dependencies).

| File | Checks | Purpose |
|---|---|---|
| `Program.cs` | 66 | Tests covering `Navigation.Resolve`, frame URL filtering, local file safety, `BrowserSession` lifecycle (tab CRUD, workspace management, pinning, temporary tabs, InPrivate isolation, session restore, tab cap), `StateStore` round-trip and corruption recovery, `FaviconStore` (origin keying, PNG validation, concurrency, cache clearing, race conditions), `BrowserCommandRegistry` (shortcut matching, text-editing passthrough, display labels), `DownloadSafety` (traversal, device names, ADS, collision-free naming), and `OmniboxService` (ranking, deduplication). |
| `PasswordTests.cs` | 29 | Cryptographic unit tests for `CredentialOrigin` (canonicalization, port isolation, Punycode, invalid input rejection), `PasswordGenerator` (entropy, length, character-class guarantees), and `CredentialVault` (CRUD, conflict detection, envelope tamper resistance, durability, read-only backup fallback). |

---

## Build / Test / Run Commands

```powershell
# Full build + core tests (preferred):
.\scripts\build.ps1

# Build Release + core tests:
.\scripts\build.ps1 -Release

# Build + publish to artifacts\Slate\:
.\scripts\build.ps1 -Publish

# Headless core unit tests only (95 checks):
# When system dotnet is an SDK:
dotnet run --project Slate.Tests/Slate.Tests.csproj
# When using workspace embedded SDK:
.tools\dotnet\dotnet.exe run --project Slate.Tests/Slate.Tests.csproj

# GUI smoke tests (requires Release build, launches app headless with loopback fixture - 309 checks):
.\scripts\smoke-test.ps1

# Run the app:
.\scripts\run.ps1
```

`build.ps1` resolves the SDK under `.tools\dotnet\dotnet.exe` if present, restores NuGet dependencies, builds `Slate/Slate.csproj` (x64), and executes `Slate.Tests`. Smoke tests build Release, launch `Slate.exe --smoke-test=<output.json>`, wait up to 120s, and verify the JSON result.

---

## Architectural Boundaries

1. **Slate.Core owns all domain logic.** Models, session lifecycle, URL resolution, security policy (frame filtering, scheme validation, dangerous extension blocking), settings normalization, favicon caching, keyboard command definitions, download path safety, omnibox suggestion ranking, password generation, and encrypted credential vault policy live here. Zero UI or OS dependencies.
2. **Slate owns all UI and WebView2 integration.** Shell construction, theming, WebView2 lifecycle, event wiring, dialog presentation, keyboard hook installation, favicon rendering, DPAPI P/Invoke, and CDP password document injection.
3. **Boundary is enforced by project references.** `Slate.Core` has zero NuGet dependencies. It cannot reference WinUI or WebView2 types. `Slate.Tests` references only `Slate.Core`.

---

## MainWindow Partial-Class Organization

`MainWindow` is a `sealed partial class` split across 6 files totaling **4,288 lines**:

| File | Lines | Responsibility |
|---|---|---|
| `MainWindow.cs` | 1,130 | Shell layout, sidebar, tab list, theming (`ApplyAppearance`, `UpdateThemeColors`), window placement, keyboard shortcut registration (`AddShortcuts`), favicon UI cache, Omnibox suggestions popup, find bar layout, bookmarks bar layout, and `Shutdown()`. |
| `MainWindow.Browser.cs` | 1,293 | **All WebView2 interaction.** `TabRuntime` state, `InitializeWebViewAsync`, `NavigateAsync`, all `CoreWebView2` events, security hardening, downloads, sleeping tabs, split view, zoom controls, full screen, in-page find, print, view source, local file loading, and process crash recovery. |
| `MainWindow.Dialogs.cs` | 829 | All modal dialogs. `SemaphoreSlim(1,1)` guards against overlapping dialogs. Command palette, history with individual item deletion, bookmarks manager, workspaces, settings (5 sections), granular clear data (5 time ranges), site info, TLS certificate viewer, and downloads. |
| `MainWindow.SmokeTests.cs` | 763 | GUI integration test runner. Only executes when `--smoke-test=` is passed. HTTP fixture server, screenshot capture, WCAG contrast checks, and security port inspection. |
| `MainWindow.Passwords.cs` | 58 | Toolbar password button, save/update InfoBar prompt, account autofill flyout, password generation action, and password manager launcher. |
| `MainWindow.PasswordSmokeTests.cs` | 215 | In-process GUI integration tests for the password manager: DPAPI encryption, isolated-world injection, SPA navigation, and race mitigation. |

---

## WebView2 Lifecycle & Security Hardening

1. **Environment:** Lazy singleton `CoreWebView2Environment` created via `GetEnvironmentAsync()`. User data at `<ProfileDir>\WebView2`.
2. **Creation:** `new WebView2()` in `ShowTabAsync()`. Deferred — `slate://newtab` pages render native XAML, no WebView2 allocated.
3. **Initialization:** `EnsureCoreWebView2Async(environment)` in `InitializeWebViewAsync()`. Configures `CoreWebView2Settings`:
   - `AreHostObjectsAllowed = false`
   - `IsWebMessageEnabled = false`
   - `IsPasswordAutosaveEnabled = false`
   - `AreDevToolsEnabled = settings.DeveloperToolsEnabled`
   - `IsStatusBarEnabled = false`
4. **Navigation & HTTPS-First:** `NavigateAsync(input)` → `Navigation.Resolve()` → `core.Navigate(url)`. Public HTTP is upgraded to HTTPS by default; an explicit warning is displayed if unencrypted HTTP is loaded. The loopback exemption (permitting HTTP without warning) is strictly limited to `localhost`, `*.localhost`, `127.0.0.0/8`, and `::1`. LAN IPs (`192.168.x.x`, `10.x.x.x`) and mDNS (`.local`) are rejected from loopback status. Silent HTTPS->HTTP downgrades across redirects, popups, and new tabs are blocked.
5. **Security policies & startup audits:**
   - `StartupSecurity.AuditStartupEnvironment`: scrubs dangerous WebView2 environment variables (`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`, `WEBVIEW2_BROWSER_EXECUTABLE_FOLDER`, `WEBVIEW2_USER_DATA_FOLDER`, `WEBVIEW2_PIPE_FOR_REMOTING`, etc.) while leaving benign variables intact.
   - `StartupSecurity.ValidateCommandLine`: rejects dangerous Chromium switches (sandbox bypass, remote debugging, site-isolation disabling such as `--disable-features=IsolateOrigins,site-per-process`) and unrecognized flags.
   - `AuditRegistryPolicies`: audits `SOFTWARE\Policies\Microsoft\Edge\WebView2` and rejects unauthorized binary or user data folder redirects.
   - `NavigationStarting`: cancels non-http/https/about:blank/file navigation.
   - `FrameNavigationStarting`: filters iframe destinations via `Navigation.IsAllowedFrameUrl`.
   - `LaunchingExternalUriScheme`: always cancelled to prevent arbitrary protocol handler exploitation.
   - `ServerCertificateErrorDetected`: cancels navigation, marks `CertificateError`, and captures `LastCertificate` for user inspection.
   - `NewWindowRequested`: enforces user-gesture check + burst rate limit (5 popups / 10s per host, 10 global), and blocks HTTP downgrade popups.
   - `PermissionRequested`: checks origin + user gesture + per-type policy + burst rate limit (3 prompts / 1 min per host, 6 global). Sets `HasActivePermissionPrompt` to prevent tab discard during active user prompts.
6. **InPrivate isolation:** InPrivate tabs (`IsPrivate = true`) initialize with an isolated profile (`IsInPrivateModeEnabled = true`). They share no cookies, storage, or cache with regular tabs, record zero history, omit session persistence, and do not attach a password controller.
7. **Disposal:** `DisposeRuntime(id)` removes view, detaches `CommandRouter`, and calls `Close()`. Runtimes with active downloads are kept alive until downloads complete.

---

## Password Management & DPAPI Subsystem

1. **Storage format:** `<ProfileDir>/credentials.v1.json` (max 16 MiB, 10,000 entries). Versioned schema with metadata (stable GUID, canonical origin, username, timestamps, monotonically increasing revision) and `ProtectedSecret` (base64-encoded DPAPI ciphertext).
2. **Envelope protection:** Plaintext payload is an encrypted envelope containing `{ Id, Origin, Username, Revision, Password }`. Decryption verifies origin, username, ID, and revision against metadata to prevent ciphertext transplant attacks.
3. **Windows DPAPI:** `WindowsCredentialProtector` calls Win32 `CryptProtectData` with `CRYPTPROTECT_UI_FORBIDDEN` in the current user context. Memory buffers containing plaintext are pinned and zeroed via `CryptographicOperations.ZeroMemory` / `Marshal.WriteByte`.
4. **CDP isolated world injection:** `PasswordController` uses Chrome DevTools Protocol to create a uniquely named isolated JavaScript world (`Page.createIsolatedWorld`) and binds `__slateCredentialSubmit` (`Runtime.addBinding`). Scripts execute via `Runtime.callFunctionOn` targeting `uniqueContextId` and a random per-document nonce. Page-world JavaScript cannot inspect controller state, intercept filled credentials, or Forge submit events.
5. **Capture & save confirmation:** Submission captures credentials as transient candidates. Save/update is only offered if authentication succeeds (form element removed or successful same-origin navigation) and always requires an explicit user click on the native InfoBar prompt.
6. **Password manager UI:** Modal dialog with instant filtering, masked display (`••••••••••••`), 30-second auto-masking timer on reveal, history/cloud-roaming-excluded clipboard copy, and two-step confirm delete.

---

## Bookmarks Subsystem

1. **Model & storage:** `BookmarkEntry` (`Id`, `Title`, `Url`, `Folder`, `CreatedAt`) embedded directly in `BrowserState.Bookmarks` (capped at 1,000 entries). Persisted atomically in `session.json`.
2. **Operations:** `BrowserSession` provides `IsBookmarked`, `AddBookmark`, `RemoveBookmark`, `RemoveBookmarkByUrl`, and `ToggleBookmark`. Missing titles fallback to hostnames.
3. **UI integration:**
   - Interactive toolbar bookmark toggle button (`_bookmarkButton`) with filled/unfilled visual state.
   - Optional horizontal bookmarks bar (`_bookmarksBar`, `_bookmarksList`) below toolbar, toggled in Settings or view.
   - Dedicated Bookmarks manager dialog (`ShowBookmarksAsync`) with real-time search, "Bookmark current tab", and per-bookmark deletion.
   - Autocomplete integration: `OmniboxService` returns matching bookmarks with `OmniboxSuggestionKind.Bookmark`.
4. **Shortcuts:** `Ctrl+D` (toggle bookmark current tab), `Ctrl+Shift+O` (open bookmarks manager).

---

## Download Safety & File Handling

1. **Path sanitization (`DownloadSafety.SanitizeFileName`):**
   - Strips directory traversal tokens (`..`, `/`, `\`).
   - Neutralizes Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`) by prefixing `_`.
   - Strips NTFS alternate data streams (`:`).
   - Replaces invalid Windows filename characters with `_`.
   - Clamps filename length to 200 characters for MAX_PATH compatibility while preserving extensions.
2. **Destination resolution (`DownloadSafety.GetSafeDestinationPath`):**
   - Strictly enforces destination containment within the designated download directory.
   - Generates collision-free filenames (`name (1).ext`) without overwriting existing files.
3. **Local directory safety (`DownloadSafety.IsSafeLocalDirectory`):**
   - Rejects UNC network paths (`\\server\share`, `//server/share`) to prevent SMB credential and NTLM hash leakage.
   - Enforces rooted local drive paths (`C:\...`).
4. **Mark-of-the-Web (`DownloadSafety.AttachZoneIdentifier`, `DownloadSafety.FormatZoneIdentifier`):**
   - Attaches `ZoneId=3` (Internet) via NTFS Alternate Data Stream (`Zone.Identifier`) to invoke Windows SmartScreen/Defender inspection on downloaded files.
   - Strictly sanitizes `HostUrl` and `ReferrerUrl` (strips `\r`, `\n`, `\0`, `[`, `]`, control characters, and clamps to 2048 chars) to eliminate INI injection downgrades (`\r\nZoneId=0`).
   - Strictly best-effort and non-destructive: fails safe without deleting or failing downloads on FAT32, exFAT, ReFS, locked files, or symlink/reparse points.
5. **Local file opening (`Navigation.IsLocalFileUrl`, `Navigation.IsDangerousExtension`):**
   - Rejects UNC paths.
   - Rejects dangerous executable and script extensions (`.exe`, `.bat`, `.cmd`, `.ps1`, `.vbs`, `.msi`, `.dll`, `.com`, `.scr`, `.reg`, `.hta`, `.cpl`, `.pif`).
   - Allows safe document inspection (`.html`, `.htm`, `.txt`, `.pdf`, `.json`, etc.).

---

## Tab & Session Lifecycle

- **Creation:** `BrowserSession.AddTab()` enforces 100-tab cap. Supports `temporary` and `isPrivate` flags. `slate://newtab` tabs skip WebView2 allocation.
- **Activation:** `BrowserSession.Activate()` updates `ActiveWorkspace.ActiveTabId` and `LastAccessed`. Non-visible WebViews parked in `_parkedViews` (remain alive, do not render).
- **Sleeping (Suspend):** `_sleepTimer` (30s interval) checks tabs idle > `SleepAfterMinutes` (default 15). Calls `core.TrySuspendAsync()`. Tab runtime remains alive; DOM, JS execution context, and WebSockets are preserved. Guarded: no suspend if tab is visible, loading, playing audio, or downloading.
- **Discarding (Gaming Efficiency / Memory Pressure):** `GamingEfficiencyService` and `TabManagerService.DiscardInactiveRuntimes`. Completely disposes `WebView2` and detaches from UI tree, terminating OS renderer processes and reclaiming >70% RAM.
  - **State Survival:** Only `BrowserTab` model survives (`Url`, `Title`, `Favicon`, `IsPinned`, `IsPrivate`, `LastAccessed`). In-memory DOM, script states, WebSockets, and navigation back/forward history are destroyed.
  - **Protection Policy:** Active tab, split tab, focused tab, audio-playing tabs (`IsDocumentPlayingAudio == true`), active downloads, active permission prompts/dialogs, loading tabs, and recently accessed tabs (< 30s) are protected.
  - **Candidate Selection:** Evaluated in Least Recently Used (LRU) order via `TabLifecyclePolicy.SelectDiscardCandidates`.
  - **Reclamation:** Immediately after discard, synchronous `GC.Collect()` and `TrimWorkingSet()` (`SetProcessWorkingSetSize(-1, -1)`) reclaim physical RAM on the UI thread.
- **Wake / Reactivation:** Selecting a sleeping tab calls `core.Resume()`. Selecting a discarded tab recreates the `WebView2` runtime lazily and re-navigates to `tab.Url`.
- **Close:** `BrowserSession.CloseTab()` archives to `RecentlyClosed` (30 max). Temporary and InPrivate tabs are never archived. If tab was the last in workspace, `EnsureActiveTab` creates a new-tab placeholder.
- **Restore:** `BrowserSession.RestoreClosed()` re-adds with `IsSleeping=true`, activates.
- **Session save:** Debounced 650ms `DispatcherTimer`. `StateStore.Save()` writes atomic temp → replace. Temporary and InPrivate tabs filtered out of persistence. Final synchronous save on `Shutdown()`.
- **Session load:** `StateStore.Load()` tries `session.json`, falls back to `.bak`. `BrowserSession.Normalize()` sanitizes everything: drops orphans, resets favicons/loading/sleeping, clamps settings, filters invalid bookmarks/history, marks active downloads as `"Interrupted"`, and sanitizes download file paths.

---

## Settings Architecture

- **Model:** `BrowserSettings` in `Slate.Core/Models.cs`.
  - `SearchEngine`: "DuckDuckGo", "Google", or "Bing".
  - `Theme`: "System", "Light", or "Dark".
  - `AccentTheme`: "Slate", "Cobalt", "Moss", "Plum", "Clay", "Amber", or "System".
  - `SleepAfterMinutes`: 0–120 minutes (0 = never).
  - `RestoreSession`: boolean.
  - `SidebarCollapsed`: boolean.
  - `ReduceMotion`: boolean.
  - `DefaultZoomPercent`: 25–500% (default 100).
  - `DeveloperToolsEnabled`: boolean.
  - `ShowBookmarksBar`: boolean (default true).
  - `DownloadPath`: optional custom local folder path.
  - `AskDownloadLocation`: boolean.
- **Persistence:** Embedded in `BrowserState` → serialized in `session.json` via `StateStore`.
- **UI:** Settings dialog in `MainWindow.Dialogs.cs` (`ShowSettingsAsync`) organized into 5 clear sections: Appearance, Browsing, Tabs, Privacy / Data, and Profile.

---

## Theme / Design System

`SlateTheme.cs` implements a procedural color system:

1. A `NeutralPalette` provides base gray-scale colors for dark/light modes.
2. 6 curated accent themes (`Slate`, `Cobalt`, `Moss`, `Plum`, `Clay`, `Amber`) define RGB `HueVector` offsets. `"System"` derives its vector dynamically from the Windows accent color.
3. `ApplyChroma()` tints neutral surfaces with tiered chroma weights (Page=2.0 → Pressed=13.0).
4. `EnsureContrast()` enforces WCAG AA ratios (7:1 for primary text, 4.5:1 for secondary text and accent).
5. `SlateTheme.Create(accent, dark)` → `Palette` record with 21 semantic colors and computed brush properties.
6. `ApplyResources()` pushes semantic tokens (`SlateSurfaceBaseBrush`, `SlateAccentPrimaryBrush`, etc.) and overrides WinUI system brushes into `ResourceDictionary`.
7. Consumed by `MainWindow.UpdateThemeColors()` and `SetWebTheme()` (for WebView2 `PreferredColorScheme`).

---

## Keyboard / Command Architecture

Three-layer routing (in priority order):

1. **`WH_KEYBOARD_LL` global hook** (`BrowserCommandRouter`): Pre-empts Chromium key consumption. Checks `IsSlateForeground()`, ignores Win key combos, filters text-editing shortcuts, debounces 80ms, dispatches via `DispatcherQueue`.
2. **`WebView2.PreviewKeyDown`** (`BrowserCommandRouter.AttachWebView2`): In-process fallback if global hook missed the event.
3. **WinUI `KeyboardAccelerators`** (`MainWindow.AddShortcuts`): Standard XAML accelerators as tertiary routing.

All three layers check `_isModalDialogOpen()`, `BrowserCommandRouter.CanExecuteFilter`, and `BrowserCommandRegistry.IsTextEditingShortcut()` before routing.

---

## Concurrency / Threading

- **UI thread affinity:** `BrowserSession`, `MainWindow`, all UI state mutations run on the WinUI `DispatcherQueue` thread. No internal locks on session state — synchronization is by single-threaded dispatch.
- **FaviconStore:** `ConcurrentDictionary<string, SemaphoreSlim>` for per-origin locks. `Volatile.Read`/`Interlocked.Increment` on `_generation` for lock-free cache invalidation. `ClearAsync` acquires all origin locks before purging.
- **CredentialVault:** `SemaphoreSlim(1, 1)` serializes all vault operations. Disk I/O and crypto run on background thread via `Task.Run`.
- **StateStore:** File-level atomicity via temp-file + replace. No in-memory locks.
- **Timers:** `_saveTimer` (650ms debounce), `_sleepTimer` (30s), `_sidebarAnimation` (16ms ease-out). All `DispatcherTimer` — fire on UI thread.
- **Modal dialogs:** `SemaphoreSlim(1, 1)` prevents overlapping dialogs. `_activeDialog` flag suppresses keyboard routing during modals.
- **Downloads:** Runtimes kept alive past tab closure while downloads are active. Download tracking via `_downloadOperations` dictionary.

---

## Agent Working Rules

1. **Inspect existing abstractions before creating new ones.** Check `Navigation`, `DownloadSafety`, `FaviconStore`, `BrowserSession`, `OmniboxService`, `PasswordGenerator`, `CredentialVault`, `BrowserCommandRegistry`, and `SlateTheme` — they likely already handle your use case.
2. **Do not place substantial browser or security logic directly in MainWindow event handlers.** Navigation validation, URL resolution, scheme filtering, download sanitization, and session invariants belong in `Slate.Core`. MainWindow should delegate to domain classes.
3. **Preserve Slate.Core / Slate separation.** `Slate.Core` must remain free of UI framework and OS references. If your logic doesn't need WinUI or WebView2, it belongs in `Slate.Core` where it can be tested headlessly.
4. **Prefer existing utilities and persistence conventions.** Use `Navigation.Resolve` for URL handling, `DownloadSafety` for file paths, `FaviconStore` for favicon caching, `StateStore` for persistence, `CredentialVault` for passwords, and `BrowserCommandRegistry` for shortcuts. Do not create parallel implementations.
5. **Run the relevant tests after architectural changes.** Run `.tools\dotnet\dotnet.exe run --project Slate.Tests/Slate.Tests.csproj` (or `.\scripts\build.ps1`) after any change to Slate.Core. Run `.\scripts\smoke-test.ps1` after UI/WebView2 changes.
6. **Do not silently weaken existing security controls.** The scheme filtering, frame restrictions, popup rate limiting, permission gating, host object disabling, web messaging disabling, TLS error blocking, dangerous extension filtering, UNC path rejection, and isolated CDP password contexts are intentional. Any relaxation must be explicit and justified.
7. **Do not introduce NuGet dependencies without a concrete architectural need.** Slate.Core currently has zero dependencies. Slate has only WindowsAppSDK and WebView2. Keep the dependency surface minimal.
8. **Respect the threading model.** `BrowserSession` and all UI state are UI-thread-affine. `FaviconStore` has its own concurrency model with per-origin locks. `CredentialVault` uses an internal semaphore. Don't add `lock` statements to `BrowserSession` — use `DispatcherQueue` dispatch instead.
9. **New testable logic should go in Slate.Core with tests in Slate.Tests.** The headless test harness (`Slate.Tests/Program.cs` and `Slate.Tests/PasswordTests.cs`) is cheap to extend. Integration tests requiring WebView2 go in `MainWindow.SmokeTests.cs` or `MainWindow.PasswordSmokeTests.cs`.
10. **Temporary and InPrivate tabs must remain ephemeral.** They are excluded from history recording, filtered from session persistence in `StateStore.Save`, and discarded on restart. InPrivate tabs must never persist or attach a password controller.
11. **Password secrets must never be leaked to logs, diagnostics, or page contexts.** DPAPI envelopes must be verified before secrets are returned. Secrets must never be placed in `BrowserState`, `crash.log`, or page-accessible globals. Plaintext byte buffers must be zeroed immediately after transformation.
12. **File operations must guard against Windows path vulnerabilities.** Always sanitize filenames via `DownloadSafety.SanitizeFileName`, validate directories with `DownloadSafety.IsSafeLocalDirectory`, reject UNC network paths, and check extensions against `Navigation.IsDangerousExtension`.
