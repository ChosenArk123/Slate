# Browser data import

Slate imports browser data through a format-adapter boundary in `Slate.Core`. The
first adapter supports password CSV files exported by Chrome, Edge, and Firefox.
Bookmarks, history, and settings are represented as future data kinds, but Slate
does not claim to import them yet.

## Flow

1. Settings → Profile → Import browser data opens a local `.csv` picker.
2. Slate accepts only a rooted local path and rejects UNC directories, non-CSV
   files, missing files, and files larger than 16 MiB.
3. `BrowserDataImportService` parses the file without networking or copying it
   into the Slate profile. The parser registry selects an adapter by its header.
4. The password adapter requires one `url`, `username`, and `password` column.
   Additional browser columns are ignored rather than trusted.
5. Every URL is reduced by the existing `CredentialOrigin` implementation to an
   exact scheme, canonical host, and effective port. Invalid schemes, malformed
   origins, control characters, empty passwords, and oversized values are skipped.
6. Slate previews new, changed, unchanged, duplicate, rejected, and conflicting
   rows without displaying a password.
7. Changed saved passwords are preserved by default. Replacing them requires an
   explicit checkbox and confirmation.
8. Approved entries use `CredentialVault.SaveBatchAsync`, the same validation,
   DPAPI envelope, revision checks, durable temporary file, and atomic replacement
   path as ordinary saves. A stale item rejects the entire reviewed batch.

Identical duplicate rows collapse to one candidate. If two rows for the same
exact origin and username contain different passwords, Slate skips all rows for
that identity instead of guessing which one is current. Vault capacity is checked
before confirmation.

## Parser boundary

`IBrowserDataImportParser` exposes a format name, supported `BrowserDataKinds`,
header matching, and package creation. `BrowserDataImportPackage` currently carries
passwords and summary counts. A future bookmark, history, or settings adapter can
join the same registry without adding another settings store, URL normalizer, or
credential write path.

The CSV reader supports escaped quotes, commas, CRLF/LF records, quoted newlines,
and a UTF byte-order mark. It caps total input, rows, columns, field length, and
record length before constructing a package. Malformed quoting fails the import;
individual invalid credential rows are counted and skipped.

## Security and operational limits

- Browser password exports are plaintext. Slate warns before confirmation and
  never modifies or deletes the source file; the user must protect or remove it.
- Imported plaintext exists briefly in managed strings during review. .NET cannot
  guarantee erasure of those immutable strings. Package disposal drops retained
  secret references as soon as the flow finishes.
- No password, CSV row, parser payload, or inner exception is logged or copied to
  `session.json`.
- DPAPI protects the destination vault as the current Windows user. Same-user
  malware, memory inspection, and replacement by an older complete valid vault
  remain outside the stated boundary.
- CSV import does not read a live browser profile, decrypt another browser's
  database, import passkeys, or promise compatibility with vendor formats other
  than the documented exported columns.

## Validation

Headless tests cover Chrome/Edge and Firefox headers, exact-origin normalization,
quoted fields, duplicate collapse, conflicting-row rejection, malformed schemas,
default-preserve and explicit-update policy, revision-bound writes, and atomic
conflict behavior. The native suite imports a synthetic CSV through the same
coordinator into the real Windows DPAPI vault before exercising autofill.
