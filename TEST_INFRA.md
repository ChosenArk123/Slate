# E2E Test Infra: Slate Browser Roadmap Hardening (Phases 2–10)

## Test Philosophy
- Opaque-box, requirement-driven testing based on `ORIGINAL_REQUEST.md`.
- Systematic 4-tier methodology:
  - **Tier 1**: Feature Coverage (>=5 test cases per feature covering representative happy-path inputs).
  - **Tier 2**: Boundary Value Analysis & Error Invariants (>=5 test cases per feature covering empty, max bounds, malformed inputs, edge cases).
  - **Tier 3**: Cross-Feature Combinations (pairwise combinatorial testing across vault, import, settings, lifecycle, security).
  - **Tier 4**: Real-World Application Scenarios (end-to-end user workflows).
  - **Tier 5**: Adversarial Coverage Hardening (white-box gap analysis, edge case probes, stress tests).

## Feature Inventory & Test Matrix

| # | Feature | Source | Tier 1 | Tier 2 | Tier 3 |
|---|---------|--------|:------:|:------:|:------:|
| 1 | Vault Schema v2 & Envelope Binding | R1 | 5 | 5 | ✓ |
| 2 | Vault DPAPI Contextual Entropy | R1 | 5 | 5 | ✓ |
| 3 | Vault Monotonic Rollback Anchor | R1 | 5 | 5 | ✓ |
| 4 | Vault Full-State EntriesDigest | R1 | 5 | 5 | ✓ |
| 5 | Vault v1-to-v2 Schema Migration | R1 | 5 | 5 | ✓ |
| 6 | Vault Corruption & Backup Fallback | R1 | 5 | 5 | ✓ |
| 7 | Import Subsystem & Descriptors | R2 | 5 | 5 | ✓ |
| 8 | Multi-Browser Header Matching | R3 | 5 | 5 | ✓ |
| 9 | Bounded CSV Streaming Parser | R3 | 5 | 5 | ✓ |
| 10 | Independent Malformed Row Handling | R3 | 5 | 5 | ✓ |
| 11 | Two-Tier Deduplication & Conflicts | R3 | 5 | 5 | ✓ |
| 12 | Typed Import Result Schema | R3 | 5 | 5 | ✓ |
| 13 | 7-Section Settings IA Layout | R4 | 5 | 5 | ✓ |
| 14 | Tab Sleep Human-Readable ComboBox | R4 | 5 | 5 | ✓ |
| 15 | Downloads Path Sync & FolderPicker | R4 | 5 | 5 | ✓ |
| 16 | InPrivate Zero Persistence & Favicons | R5 | 5 | 5 | ✓ |
| 17 | Local File & Save Page Security | R5 | 5 | 5 | ✓ |
| 18 | View Source & Cert Invariants | R5 | 5 | 5 | ✓ |
| 19 | Tab Lifecycle & Event Detachment | R6 | 5 | 5 | ✓ |
| 20 | Tab Sleep/Wake Memory Invariants | R6 | 5 | 5 | ✓ |

## Test Architecture
- **Test Runner**:
  - Headless xUnit/NUnit test suite in `Slate.Tests/Slate.Tests.csproj`.
  - Invocation: `.tools\dotnet\dotnet.exe run --project Slate.Tests/Slate.Tests.csproj`
  - Pass/Fail: Exit code 0, all tests pass, zero unhandled exceptions.
  - Native GUI smoke test: `powershell -File scripts\smoke-test.ps1`
  - Release compilation: `powershell -File scripts\build.ps1 -Release`
- **Directory Layout**:
  - `Slate.Tests/`: Unit and integration test suites.
  - `Slate.Tests/PasswordTests.cs`: Cryptographic vault, DPAPI entropy, rollback resistance, CSV parser matrix.
  - `Slate.Tests/SecurityAuditTests.cs`: Private browsing zero-persistence, local file sandboxing, save page safety, view source invariants.
  - `Slate.Tests/SettingsTests.cs`: Settings persistence, 7-section IA validation, tab sleep mapping, download path synchronization.
  - `Slate.Tests/LifecycleTests.cs`: Tab lifecycle open/sleep/wake/close transitions, memory and handler detachment verification.

## Real-World Application Scenarios (Tier 4)
| # | Scenario | Features Exercised | Complexity |
|---|----------|--------------------|------------|
| 1 | Full Migration & Batch Credential Lifecycle | F1, F3, F4, F5, F6 | High |
| 2 | Multi-Browser Password CSV Import & Conflict Resolution | F8, F9, F10, F11, F12 | High |
| 3 | Settings Customization, Restart Persistence & Download Sync | F13, F14, F15 | Medium |
| 4 | InPrivate Browsing Session with Popups, Downloads & Navigation | F16, F17, F18 | High |
| 5 | Multi-Tab Intensive Browsing, Tab Suspension, Resumption & Closure | F19, F20 | High |
| 6 | Adversarial Tampering: Corrupted Primary, Forged Entries & Rollback Attack | F1, F2, F3, F4, F6 | High |

## Coverage Thresholds
- **Tier 1 (Feature Coverage)**: >= 5 test cases per feature (20 features × 5 = 100 tests).
- **Tier 2 (Boundary & Corner Cases)**: >= 5 test cases per feature (20 features × 5 = 100 tests).
- **Tier 3 (Cross-Feature Combinations)**: >= 20 combinatorial interaction tests.
- **Tier 4 (Real-World Scenarios)**: >= 6 comprehensive end-to-end scenarios.
- **Minimum Target**: >= 226 verified automated test cases in the unified test suite.
