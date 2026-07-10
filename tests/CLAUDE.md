# Tests

## Run Tests

```bash
dotnet test                                            # everything runnable on this OS
dotnet test tests/LotroKoniecDev.Tests.Unit            # unit only — always green, every OS
dotnet test tests/LotroKoniecDev.Tests.Infrastructure  # real-infrastructure tests
dotnet test tests/LotroKoniecDev.Tests.E2E             # full CLI pipeline — auto-skips off-Windows
dotnet test tests/LotroKoniecDev.TranslationSystem.E2E.Tests  # real-process TMS stack — REQUIRES Docker (builds 3 images)
dotnet test tests/LotroKoniecDev.Frontend.E2E.Tests          # browser stack via Testcontainers — REQUIRES Docker (4 images + Playwright)
dotnet test --filter "FullyQualifiedName~Fragment"     # filter by name
```

> **Heads-up on bare `dotnet test`:** the TMS real-process E2E suite runs whenever the whole solution is
> tested and **requires a running Docker daemon** (it builds the auth/tms/migrator images and boots the
> stack). It is off the PR/CI gate by name (below); target a specific project when you don't have Docker.

## Mutation Testing (Stryker.NET)

Mutation testing runs on the two purest, highest-value layers — `TranslationSystem.Domain` and
`SharedKernel` — as a **PR gate** (`.github/workflows/mutation-test.yml`: path-filtered +
`workflow_dispatch`, matrix over both targets). The tool is pinned in `.config/dotnet-tools.json`;
each target carries its own `stryker-config.json`.

```bash
dotnet tool restore                                            # once — restores dotnet-stryker (pinned)

# Run from each test project directory; writes ./StrykerOutput/ (gitignored) with the HTML report:
( cd tests/LotroKoniecDev.TranslationSystem.Domain.Tests.Unit && dotnet stryker )
( cd tests/LotroKoniecDev.SharedKernel.Tests.Unit            && dotnet stryker )
```

Thresholds are high 80 / low 70 / **break 67** — `break` fails the run (and the CI leg) when the
mutation score drops below it. `break: 67` is a calibrated starting point, not dogma; adjust it in the
per-project `stryker-config.json` after reviewing the baseline report.

## Framework & Libraries

- **xUnit** — test framework (`Fact`, `Theory`, `InlineData`)
- **Shouldly** — `.ShouldBe()`, `.ShouldBeTrue()`, `.ShouldContain()`, …
- **NSubstitute** — mocking: `Substitute.For<IInterface>()`
- **Xunit.SkippableFact** — E2E tests that need Windows + a real DAT
- **coverlet.collector** — code coverage
- Versions: `Directory.Packages.props` is the single source of truth.

## Unit Tests (LotroKoniecDev.Tests.Unit)

```
Shared/
  TestDataFactory.cs        Binary SubFile test data builder (shared across feature tests)
Tests/
  Core/
    BuildingBlocks/
      ErrorTests.cs           Error factory methods, equality, ToString()
      ValueObjectTests.cs     Structural equality semantics
    Monads/
      ResultTests.cs          Result success/failure, value access protection
    Utilities/
      VarLenEncoderTests.cs   Encode/decode roundtrip for various ranges
  Extensions/
    ResultExtensionsTests.cs  Map, Bind, OnSuccess, OnFailure, Match, Combine
  Features/
    ExportTextsQueryHandlerTests.cs    Export handler (mocked IDatFileHandler) + validation guards
    ApplyPatchCommandHandlerTests.cs   Patch handler (mocked IPatchingService, real validator)
    GameLaunchingCommandHandlerTests.cs Launch handler + SimplifiedGameLaunchingStrategy branches
    PreflightCheckQueryHandlerTests.cs Preflight checks (process, write access, update check)
    PatchingServiceTests.cs            Patch orchestration (mocked IDatFileHandler + ITranslationParser)
    VersionBaselineServiceTests.cs     Version baseline persistence rules
    GameUpdateCheckerTests.cs          Forum scraping + version comparison (mocked fetcher + store)
  Models/
    FragmentTests.cs          Binary parse/write roundtrip
    SubFileTests.cs           Serialization/deserialization
    TranslationTests.cs       Property validation
  Parsers/
    TranslationFileParserTests.cs  Line parsing, format validation, edge cases
```

Command handlers are constructed with their **real FluentValidation validator** (validators are
dependency-free) and mocked ports. Unit tests are **pure**: no filesystem assertions, no network,
no real DAT — and **platform-agnostic** (build paths with `Path.Combine`, never hardcode `C:\`;
the suite runs on macOS too).

## Infrastructure Tests (LotroKoniecDev.Tests.Infrastructure)

Tests that touch real infrastructure adapters (today: `GameLauncherTests`,
`TranslationFileDownloaderTests` and `ForumPageFetcherTests` — the network ones drive the
adapters' integrity, response-size-cap and timeout enforcement points over an in-memory stub
`HttpMessageHandler`, no real network). Grows in M2
(PostgreSQL + EF Core) and whenever file-content verification is needed — asserting on real file
output belongs here, never in `Tests.Unit`. Targets `net10.0-windows`: builds everywhere,
**runs on Windows only** (on macOS the run aborts — expected).

## E2E Tests (LotroKoniecDev.Tests.E2E)

Full-pipeline tests driving the CLI (`ExportE2ETests`, `PatchE2ETests`, `RoundtripE2ETests`,
`ErrorPathE2ETests`) against a real DAT. They use `SkippableFact` and **skip automatically**
off-Windows or without a DAT available — `Skipped` on macOS is expected, not a failure.

## TMS Real-Process E2E Tests (LotroKoniecDev.TranslationSystem.E2E.Tests)

A Testcontainers-driven suite that mirrors TheKittySaver's `E2E.Tests`: `E2ETestFixture` builds the
`auth`/`tms`/`migrator` Docker images (`SKIP_DOCKER_BUILD=true` to reuse pre-built ones), spins up a private
network with `postgres:17-alpine` (+ the bind-mounted `scripts/init-postgres.sh` for the second `lotro_auth`
DB), runs the one-shot migrator, then boots `auth-api` (env `Testing` → password-grant `lotrokoniecdev-test`
client + seeded Admin) and `tms-api` on `http://+:8080`, waiting on `/health/live`. Tests drive the loop over
real HTTP with **real auth-api tokens validated by tms-api via live JWKS + lazy translator provisioning** — the
layer the in-process `*.Tests.Integration` suite fakes by forging HS256 tokens.

- `AuthFlowE2ETests` — real Admin token accepted + first write lazily provisions the translator; a registered
  Translator's token is accepted; no/garbage token → 401.
- `CoreLoopE2ETests` — register version → import → list → upsert → approve → download (+ ETag/304).
- `UpdateCycleE2ETests` — a later import reword's an approved row's source → invalidated + dropped from the file.

**Requires Docker; off the PR gate by name** — the project is `...E2E.Tests` (not `*.Tests.Unit` /
`*.Tests.Integration`), so the `pr-verify.yml`/`ci.yml` test-discovery globs skip it. It runs only via
`.github/workflows/e2e.yml` (`workflow_dispatch`) or a local `dotnet test` of the project. It IS compiled by the
solution build, so the zero-warning gate still covers it.

## Frontend Browser E2E Tests (LotroKoniecDev.Frontend.E2E.Tests)

A **Playwright** browser suite over the same Testcontainers idiom (ADR-0009 — **not** a compose file, **not**
FluentDocker). `PlaywrightStackFixture` builds the `auth`/`tms`/`frontend`/`migrator` images, boots the whole
browser-facing stack on one network — postgres + migrator + auth-api + tms-api + **frontend** + **mailpit** —
all over **HTTPS** (a cert generated in C#; SAN = the service DNS names; trusted in-container via an inline root
entrypoint), plus a Playwright `run-server` browser container built by hand (the `Testcontainers.Playwright`
module is unusable on Playwright v1.55+ images — it omits run-server's `--host` and hard-codes a `localhost`
readiness probe; see `PlaywrightStackFixture.StartBrowserAsync`). The host connects to the
in-network browser over WS (`Chromium.ConnectAsync(ws://host:mappedPort/)`), so `https://auth-api:8443` resolves
identically for the browser front-channel and the FE OIDC back-channel — the single-`Authority` constraint
(ADR-0006) held in-network via DNS. Mailpit receives the real confirmation e-mail (no auto-confirm), read back
over its HTTP API. Because the browser is in a container, the host needs **no** `playwright install` (no browser
download). NuGet-only (no `ProjectReference`) — it drives everything over HTTP/the browser.

- `RegisterConfirmLoginLogoutTests` — register (Auth Razor Page) → confirm via the Mailpit link → login through
  the FE OIDC challenge → logout. **No DB seed, no `exported.txt`.** PL-only (the pages have no culture routing).

Locators follow Playwright's guidance: `GetByRole`/`GetByLabel` first, `data-testid` only for the consent
checkboxes + state panels (`register-success`, `confirm-email-success`). **Requires Docker; off the PR gate by
name** (`...E2E.Tests`) — runs via `e2e.yml` (the `e2e-frontend` job) or a local `dotnet test`. Compiled by the
solution build, so the zero-warning gate covers it.

## N-1 schema seam (integration factories — ADR-0024)

Both API integration factories carry `N1CompatSchemaSeam` (twin copies — keep in sync): when
`N1_COMPAT_SCHEMA_SCRIPTS_DIR` is set, the factory applies a pre-generated idempotent HEAD schema
script (`translation.sql` / `auth.sql`) to its fresh PostgreSQL container before its own
`MigrateAsync()` (which then no-ops), so the suite exercises **its** code against a **newer**
schema. Only `scripts/n1-compat.sh` (the MIGR-05 N-1 proof, `n1-compat.yml`) sets the variable —
normal test runs are untouched. Every misconfiguration throws; keep `TRUNCATE … CASCADE` in test
resets (a plain TRUNCATE breaks once a newer schema adds a referencing child table). A new
integration suite with its own DbContext must adopt the seam (new script file name + the same
apply call) to be covered.

## Conventions

- Test class naming: `{ClassUnderTest}Tests`
- Method naming: `MethodName_Scenario_ExpectedResult`
- AAA structure; assertions inline in the test method — never hidden in helpers
- One reason to fail per test (may use multiple Shouldly calls for one concept)
- `[Theory]` + `[InlineData]` for boundary/unhappy-path matrices
- Shouldly style only, no raw `Assert.*`
- `.Received()` only for side effects invisible in the return value (cleanup, "must NOT have
  been called on validation failure") — never to mirror internal call patterns
- Shared test data builders go in `Shared/` (extend `TestDataFactory`, don't hand-roll bytes)
