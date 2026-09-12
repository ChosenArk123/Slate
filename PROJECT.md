# Project: Slate Browser Roadmap Hardening (Phases 2–10)

## Architecture
- **Slate.Core**: Pure .NET 8 headless domain library. Contains credential vault models and encryption boundaries, canonical origin normalization, CSPRNG password generation, browser data import architecture and CSV streaming parser, session state serialization, and download safety validation. Zero WinUI or WebView2 dependencies.
- **Slate (App)**: Windows App SDK / WinUI 3 desktop application. Hosts Microsoft WebView2 runtimes, tab lifecycle manager, isolated-world CDP password controller, modal password manager UI, 7-section settings dialog, command router, navigation guards, and TLS probing.
- **Slate.Tests**: Headless xUnit/NUnit test suite covering cryptographic properties, tamper resistance, origin normalization, CSV parsing edge cases, settings persistence, and lifecycle invariants.
- **Scripts**: Automation suite for release compilation (`build.ps1`) and in-process native GUI smoke testing (`smoke-test.ps1`).

## Feature Inventory
Every feature identified during the Survey phase is mapped below with its assigned milestone.

| # | Feature | Description | Milestone | Source |
|---|---------|-------------|-----------|--------|
| 1 | Hardened Vault Schema v2 | `VaultId`, `VaultRevision`, `LastModified`, `EntriesDigest` in vault header | M1 | R1, Survey 1 |
| 2 | Cryptographic Envelope Binding | Bind `VaultId`, `Created`, `Updated`, `Revision`, `Id`, `Origin`, `Username`, `Password` inside DPAPI envelope | M1 | R1, Survey 1 |
| 3 | Contextual DPAPI Entropy | Derive DPAPI entropy from `(VaultId, Id, Origin, Username)` to prevent cross-vault transplants | M1 | R1, Survey 1 |
| 4 | Monotonic Rollback Resistance | External rollback anchor (`vault.anchor` / session) detecting full-vault replacement | M1 | R1, Survey 1 |
| 5 | Full-Vault State Commitment | Sorted SHA-256 `EntriesDigest` preventing single-entry rollback, insertion, or deletion | M1 | R1, Survey 1 |
| 6 | Metadata Integrity on Load/List | Validate entries against `EntriesDigest` on load; detect tampering before reveal | M1 | R1, Survey 1 |
| 7 | Two-Stage Schema Migration | Seamless v1 to v2 migration preserving legacy `credentials.v1.json` safely | M1 | R1, Survey 1 |
| 8 | Fail-Closed Corruption Handling | Read-only fallback to `.bak` preserving damaged primary; no write corruption | M1 | R1, Survey 1 |
| 9 | Secret Redaction in Exceptions | Ensure zero plaintext secrets in exception messages, logs, or crash artifacts | M1 | R1, Survey 1 |
| 10 | Vault Security Unit Tests | Headless tests for rollback, transplant, corruption, tamper, and migration | M1 | R1, Survey 1 |
| 11 | `BrowserDataKinds` Enum | Flags enum defining Passwords, Bookmarks, History, Settings | M2 | R2, Survey 2 |
| 12 | `BrowserDataImportDescriptor` | Descriptor record providing metadata, category, extensions, availability flag | M2 | R2, Survey 2 |
| 13 | `IBrowserDataImporter` Interface | Extensible importer interface (`Descriptor`, `CanImport`, `Parse`) | M2 | R2, Survey 2 |
| 14 | Import Descriptor Registry | `BrowserDataImportService` registry exposing functional and future categories | M2 | R2, Survey 2 |
| 15 | Multi-Browser Header Matching | Support `url`/`origin`/`website`, `username`/`login`/`user`/`email`, `password`/`pass`/`pwd` | M2 | R3, Survey 2 |
| 16 | Robust Bounded CSV Streaming | RFC 4180 quotes, multiline fields, BOM stripping, 16MB/10K row/64 col bounds | M2 | R3, Survey 2 |
| 17 | Row-Level Fault Tolerance | Resynchronize on malformed rows without aborting the entire CSV import | M2 | R3, Survey 2 |
| 18 | Import Secret Memory Scrubbing | Pinned/zeroed buffers, disposable packages, zero temp files, no logging | M2 | R3, Survey 2 |
| 19 | Two-Tier Deduplication & Conflict | Intra-file resolution + inter-vault review with opt-in overwrite | M2 | R3, Survey 2 |
| 20 | Strict Origin Normalization | Canonicalize origins; reject `file:`, `ftp:`, `javascript:`, userinfo, dots | M2 | R3, Survey 2 |
| 21 | Typed Import Result Schema | `BrowserDataImportResult` (TotalRows, Imported, Skipped, Duplicates, Conflicts, Invalid, Failed) | M2 | R3, Survey 2 |
| 22 | Import UI & Plaintext Warning | Modal review dialog under Privacy / Data with plaintext source security notice | M2 | R3, Survey 2 |
| 23 | 7-Section Settings IA Layout | Structured modal: Appearance, Browsing, Tabs, Privacy / Data, Downloads, Profile, About | M3 | R4, Survey 3 |
| 24 | Appearance Section Refinement | Theme, accent colors, reduce motion, show bookmarks bar toggle, sidebar density | M3 | R4, Survey 3 |
| 25 | Browsing Section Refinement | Search engine, dev tools, default zoom, default browser action via `ms-settings:defaultapps` | M3 | R4, Survey 3 |
| 26 | Tabs Section Refinement | Restore tabs toggle, human-readable tab sleep ComboBox ("Never", 5m, 15m, 30m, 1h, 2h) | M3 | R4, Survey 3 |
| 27 | Privacy / Data Section Refinement | Autofill toggle, Manage passwords, Import browser data, Clear browsing data, isolation notice | M3 | R4, Survey 3 |
| 28 | Downloads Section Refinement | FolderPicker, safe-directory validation, WebView2 default download path sync, ask-save toggle | M3 | R4, Survey 3 |
| 29 | Profile Section Refinement | Single-profile explanatory text, Open profile folder button | M3 | R4, Survey 3 |
| 30 | About Section Refinement | Slate version, assembly metadata, WebView2 runtime version string, process architecture | M3 | R4, Survey 3 |
| 31 | Settings UI/UX & Accessibility | Slate design tokens, WCAG AA contrast, keyboard navigation, explicit AutomationProperties | M3 | R4, Survey 3 |
| 32 | Fix InPrivate Favicon Disk Leak | Guard `UpdateFaviconAsync` with `!tab.IsTemporary && !tab.IsPrivate` | M4 | R5, Survey 3 |
| 33 | Fix InPrivate Popup Downgrade | Pass `isPrivate: tab.IsPrivate` in `core.NewWindowRequested` | M4 | R5, Survey 3 |
| 34 | Fix Tab Duplicate Privacy Downgrade | Preserve `tab.IsTemporary` and `tab.IsPrivate` on tab duplication | M4 | R5, Survey 3 |
| 35 | Local File Security Boundaries | Enforce web-to-file blocking, iframe file blocking, dangerous extension blocking, UNC rejection | M4 | R5, Survey 3 |
| 36 | Save Page Security Hardening | Validate `DownloadSafety.IsSafeLocalDirectory` and dangerous extensions in `SavePageToFileAsync` | M4 | R5, Survey 3 |
| 37 | View Source Sandboxing Invariants | Script execution disabled, credential origin rejected, HTML escaped | M4 | R5, Survey 3 |
| 38 | Certificate Tooling Invariants | Unconditional error cancellation, non-blocking asynchronous TLS probe with timeout | M4 | R5, Survey 3 |
| 39 | Downloads Security Invariants | Safe directory validation, dangerous extension blocking, atomic file collision reservation | M4 | R5, Survey 3 |
| 40 | Command Router Detachment | Add `DetachWebView2` in `BrowserCommandRouter` and invoke during `DisposeRuntime` | M4 | R6, Survey 3 |
| 41 | Clean Tab Lifecycle Disposal | Deterministic unhooking of CoreWebView2 events and password controllers | M4 | R6, Survey 3 |
| 42 | Tab Sleep/Wake Resource Verification | Verify sleeping tabs suspend properly and resume without memory or controller leaks | M4 | R6, Survey 3 |
| 43 | E2E Test Harness & Suite (Tiers 1-4) | Comprehensive opaque-box test suite published via `TEST_READY.md` | M5 | R1-R6, Test Track |
| 44 | 100% E2E Pass & Tier 5 Adversarial | Implementation passes all Tiers 1-4 tests + Tier 5 adversarial stress testing | M6 | Final Milestone |
| 45 | Native Smoke Tests & Release Build | Verify all 312 baseline GUI smoke tests pass and `build.ps1 -Release` has 0 warnings/errors | M7 | Acceptance Criteria |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| M1 | Credential Vault Security Hardening (Phase 2 / R1) | Vault schema v2, envelope binding, DPAPI entropy, rollback anchor, entries digest, migration, corruption resilience, unit tests | None | DONE |
| M2 | Browser Data Import Subsystem & CSV Importer (Phases 3 & 4 / R2, R3) | `IBrowserDataImporter`, descriptors registry, multi-browser CSV parsing, fault-tolerant row parsing, typed results, privacy placement | M1 | DONE |
| M3 | Settings Information Architecture & UI/UX Polish (Phases 5 & 6 / R4) | 7-section settings modal, bookmarks toggle move, default apps action, sleep ComboBox, folder picker sync, about metadata, accessibility | M2 | DONE |
| M4 | Adversarial Security Audit Fixes & Lifecycle Hardening (Phases 7 & 8 / R5, R6) | InPrivate favicon leak fix, popup downgrade fix, duplicate tab privacy fix, SavePage safety, command router detachment, lifecycle tests | M3 | PLANNED |
| M5 | E2E Test Suite Implementation (Dual-Track Test Track) | Test runner, Tiers 1-4 test suite covering all features, publish `TEST_READY.md` | In Parallel with M1-M4 | PLANNED |
| M6 | Final Milestone: 100% E2E Test Pass & Tier 5 Adversarial Hardening | Verify implementation passes 100% of M5 tests, followed by Tier 5 adversarial coverage hardening | M4, M5 | PLANNED |
| M7 | Release Build Certification & Native GUI Smoke Tests | Full headless unit tests (114 checks), native GUI smoke test suite (312 checks), release build zero warnings/errors | M6 | PLANNED |

## Interface Contracts
### Credential Vault v2 ↔ Application
- `ICredentialProtector`: `byte[] Protect(byte[] plaintext, byte[]? entropy = null)`, `byte[] Unprotect(byte[] ciphertext, byte[]? entropy = null)`
- `CredentialVault.Load(string path, ICredentialProtector protector, string? anchorPath = null)`: Loads schema v2 or seamlessly migrates v1. Sets `RecoveredFromBackup = true` on corruption fallback.
- `CredentialVault.SaveBatchAsync(IReadOnlyList<CredentialSaveRequest> requests)`: Monotonically updates `VaultRevision` and commits atomic write-through.

### Browser Data Import ↔ UI / Settings
- `IBrowserDataImporter`: `BrowserDataImportDescriptor Descriptor { get; }`, `bool CanImport(IReadOnlyList<string> headers)`, `BrowserDataImportPackage Parse(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)`
- `BrowserDataImportService.GetRegisteredDescriptors()`: Returns descriptors for Passwords (available), Bookmarks (planned), History (planned), Settings (planned).
- `BrowserDataImportResult`: `(int TotalRows, int Imported, int Skipped, int Duplicates, int Conflicts, int Invalid, int Failed, BrowserDataKinds Kinds, string? SourceName)`

### Settings Dialog ↔ WebView2 Runtimes
- `ShowSettingsAsync`: Refined 7-section layout adhering to Slate design tokens.
- Downloads change: Updates `settings.DownloadPath` and synchronizes `core.Profile.DefaultDownloadFolderPath` across all active runtimes in `_runtimes.Values`.
- Tab sleep: Two-way binding between ComboBox string ("Never", "5 minutes", ...) and integer minutes in `settings.SleepAfterMinutes`.

### Tab Lifecycle ↔ Command Router
- `BrowserCommandRouter.DetachWebView2(WebView2 view)`: Unsubscribes `PreviewKeyDown -= OnWebViewPreviewKeyDown` when tab runtime is disposed in `DisposeRuntime`.

## Code Layout
- `Slate.Core/CredentialVault.cs`: Vault models, persistence, validation, rollback anchor, digests, migration.
- `Slate.Core/ICredentialProtector.cs` & `Slate/WindowsCredentialProtector.cs`: DPAPI protector with contextual entropy support.
- `Slate.Core/BrowserDataImport.cs`: Importer interfaces, descriptors registry, CSV parser, deduplication, typed results.
- `Slate/MainWindow.Dialogs.cs`: Settings modal (7 sections), Import browser data review dialog, TLS probing dialog.
- `Slate/MainWindow.Browser.cs`: InPrivate favicon fix, popup downgrade fix, duplicate tab fix, SavePage safety, tab lifecycle & download safety.
- `Slate/BrowserCommandRouter.cs`: Keyboard routing with attach & detach methods.
- `Slate.Tests/Program.cs`: Core checks covering session lifecycle, navigation security, command registry, download safety, omnibox, and favicon store.
- `Slate.Tests/PasswordTests.cs`: Cryptographic and vault unit tests, CSV import tests.
- `Slate.Tests/SecurityAuditTests.cs`: Security audit checks covering command-line validation, UNC rejection, frame URL filtering, and environment variable cataloging.
- `scripts/smoke-test.ps1`: Native GUI automation smoke tests (312 checks).
- `scripts/build.ps1`: Release build script.
