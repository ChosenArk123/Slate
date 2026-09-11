# Password manager threat model and design

Written before implementation. Page content is hostile; native browser chrome and the
Windows user account are trusted. Real credentials must never be used in tests.

| Threat | Boundary / response |
| --- | --- |
| Wrong origin, sibling sites, HTTP/HTTPS, ports | Exact canonical scheme, ASCII host and effective port. No domain sharing. Automatic fill requires HTTPS (or secure loopback), one unambiguous exact-origin account, and an untouched eligible form. Other HTTP fills stay explicit and show a warning. |
| Malicious JavaScript, XSS, deceptive forms | Multiple accounts require explicit native selection. Isolated JavaScript world protects controller state, not the shared DOM. The receiving page can read filled values. Same-origin XSS cannot be solved by a password manager. |
| Compromised iframe | Top-level frame only; same-origin frames also deferred. Never traverse frame documents. |
| Navigation races, redirects, tab reuse | Per-controller lifetime, monotonic navigation generation, and CDP system-unique execution context ID. Never send a secret using a reusable integer context ID or default-context evaluation. |
| SPA navigation, stale async work | Source changes invalidate operation tickets. Revalidate after every await and target the original unique context. No mutation-driven refill. |
| Sleeping, crash, recreation, closure | Liveness callback includes runtime identity, suspension and tab membership. Dispose detaches handlers and drops candidates. New runtime has new controller. |
| Forged page messages | Web messaging and host objects stay disabled. CDP binding restricted to randomly named isolated world; strict bounded payload with per-document nonce and context ID validation. Page origins are never authoritative. |
| Local file reads | Current-user DPAPI for each secret with ID/origin/username bound inside the protected envelope. Metadata is not secret. Same-user malware can call DPAPI and is outside this boundary. |
| Logs / crash output | Generic credential errors only, no inner exceptions, DOM, script results or protocol payloads in diagnostics. Managed strings cannot be reliably erased. Clear byte buffers where possible. |
| Clipboard | Explicit copy only. Disable clipboard history/cloud roaming where supported. No automatic clearing that could erase unrelated content. |
| Corrupt / interrupted writes | Versioned separate vault, bounded strict validation, serialized operations, durable encrypted temp, atomic replacement and backup. Backup recovery is read-only; preserve damaged primary and report it. Unknown versions fail closed. Replacement with an older complete, cryptographically valid vault is not currently detected. |
| Duplicate or stale updates | Exact origin plus ordinal username; compare one decrypted secret, avoid unchanged prompts, optimistic revision checked on commit. No silent overwrite after concurrent update/deletion. |
| Malicious or ambiguous import file | Local 16 MiB CSV only; bounded strict parser; existing origin/input validation; conflicting duplicate identities skipped; changed saved passwords preserved by default; one atomic revision-checked vault batch. |

## Invariants

1. No passwords in BrowserState, history, ordinary session JSON or diagnostics.
2. Native CoreWebView2.Source is independently normalized for every page operation.
3. A fill never submits a form. Automatic fill requires HTTPS or secure loopback,
   explicitly annotated login fields, and one exact-origin account; unhinted forms
   and multiple accounts require an explicit native choice. Neither path overwrites
   a nonempty username/password or treats a one-time-code field as a password.
4. A fill targets a system-unique top-level document context and matching generation.
5. A submitted candidate is not proof of authentication. Offer only after a same-origin
   successful navigation with no password form, or after its SPA form disappears.
   The prompt asks the user to confirm success; persistence always requires a click.
6. Corrupt, structurally invalid, or internally inconsistent vault state fails closed.
   Replacement with an older complete, cryptographically valid vault is not currently detected.
7. Temporary tabs never capture candidates, save credentials or update last-used data.
8. No global page-native bridge, and no passwords interpolated into JavaScript source.
9. The vault retains encrypted records, decrypting one only for compare, fill or reveal.

## Storage choice

Per-secret DPAPI limits decryption and corruption blast radius and avoids holding the
entire vault in plaintext for list rendering or a single update. A versioned JSON file
contains metadata and DPAPI ciphertext; encrypted envelopes bind stable ID, canonical
origin, username and revision to the secret. DPAPI provides integrity for those protected fields.
Metadata timestamps and deletion/rollback are not authenticated against local tampering.
Backups may retain a deleted/previous credential; secure erasure on SSDs is not claimed.

## API basis

- [CDP Runtime](https://chromedevtools.github.io/devtools-protocol/tot/Runtime/):
  `uniqueContextId` avoids integer execution-context reuse across processes;
  `addBinding.executionContextName` restricts binding installation to the isolated world.
- [DPAPI](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata):
  current-user protection, UI forbidden, no machine-wide flag or application key.
- [Windows verification interop](https://learn.microsoft.com/en-us/windows/win32/api/userconsentverifierinterop/nf-userconsentverifierinterop-iuserconsentverifierinterop-requestverificationforwindowasync):
  an HWND-aware desktop API exists. Integration and recovery behavior need dedicated
  device testing; this iteration uses deliberate reveal/copy, not pretend authentication.
