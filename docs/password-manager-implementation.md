# Slate password manager implementation and audit report

The implementation adds a local, first-party password manager without changing the
Slate.Core/Slate dependency boundary or using Chromium's password manager. No new
NuGet dependency was introduced. This report describes the implemented boundaries,
not a claim of independent security certification.

## 1. Architecture

`CredentialOrigin`, `PasswordGenerator`, and `CredentialVault` live in Slate.Core.
The vault accepts `ICredentialProtector`; Windows P/Invoke lives only in Slate.
The single application vault serializes operations with a semaphore and runs disk
and protection work on a worker thread. The application's existing per-user mutex
continues to prevent two normal Slate processes writing the profile.

`PasswordController` owns one WebView lifetime, its isolated document state,
navigation tickets, transient candidate and save decision. `PasswordManagerUi`
owns list/reveal/copy/edit/delete presentation. `BrowserDataImportService` and
`CredentialImportCoordinator` parse and review browser exports in Slate.Core.
MainWindow only connects lifecycle and
chrome actions; domain security and protocol handling are in dedicated classes.

## 2. Files

New production files:

- `Slate.Core/CredentialOrigin.cs`
- `Slate.Core/PasswordGenerator.cs`
- `Slate.Core/CredentialVault.cs`
- `Slate.Core/BrowserDataImport.cs`
- `Slate/WindowsCredentialProtector.cs`
- `Slate/PasswordController.cs`
- `Slate/PasswordDocument.js` (embedded resource)
- `Slate/PasswordManagerUi.cs`
- `Slate/MainWindow.Passwords.cs` (small chrome adapter)

New test files: `Slate.Tests/PasswordTests.cs` and
`Slate/MainWindow.PasswordSmokeTests.cs`.

Modified integration files: `Slate/MainWindow.cs`, `Slate/MainWindow.Browser.cs`,
`Slate/MainWindow.Dialogs.cs`, `Slate/MainWindow.SmokeTests.cs`,
`Slate/Slate.csproj`, and `Slate.Tests/Program.cs`.

Documentation: `README.md`, this report, and `password-manager-security.md`.
The repository started with its source tree untracked; no existing work was reset
and no commit or staging operation was performed.

## 3. Storage format

`<ProfileDirectory>/credentials.v1.json` contains `Version: 1` and `Entries`.
Each entry contains metadata (stable GUID, canonical origin, username, created,
updated, nullable last-used timestamp and monotonically increasing revision) and
`ProtectedSecret`, serialized as base64 **ciphertext**. Base64 is only the JSON
encoding of DPAPI output, not the protection mechanism.

The encrypted envelope contains ID, origin, username, revision and password. Decryption
verifies those four identity fields against the metadata before returning the
secret. Altering metadata to move a ciphertext to another origin/account fails.
Metadata itself is readable on disk; it is not presented as confidential.

The file is limited to 16 MiB and 10,000 entries. Validation rejects duplicate IDs,
duplicate origin/username pairs, invalid origins, malformed metadata, oversized
fields and unknown versions. Version inspection precedes strict deserialization
so a future schema cannot accidentally fall back to an older backup.

Writes use an encrypted `.tmp`, `WriteThrough`, `Flush(true)`, and atomic
replace/move. A `.bak` retains the previous valid database. In-memory state changes
only after the write succeeds. Corrupt primary recovery is read-only and visibly
reported in management; the damaged primary is preserved. An unreadable secret
fails closed when individually decrypted. There is no automatic destructive repair.

## 4. Windows protection

Current-user `CryptProtectData` / `CryptUnprotectData`, with
`CRYPTPROTECT_UI_FORBIDDEN`, no machine-wide flag, no application key, no custom
cryptography and no third-party crypto dependency. Each secret is protected
independently, avoiding whole-vault password decryption for listing or updates.
Native output buffers and serialized plaintext byte buffers are cleared. Managed
strings and WebView protocol string copies cannot be reliably erased.

DPAPI protects against a file reader without the Windows user's decryption context.
It does **not** protect against malware running as that user, memory inspection,
an already compromised browser process, or local database rollback/deletion.

## 5. Origin matching

Exact normalized origin: scheme + canonical ASCII/IDN host + effective port.
Default ports disappear; nondefault ports remain. Host case, IPv4, IPv6, localhost
and Unicode/punycode representations are tested. Sibling subdomains and HTTP/HTTPS
remain distinct. Userinfo, malformed/non-web URLs, whitespace, backslashes and
trailing-dot hostnames are rejected. No eTLD or registrable-domain sharing.

## 6. WebView bridge

`AreHostObjectsAllowed`, `IsWebMessageEnabled` and `IsPasswordAutosaveEnabled`
remain false. There is no WebMessageReceived handler or host object.

Native code creates a randomly named CDP isolated world in the frame independently
obtained from `Page.getFrameTree`. It validates that frame against native
`CoreWebView2.Source`. Runtime context creation supplies a **system-unique** context
ID; all script calls target `Runtime.callFunctionOn.uniqueContextId`, never a
default world or reusable integer context for secret delivery.

The one submit binding is installed only in that named world via
`Runtime.addBinding.executionContextName`. Native handling validates binding name,
context ID, bounded payload size, exactly four versioned fields and a per-document
random nonce. Messages contain no authoritative origin. No binding is installed
in page-script worlds. Calls use structured CDP arguments; passwords are never
composed into JavaScript source. The small embedded script has no eval or library.

Tickets carry runtime/controller identity implicitly, navigation generation,
unique context, normalized origin and exact native URL. Checks run before and
after asynchronous work. A second URL check runs inside the original isolated
context to catch SPA routing before native event delivery. Unsupported CDP
capabilities fail closed without disabling ordinary browsing.

## 7. Form detection

Only visible, enabled, writable top-level input controls in an unambiguous POST
form with an exact same-origin action qualify. Username/email text fields and
autocomplete hints guide discovery. One current-password field can be filled;
new-password/confirmation and one-time-code fields are excluded from login filling. Generation uses
one or two explicit `autocomplete=new-password` fields and a username field,
and fills both password fields together. Unidentified change forms are declined
rather than generating a password that cannot enter the save pipeline.
Capture checks matching new-password values and never selects a confirmation
field independently. Dynamic forms are discovered at operation time.

Password-only flows support explicit saved-account selection. Unidentified manual
password-only submissions are not saved; Slate does not infer an account from a
previous page or silently pick a username. All iframe and shadow-root forms,
formless controls, ambiguous multiple forms, GET forms, and cross-origin form
actions are deliberately unsupported in this version.

## 8. Save/update

A trusted submit event captures a bounded candidate; the event alone does not
offer or persist it. A subsequent successful same-origin navigation to a different
URL with no visible password field, or disappearance of the submitted SPA form,
provides evidence for a compact native offer. A slow in-progress navigation can
retain its candidate until completion. Cross-origin/extra redirect hops and
failed navigation discard it. The candidate expires after 60 seconds.

The offer explicitly asks the user to save only if sign-in succeeded. Exact origin
plus ordinal username selects the existing account for comparison. Identical
passwords produce no offer; changed passwords offer update. A revision check at
commit prevents stale prompts overwriting concurrent updates or reviving deletions.
The approved write can complete atomically after the source tab closes; unapproved
candidates are discarded on closure. Separate tabs have separate candidates and
share the serialized vault. Only the focused tab's pending offer is displayed.

Login success is heuristic, not an authentication assertion. A malicious page can
simulate success on its own origin; it still cannot persist without the user's
native confirmation. Script-driven `requestSubmit()` may produce a trusted submit
event: trust here filters fabricated events, not proof of a human login.

## 9. Fill

When exactly one account matches an eligible untouched login form on HTTPS or a
secure loopback development origin, Slate can fill it automatically only when the
form explicitly annotates `username` and
`current-password`; this behavior has a Settings toggle. Multiple accounts always
require explicit selection from the native toolbar surface. Autofill never runs in
InPrivate or temporary tabs, never overwrites a nonempty username or password, and
never submits the form. Last-used recording is best-effort;
a metadata write failure does not misreport an already completed fill. All account choices are exact
origin matches. HTTP displays a clear unencrypted-connection notice before use.
A successful fill is performed once per document and does not overwrite a
nonempty password. Failed detection can be retried after the form changes.
Navigation, SPA routes, sleep, destruction and recreation invalidate old tickets.
The legitimate receiving page can read the resulting field; isolated worlds do
not conceal shared DOM values from that page or its XSS.

## 10. Temporary tabs

Explicit fill/generation only. Their document script disables candidate capture,
native code independently rejects it, and successful fill never writes last-used
metadata. No automatic save offer. Keeping/pinning a temporary tab requires a
reload before capture becomes enabled for that document.

## 11. Generation

Default 24 characters; supported lengths 16–128. Uses
`RandomNumberGenerator.GetInt32` (unbiased bounded sampling), lower/uppercase,
digits and compatible ASCII symbols, with all four classes guaranteed and a
secure Fisher–Yates shuffle. No deterministic seed, network or clipboard use.
Creation/change form submissions enter the same candidate/confirmation pipeline.

## 12. Management, reveal and clipboard

Settings → Privacy / Data → Manage passwords, also available from the toolbar
password menu. Search covers origins and usernames. List rows contain metadata
only. Explicit reveal decrypts one selected secret; selection changes, closing
the dialog, Hide, or 30 seconds remask it. Async selection tickets prevent a late
reveal from populating the wrong selection. Delete requires a second click and
checks the stored revision. Edit is deliberate, keeps the exact origin immutable,
and updates username/password through the revision-bound vault. A username collision
or stale edit fails without overwriting either account.

Copy username/password requires a click, uses the Windows clipboard API with
history and roaming disabled, and does not automatically clear unrelated contents.
Windows Hello's supported HWND interop API was evaluated. It can request user
verification for a desktop window, but it does not cryptographically change the
current-user DPAPI key boundary. Enrollment, fallback, lockout, and recovery policy
need dedicated device validation before Slate can require it without locking users
out of their own vault. Reveal/copy/edit currently require deliberate interaction
but **not** Windows reauthentication.

## 13. Preserved invariants

No secrets added to BrowserState/session models; no credential diagnostics contain
passwords, DOM or protocol payloads. No new browser-native capability enabled for
page worlds. Existing URL/frame, TLS, popup, permissions, external-URI and keyboard
controls remain in place. Existing session, downloads and suspension tests pass.
No passkeys, sync, password export, breach services or extension integration.
Password CSV import uses the existing vault; bookmark/history/settings adapters are
not yet implemented. See [browser-data-import.md](browser-data-import.md).

## 14–15. Validation

Current verification on 2026-09-09:

| Command / verification | Result |
| --- | --- |
| `scripts\build.ps1` | Passed; 0 warnings, 0 errors |
| `scripts\build.ps1` | Passed; 0 warnings, 0 errors; 104 headless checks passed |
| `scripts\smoke-test.ps1` | Release build passed; 319 native checks passed in 29 seconds |
| `scripts\build.ps1 -Publish` | Passed; self-contained output updated in `artifacts/Slate` |

Native result: [smoke-20260909-233704.json](../artifacts/smoke-20260909-233704.json).
Rendered QA: [masked manager](../artifacts/password-manager-masked.png) and
[save offer](../artifacts/password-save-offer.png).
Source/output fingerprint manifest: [password-manager-validation.json](../artifacts/password-manager-validation.json).

DPAPI initially failed in the restricted tool sandbox. An independent Windows
ProtectedData probe reproduced the missing-user-context failure there. The native
suite was then run with authorized normal Windows user context, where actual DPAPI
round-trip and tamper-rejection tests passed. No protection fallback was added.
Compile/test failures encountered while developing this feature were fixed; none
were dismissed as pre-existing failures.

Core tests exercise normalization, account policy, generator invariants, version
and record rejection, serialized writes, stale revisions, disk write failure,
interrupted temporary files, backup recovery and ciphertext identity binding.

Native tests use synthetic credentials and the existing local HTTP server. They
exercise real DPAPI round-trip/tamper failure, isolated-world and page-global
forgery boundaries, account selection, a deterministic blocked-decryption race,
same-origin replacement, wrong origin, SPA navigation, iframe/action denial,
dynamic/password-only forms, success-gated save/update/unchanged decisions,
generation, single-account autofill and opt-out, OTP/prefilled-field refusal,
atomic CSV import, slow POST completion, temporary-tab privacy, sleeping/waking,
runtime destruction/recreation, and deliberate reveal/edit/remasking. Screenshots
contain masked passwords only. The clipboard is not mutated by smoke tests.

## 16. Compatibility and operational limits

- The documented conservative form/frame rules exclude some real sites.
- The bridge is installed after navigation completes; early submissions can be
  missed. A document captures one candidate and accepts one successful fill;
  reload after a failed login/retry or a dismissed/expired capture.
- Redirect chains, cross-origin SSO and same-URL POST reloads may not offer save.
- Candidate timeout and a busy/closing runtime may suppress an offer.
- No managed-string erasure guarantee; page DOM and CDP necessarily receive
  plaintext during the requested operation. Same-user malware remains a threat.
- Backups may retain deleted/previous credentials. SSD secure erasure and rollback
  resistance are not claimed. Read-only recovery needs manual file review.
- Real-site compatibility, adversarial OS/process scenarios and clipboard/Hello
  device behavior are not established by local fixture tests.

## 17. Deferred hardening

Independently audited threat-model and protocol review; Windows Hello verification
with reliable recovery; guided vault recovery; encrypted metadata if site/username
confidentiality becomes a requirement; tested secure retry rearming; broader form
compatibility only with equivalent document/origin guarantees; fuzz testing of
vault parsing and browser navigation event ordering. No weaker fallback is used
when DPAPI or the isolated context mechanism is unavailable.

## 18. Hostile self-review

Review assumed attacker-controlled page JavaScript, forms and frames. Findings
fixed during implementation:

1. Reusable integer execution-context IDs were excluded from secret delivery;
   unique context plus native ticket and in-context URL checks cover replacement
   and SPA races. Tests include a deliberately paused decryption across navigation.
2. Ciphertexts bind ID/origin/username/revision internally to prevent metadata reassignment
   and transplantation from an older revision of the same credential.
3. Unknown-version handling moved before schema deserialization, avoiding future
   fields triggering rollback to a backup.
4. Sleep invalidates tickets before asynchronous suspension, even if resumed.
5. Temporary document scripts do not capture credentials; native checks and no
   last-used writes provide independent privacy guards.
6. Candidate lifetime distinguishes a live runtime from a currently loading page,
   allowing slow success without enabling filling during navigation.
7. Reveal rechecks selection after decryption, clears local references on all exits,
   and restores action-button availability after selection races.
8. Save prompt theme resources were corrected after rendered visual inspection.
9. Test assertions now wait for actual dialog loading, avoiding vacuous list checks.
10. Last-used bookkeeping failure no longer reports that an already delivered
    password failed to fill; read-only backup use skips that metadata write.
11. Generation refuses an unidentified password-creation/change form, avoiding
    an unsavable generated password in the conservative account-matching policy.
12. Password CSV parsing is bounded by total size, row, column, field, and record
    limits; duplicate identities with conflicting secrets are all skipped.
13. Import review preserves changed passwords by default and commits selected
    entries through one general revision-checked vault batch, preventing partial
    import after a stale decision.
14. Automatic fill requires exactly one account, a secure HTTPS/loopback origin,
    an exact live document ticket, the enabled setting, a normal non-temporary
    tab, and untouched eligible fields.
15. Clearing no selected category no longer falls through to clearing the entire
    WebView2 profile; time-bounded password deletion is rejected.

No unresolved wrong-origin delivery path was found in this review. This statement
is limited to the inspected implementation and recorded tests, not a proof that
no vulnerabilities remain. Existing broad exception logging was not expanded;
all new secret-bearing service/protocol paths sanitize or consume exceptions.

API references and the pre-implementation threat table are in
[password-manager-security.md](password-manager-security.md).
