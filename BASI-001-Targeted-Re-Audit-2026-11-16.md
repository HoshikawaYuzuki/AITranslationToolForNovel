# BASI-001 — Targeted Re-Audit

## Scope

This re-audit verifies only the issues identified in the previous BASI-001 audit. Node.js, pnpm, and Docker version differences between the Codex sandbox and Windows were explicitly excluded.

## Repository status

- Branch: `basi-001-foundation`
- HEAD: `fa70aea`
- `git status`, working-tree diff, staged diff, and untracked-file check: clean at audit time.
- No source code, database, migration, or dependency changes were made during the audit.

## Issue re-check

| Previous issue | Current status | Evidence | Further action |
|---|---|---|---|
| `CONTRACTS.md` did not match `AiRequest.cs` | Fixed | Both now use `int MaxTokens = 2048` and `double Temperature = 0.7` | None |
| `MASTER_PLAN.md` marked Task 1 as in progress | Fixed | Task 1 is now marked completed | None |
| `CURRENT_TASK.md` reported 8 tests while 12 test cases exist | Fixed | Current task reports 12 tests | None |
| README instructed an unavailable migration step | Partially fixed | Migration is now documented as a Task 2 concern | No migration is required for BASI-001 |
| Handoff claimed 12/12 tests passed | Not verified | `dotnet test` still stops after test discovery without a summary | Resolve the test runner or run with a compatible .NET environment |
| Health endpoint was not runtime-verified | Not verified | Code has a two-second DB timeout, but the test host does not complete | Execute a successful runtime request/test |
| Development credential is hardcoded | Not fixed | `appsettings.Development.json` still contains `basi_dev_password` | Move to environment configuration or document the development-only decision |
| Integration test was only a placeholder | Not fixed | `PlaceholderTests.cs` still contains `Assert.True(true)` | Add a real integration test or explicitly defer it |
| Node/pnpm/Docker version mismatch | Excluded | Rechecked using the Windows fnm runtime as requested | Not counted as an issue |

## Build and test results

### Backend

Command:

```text
dotnet build Basi.sln --no-restore -m:1
```

Result: **PASS** — all 7 projects built with 0 warnings and 0 errors.

### Backend tests

Command:

```text
dotnet test tests\\Basi.Tests.Unit\\Basi.Tests.Unit.csproj --no-build -m:1
```

Result: **NOT VERIFIED**. The test runner stopped after:

```text
A total of 1 test files matched the specified pattern.
```

No final test summary was produced; the process was terminated manually.

### Frontend using Windows fnm runtime

Runtime path:

```text
C:\Users\pc\AppData\Roaming\fnm\node-versions\v26.13.1\installation
```

Versions:

```text
Node.js  v26.13.1
npm      12.2.2
pnpm     12.21.0
```

- `pnpm check`: **PASS**, 0 errors and 0 warnings.
- `pnpm build`: **PASS**.

## Architecture and regression review

No regression was found in the BASI-001 architecture. The Modular Monolith structure, provider abstraction, backend build, frontend checks, and frontend build remain intact. No Task 2–6 requirements were evaluated.

## Final verdict

- Fully fixed: 3 documentation/contract issues.
- Partially fixed: migration documentation.
- Remaining: test runtime verification, development credential handling, and placeholder integration test.
- Critical acceptance gap: the test suite and health endpoint have not been independently verified at runtime.

**BASI-001 is not fully accepted yet.** The project should not be formally advanced to BASI-002 until the test runner produces a real result and the health endpoint is verified successfully.
