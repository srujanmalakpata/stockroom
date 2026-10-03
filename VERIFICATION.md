# stockroom test record

- **Date:** 2026-10-03
- **Machine:** Shared 4-vCPU Linux container (Ubuntu 24.04, x86_64), with other concurrent builds
- **Toolchain:** .NET SDK 8.0.425 (runtime 8.0.31), dotnet-ef 8.0.31 (local tool),
  ReportGenerator 5.4.4, Bicep CLI 0.37.4 (`Azure.Bicep.CommandLine.linux-x64` NuGet package),
  PostgreSQL 16.15 (`postgres:16-alpine` container)
- **Test setup:** Clean `bin/`, `obj/` and coverage output for the format, build and test checks.

This record reports results measured in the environment above. NOT_RUN rows retain their reasons;
separate container results are identified explicitly. Key output is quoted below.

| # | Command | Result | Key output |
| --- | --- | --- | --- |
| 1 | `dotnet format --verify-no-changes` | PASS | exit 0 |
| 2 | `dotnet build -warnaserror` (Debug) | PASS | `0 Warning(s) 0 Error(s)`. Analyzers at `AnalysisLevel=latest-recommended`, `TreatWarningsAsErrors=true` |
| 3 | `dotnet build -c Release -warnaserror` | PASS | `0 Warning(s) 0 Error(s)` |
| 4 | `dotnet test` (Debug) and `dotnet test -c Release` | PASS | Domain `Passed: 48`, Application `Passed: 7`, Integration `Passed: 44`, **99 total, 0 failed**, in both configurations |
| 5 | `dotnet test -c Release --settings coverlet.runsettings --collect:"XPlat Code Coverage"` + ReportGenerator 5.4.4 TextSummary (the CI configuration) | PASS | Line **94.7 %** (725 of 765), branch **74.7 %** (92 of 123), method 93.8 % (122 of 130). Per assembly: Api 93.4 %, Application 93.4 %, Domain 99.5 %, Infrastructure 90.7 %. EF migrations are excluded. Source-generated `LoggerMessage` bodies still count as coverable lines, and most are uncovered because the tests switch logging off. A separate recorded coverage sample is 96.0 %; it is not the Release/CI result reported here |
| 6 | Same as row 5 in Debug (`dotnet test` default) | PASS | Line 95.1 % (822 of 864), branch 75.6 % (93 of 123). Debug has more coverable lines, so its figures differ from Release/CI. Release is the CI configuration |
| 7 | `dotnet restore --locked-mode` | PASS | All 7 projects restore against the committed `packages.lock.json` files |
| 8 | `dotnet ef migrations has-pending-model-changes --context SqliteInventoryDbContext` / `PostgresInventoryDbContext` (Release, `--no-build`, as in CI) | PASS | `No changes have been made to the model since the last migration.` (both). Two migrations per provider: `InitialCreate`, `AddProductVersion` |
| 9 | `INVENTORY_TEST_POSTGRES=... dotnet test tests/Inventory.Api.IntegrationTests -c Release` against `postgres:16-alpine` (16.15) | PASS | `Passed: 44, Failed: 0` in **5 of 5** repeated runs, with Npgsql `EnableRetryOnFailure` on. No per-class test databases remain (`count(*) = 0`) |
| 10 | Concurrency tests repeated: `dotnet test --filter ConcurrencyTests` ×10 (SQLite, retries on, `MaxAttempts=10`) | PASS | **10 of 10** runs pass (9 tests per run). The oversell tests assert `Reserved <= OnHand` after every run, so 0 units are oversold in every run, here and on PostgreSQL (row 9) |
| 11 | Negative control: the same ×10 with retries **disabled** (`Concurrency:MaxAttempts=1` in the test factory, temporary edit, reverted and rebuilt) | PASS (expected failures observed) | The suite fails in **10 of 10** runs with a 409 `concurrency_conflict` where 200/201 or another code was expected. Per test, failures in 10 runs: both write-skew cases 10, new-bin receipts 10, fulfil-vs-cancel 10, 12-request oversell 10, two-request oversell **6**. The two-request count varies between separate measurements (4, 5 and 6 of 10); it is not a fixed figure |
| 12 | Negative control: `product.MarkStockChanged()` commented out in `LowStockMonitor` (temporary, reverted) | PASS (expected failures observed) | The unit test `EveryEvaluation_BumpsProductVersion_SoConcurrentEvaluationsConflict` fails deterministically. The HTTP write-skew regression `ConcurrentCountsAtTwoLocations_...` fails in **10 of 10**, 5 of 5 and 9 of 10 runs across three separate measurements. It is probabilistic (10 iterations of 2 racing requests). The unit test is the guard that catches the removal every time |
| 13 | Negative control: `CloseAsync` reading the order *before* the stock (temporary negative control, reverted) | PASS (expected failures observed) | `ConcurrentFulfilAndCancelOfOneOrder_ExactlyOneWins` fails in **4 of 5** runs (a separate measurement records 1 of 5, so this regression test is probabilistic: it catches the bug in some runs, not all): the loser gets 409 `reservation_mismatch` instead of `invalid_order_state`. With stock read first it passes 10 of 10 (row 10) and 5 of 5 on PostgreSQL (row 9) |
| 14 | Smoke test: `dotnet run` (Development, SQLite file in a scratch directory) + `curl` | PASS | `/health/ready` → `Healthy`, `/swagger/index.html` 200. The OpenAPI document marks the 7 write operations with the `ApiKey` requirement and none of the 5 GETs. `lines:[null]` → 400 `validation_failed` with `errors["Lines[0]"]` and a W3C `traceId` (`00-1f246cb2…-bb11b4df…-00`). Order 201 `placedAt` `…02:51:22.009+00:00`, identical in the later GET. Alert body has `thresholdAtRaise`. Unknown route → 404 `not_found` with a W3C `traceId` |
| 15 | `bicep build infra/main.bicep` | PASS | exit 0, no diagnostics. The ARM JSON has **12 resources**, including a user-assigned identity and its Key Vault role assignment. The web app `dependsOn` the role assignment |
| 16 | `bicep lint infra/main.bicep` | PASS | exit 0, no warnings |
| 17 | `bicep build-params infra/main.bicepparam` without secrets in env | PASS (expected error) | `BCP427: Environment variable "POSTGRES_ADMIN_PASSWORD" does not exist...` (and the same for `WRITE_API_KEY`), so secrets cannot silently default to empty |
| 18 | `bicep build-params` with placeholder env values | PASS | exit 0 (output to stdout, discarded). A placeholder API key shorter than 24 characters is rejected with `BCP333` (the template's `@minLength(24)`) |
| 19 | `.github/workflows/ci.yml` YAML parse (PyYAML `safe_load` via `uv run --with pyyaml`) | PASS | `valid YAML; jobs: ['build-test', 'postgres-integration', 'docker', 'bicep']` |
| 20 | `docker build -t stockroom .` (multi-stage, `restore --locked-mode`) | NOT_RUN | Container dependency access and local build permissions prevent building the image in this test environment. The CI `docker` job builds the committed `Dockerfile`. A separate local image build passed with an environment-specific SDK image; it does not establish a build of the default SDK image here |
| 21 | `docker run` (Production, SQLite) + `curl`, and the container against PostgreSQL | NOT_RUN | Requires the image from row 20. Separate container smoke-test results: PASS for both providers, uid 1654 (non-root `app`), `Healthy`, both migrations applied on PostgreSQL |
| 22 | Database CHECK constraint on PostgreSQL: `UPDATE "StockItems" SET "OnHand" = -1` | NOT_RUN | Local Docker command permissions prevent repeating this check. A separate constraint test rejects the update with `violates check constraint "CK_StockItems_OnHand_NonNegative"`; row 8 reports no pending model changes |
| 23 | GitHub Actions run itself | NOT_RUN | No GitHub Actions execution is recorded. Local checks cover the steps in rows 1-10 and 15-16. `az bicep` is only available on the GitHub runner |
| 24 | Azure deployment (`az deployment group create`) | NOT_RUN | Validated, never deployed: no cloud account is used and no cost is incurred. Infrastructure validation only |

## macOS integration limitation

A separate test record uses macOS arm64, .NET SDK 8.0.425 and runtime 8.0.31.

| Command | Result | Detail |
| --- | --- | --- |
| `dotnet test Inventory.sln --no-build --no-restore -nr:false --results-directory <results> --logger trx` | FAIL | Domain and Application unit tests pass. SQLite HTTP integration tests fail during `System.Net.CookieContainer` initialisation with `System.InvalidOperationException: GetDomainName: -1` from `Interop.Sys.GetDomainName`; the affected tests do not reach their HTTP assertions. This record does not establish a macOS integration-suite pass |

`<results>` denotes the test-result output directory. The Linux results above remain specific to
the recorded Linux environment.

## Measurement notes

- **Docker image size.** `docker image inspect --format {{.Size}} stockroom` reports
  112,677,674 bytes (112.7 MB) of *compressed content* on the containerd-snapshotter daemon used
  for the separate image build. This is not an unpacked image-size measurement. The image is not
  rebuilt in the recorded checks (row 20). On the same daemon, `docker images` reports the
  `mcr.microsoft.com/dotnet/aspnet:8.0` base alone at **320 MB disk usage / 90.3 MB content size**;
  the unpacked application image is larger than 320 MB. An unpacked application image size is not
  recorded. `docker images` or `docker save stockroom | wc -c` can provide a separate size measurement.
- The separate local image build uses an environment-specific SDK image via `--build-arg SDK_IMAGE=...`.
  The committed `Dockerfile` defaults to `mcr.microsoft.com/dotnet/sdk:8.0`, which CI uses.
- Integration-suite durations are about 7-16 s in the shared 4-vCPU Linux container;
  concurrent builds affect timing.
- Negative controls (rows 11-13) use temporary edits only. Recorded positive checks use the restored
  code, with all controls removed and the application rebuilt.

## Bugs found by testing and fixed

- **Fulfil/cancel read ordering:** reading the order before a competing commit and stock after it
  pairs an open order with released stock. The domain rejects it with `reservation_mismatch` before
  the version check can retry. Reading stock before the order returns `invalid_order_state` instead.
  Regression: `ConcurrentFulfilAndCancelOfOneOrder_ExactlyOneWins` (row 13). No stock or data is
  corrupted; the rejected operation writes nothing.
- **Null order lines:** FluentValidation's `ChildRules` skips null elements, allowing `lines:[null]`
  to reach the service and return 500. Explicit `NotNull()` validation and a service guard return
  400 `validation_failed`. Regression: `PlaceOrder_NullLines_Returns400NotA500` (row 14).
- **Timestamp precision:** a write response can expose an in-memory instant more precise than the
  database stores. Normalising stored timestamps to UTC whole milliseconds makes the write response
  match later reads. Regression: `Timestamps_AreTheSameInTheWriteResponseAndInLaterReads` (row 14).
- **Stock-total overflow:** `int` totals can overflow after a write commits and on later reads.
  Per-location capacity checks and `long` totals keep writes and reads valid. Regressions:
  `HugeQuantities_AreRejectedWith4xx_AndReadsKeepWorking` and `Totals_AboveIntRange_AreSummedAsLong`.
- **Low-stock write skew:** concurrent counts at different locations can miss an alert or make a valid
  count fail on a duplicate alert. A product-level concurrency token and retryable unique-index
  conflicts force a fresh decision. Regressions: `ConcurrentCountsAtTwoLocations_BothSucceed_WithExactlyOneOpenAlert`,
  `EveryEvaluation_BumpsProductVersion_SoConcurrentEvaluationsConflict` and `ModelTests` (row 12).

## 2026-10-03 local verification

Environment: restricted macOS arm64 sandbox, .NET SDK 8.0.425, runtime 8.0.31 and local dotnet-ef
8.0.31. These results supplement the earlier Linux measurements; no new full-suite or coverage
measurement is claimed.

Commands used writable temporary .NET/NuGet caches. Default parallel MSBuild attempts stalled or
reported forbidden named-pipe socket binds. Later commands used `DOTNET_PROCESSOR_COUNT=1`,
`UseSharedCompilation=false` and disabled node reuse; workload update checks were disabled locally.
These settings were not added to the repository. Online restore still required unreachable NuGet
vulnerability data. A separate restore used the existing local package feed with `NuGetAudit=false`
for that invocation only, retaining locked mode and every committed package version. This fallback
verifies dependency restoration, not the online vulnerability audit.

| Check / command | Result | Evidence |
| --- | --- | --- |
| `dotnet restore --locked-mode && dotnet format --verify-no-changes --no-restore` | BLOCKED | Restore exits 1 with `NU1900`: vulnerability data from `https://api.nuget.org/v3/index.json` is unreachable. The chained formatter does not run. An initial default-parallel attempt timed out |
| `dotnet restore --locked-mode --source <local-feed> -p:NuGetAudit=false` | PASS | All seven projects restored; no lockfile or dependency version changes. Online audit remains BLOCKED |
| `dotnet format --verify-no-changes --no-restore` (separate command after offline restore) | PASS | Exit 0 |
| `dotnet build --configuration Release --no-restore -warnaserror` | PASS | All seven projects built; `0 Warning(s)`, `0 Error(s)`, using the local process settings above. Default parallel invocation timed out |
| `dotnet test --configuration Release --no-build` | BLOCKED | All three test runs abort before assertions: VSTest's `SocketServer.Start` / `TcpListener.Start` throws `SocketException (13): Permission denied`. No test pass count is claimed |
| `dotnet tool restore --configfile <offline-config>` | PASS | Local dotnet-ef 8.0.31 restored from the existing feed |
| `dotnet ef migrations has-pending-model-changes --project src/Inventory.Infrastructure --startup-project src/Inventory.Infrastructure --context SqliteInventoryDbContext --configuration Release --no-build` | PASS | `No changes have been made to the model since the last migration.` |
| Same migration check with `--context PostgresInventoryDbContext` | PASS | `No changes have been made to the model since the last migration.` |
| Native Development startup and HTTP probes | BLOCKED | Initial plain `dotnet run --project src/Inventory.Api` readiness attempt timed out at 35 seconds. With the polling-watcher workaround below, the launch profile selects Development, applies SQLite migrations and attempts `http://localhost:5000`; Kestrel then throws `Permission denied` on bind. No curl response, Swagger or readiness pass is claimed |
| SQLite startup migration history | PASS | The scratch database from the launch-profile run contains `InitialCreate` and `AddProductVersion`, proving startup migrations ran without an explicit environment prefix |
| Release coverage and PostgreSQL integration tests | BLOCKED | VSTest cannot open its communication socket; Docker access is also denied. These steps were not repeated after the prerequisite failures |
| `docker info --format '{{.ServerVersion}}'`; Docker build / container smoke | BLOCKED | Docker daemon socket access is denied. Build and smoke were not attempted after this probe |
| Bicep build / lint | NOT_RUN | Neither Bicep nor Azure CLI is installed; no global installation performed |
| Workflow YAML parse | NOT_RUN | PyYAML is unavailable; the existing workflow was not changed |
| Launch-profile JSON, README shell syntax / local links, Quickstart command count | PASS | JSON parses; Development and port 5000 match the README; every Bash block passes `bash -n`; local links resolve; Quickstart has five commands |
| Repository hygiene and `git diff --check` | PASS | Existing `.editorconfig` and `.gitignore` cover build, test, coverage and SQLite output. No generated build/database junk is tracked; LICENSE and dependency versions are unchanged; diff has no whitespace errors |
| GitHub Actions execution / Azure deployment | NOT_RUN | No remote workflow was triggered and no infrastructure was deployed |

### Native startup diagnosis

The earlier native startup timeout was reproduced in a disposable, dependency-free ASP.NET Core
probe: it printed immediately before `WebApplication.CreateBuilder` and then stalled there.
`DOTNET_USE_POLLING_FILE_WATCHER=1` let that same probe complete host construction, after which its
Kestrel socket bind was denied. The same per-process setting let stockroom apply its migrations and
reach the identical binding restriction. Both the direct DLL and the launch-profile run were tried;
no source instrumentation or watcher configuration was committed.

Independent system probes confirmed a local TCP bind returns `Operation not permitted` and
`getdomainname()` returns `-1`, errno `1`. The latter also explains the earlier restricted macOS
`CookieContainer` errors. These are environment limitations, not failing domain assertions. The
startup and full HTTP checks still need an unrestricted environment; the Quickstart response is an
illustrative response matching the contracts, not a response captured in this sandbox.

Logs and scratch databases are outside the repository in `/private/tmp/stockroomRT`. Native startup
process groups were stopped. No commits, pushes, branch creation, runtime dependency additions or global
installations were performed.

## Host re-run (2026-10-03, macOS arm64, .NET SDK 8.0.425 selected by global.json)

| Check | Result | Evidence |
|---|---|---|
| `dotnet restore --locked-mode && dotnet format --verify-no-changes --no-restore` | PASS | No changes needed. |
| `dotnet build --configuration Release --no-restore -warnaserror` | PASS | 0 warnings. |
| `dotnet test --configuration Release --no-build` (SQLite) | PASS | 48 domain + 7 application + 44 integration = 99 passed, 0 failed. |
| `dotnet run --project src/Inventory.Api` (no environment variable) | PASS | Uses the committed `launchSettings.json`: "Hosting environment: Development", listening on `http://localhost:5000`, `/swagger/index.html` returns 200. |
