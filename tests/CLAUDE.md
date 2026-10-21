# Tests

The testing rules every session must follow live in the root `CLAUDE.md` → "Testing philosophy".
This file holds the details per suite.

## Run Tests

```bash
dotnet test                                            # everything runnable on this OS; exits 0 when it all passes
dotnet test tests/LotroKoniecDev.Tests.Unit            # unit only — always green, every OS
dotnet test tests/LotroKoniecDev.Tests.Infrastructure  # real-infrastructure tests — Windows only; elsewhere prints "not run"
dotnet test tests/LotroKoniecDev.Tests.E2E             # full CLI pipeline — Windows only; elsewhere prints "not run"
dotnet test tests/LotroKoniecDev.TranslationSystem.E2E.Tests  # real-process TMS stack — REQUIRES Docker (builds 3 images)
dotnet test tests/LotroKoniecDev.Frontend.E2E.Tests          # browser stack via Testcontainers — REQUIRES Docker (4 images + Playwright)
dotnet test --filter "FullyQualifiedName~Fragment"     # filter by name
```

Full project inventory (16 projects):

| Project | Kind | Needs |
|---|---|---|
| `LotroKoniecDev.Tests.Unit` | patcher unit | nothing (pure) |
| `LotroKoniecDev.Architecture.Tests.Unit` | repo-wide structural rules over assembly IL (NetArchTest.Rules) | nothing (pure) |
| `LotroKoniecDev.SharedKernel.Tests.Unit` | SharedKernel unit (monads, building blocks, `Ensure`, strongly typed IDs) | nothing (pure); Stryker target |
| `LotroKoniecDev.Logging.Tests.Unit` | `Utilities/Logging` redaction unit | nothing (pure) |
| `LotroKoniecDev.TranslationSystem.Domain.Tests.Unit` | TMS domain unit | nothing (pure); Stryker target |
| `LotroKoniecDev.TranslationSystem.API.Tests.Unit` | TMS handlers, auth and endpoints (fake read DbContext, stub accessor and provisioner), and the account event consumer's pauses on a fake clock (#1223) | nothing (pure) |
| `LotroKoniecDev.Frontend.Tests.Unit` | Blazor SSR components and infrastructure (bUnit) | nothing (pure) |
| `LotroKoniecDev.DesktopApp.Tests.Unit` | desktop app UI (#41): the view models (incl. the progress bar the patcher handlers report to), the main window layout and the splash hand-over, on Avalonia's headless platform | nothing (no screen; runs on Linux too) |
| `LotroKoniecDev.AuthSystem.API.Tests.Unit` | auth-api: startup settings checks, handlers, middleware, pages, services, outbox and inbox, seed retry policy, when the timed jobs run (#948) | nothing (pure) |
| `LotroKoniecDev.TranslationSystem.API.Tests.Integration` | in-process API against real PostgreSQL (Testcontainers; forged test tokens) | Docker |
| `LotroKoniecDev.AuthSystem.API.Tests.Integration` | in-process auth-api against real PostgreSQL (Testcontainers) | Docker |
| `LotroKoniecDev.Frontend.Tests.Integration` | in-process Frontend host in Production, as every box runs it (`WebApplicationFactory`, #1187): startup checks such as the caller keys (#915); the real OIDC sign-in against a stand-in token endpoint (the token answer it accepts, #1026, and the refresh time it stores, #1025); pages and sign-in while the sign-in server is down (#1058, #1153); every page's form post while its data fails to load (#1122, which is why its csproj repeats one Frontend runtime switch) | nothing (no Docker); a temp keyring folder |
| `LotroKoniecDev.TranslationSystem.E2E.Tests` | real-process TMS stack over HTTP | Docker (3 images) |
| `LotroKoniecDev.Frontend.E2E.Tests` | Playwright browser stack | Docker (4 images + browser) |
| `LotroKoniecDev.Tests.Infrastructure` | patcher real-infrastructure adapters | Windows + the x86 .NET runtime (elsewhere not run) |
| `LotroKoniecDev.Tests.E2E` | patcher CLI full pipeline | Windows (elsewhere not run) + real DAT (else skipped) |

> **Bare `dotnet test` needs Docker.** Both `*.E2E.Tests` suites run whenever the whole solution
> is tested, and each builds its images and boots its stack. They are off the PR/CI gate by name
> (below); target a specific project when you have no Docker.

## Mutation Testing (Stryker.NET)

Mutation testing runs on the two purest, highest-value layers, `TranslationSystem.Domain` and
`SharedKernel`, as a **PR gate** (`.github/workflows/mutation-test.yml`: path-filtered +
`workflow_dispatch`, one matrix leg per target). The tool is pinned in `.config/dotnet-tools.json`;
each target has its own `stryker-config.json`.

```bash
dotnet tool restore                                            # once — restores dotnet-stryker (pinned)

# Run from each test project directory; writes ./StrykerOutput/ (gitignored) with the HTML report:
( cd tests/LotroKoniecDev.TranslationSystem.Domain.Tests.Unit && dotnet stryker )
( cd tests/LotroKoniecDev.SharedKernel.Tests.Unit            && dotnet stryker )
```

Thresholds are high 80 / low 70 / **break 67**: `break` fails the run (and the CI leg) when the
mutation score drops below it. It is a calibrated starting point; adjust it in the per-project
`stryker-config.json` after reading the baseline report.

## Framework & Libraries

- **xUnit** (`Fact`, `Theory`, `InlineData`), **Shouldly** (`.ShouldBe()`, …) and **NSubstitute**
  (`Substitute.For<IInterface>()`): the test framework, assertions and mocks in every suite.
- **NaughtyStrings**: the Big List of Naughty Strings, for hostile-input theories (see below).
- **NetArchTest.Rules**: architecture tests only (`LotroKoniecDev.Architecture.Tests.Unit`).
- **Verify.Xunit**: snapshot tests for API response contracts and rendered SSR markup (see below).
- **bUnit**: renders Blazor components in `Frontend.Tests.Unit`.
- **Avalonia.Headless**: the desktop app's UI tests (#41) run the real `App` and windows on
  Avalonia's headless platform. One session per run (`HeadlessSessionFixture`), and every window
  is touched only inside `Session.Dispatch`, on the session's UI thread.
- **Microsoft.AspNetCore.Mvc.Testing**: the in-memory hosts of the three integration suites.
- **Testcontainers** (PostgreSQL, RabbitMQ) and **Microsoft.Playwright**: real databases, brokers
  and the E2E stacks.
- **Microsoft.Extensions.TimeProvider.Testing**: `FakeTimeProvider`, for code that waits on a
  `TimeProvider` timer, which a substitute cannot fire. Used for the response-time floor (ADR-0059)
  and the timed jobs (#948) in `AuthSystem.API.Tests.Unit`; for the broker consumers' pauses in both
  API unit suites (#1223, through the linked `tests/Shared/TimerWatchingTimeProvider`); as
  OpenIddict's stopped clock in `AuthSystem.API.Tests.Integration` (#1014); for the sign-out
  revoke's time limit in `Frontend.Tests.Unit` (#964); and for the access token's refresh time in
  both Frontend suites (#1025).
- **Xunit.SkippableFact**: E2E tests that need Windows and a real DAT.
- **Bogus**: generated user data in `AuthSystem.API.Tests.Integration` and `TranslationSystem.E2E.Tests`.
- **coverlet.collector**: code coverage.
- Versions: `Directory.Packages.props` is the single source of truth.

## Hostile-string theories (`tests/Shared/NaughtyStringCases.cs` — #569)

String-heavy seams are tested with the **Big List of Naughty Strings**: emoji and other surrogate
pairs, RTL/bidi controls, zero-width and combining marks, quote/escape/injection soup, non-ASCII
digits. The theory sources live in **one file**, `tests/Shared/NaughtyStringCases.cs`, **linked**
(`<Compile Include="..\Shared\…" Link="Shared\…" />`) into each pure unit suite that needs them:
`Tests.Unit`, `TranslationSystem.API.Tests.Unit`, `TranslationSystem.Domain.Tests.Unit`. A new
suite adds the link and the `NaughtyStrings` package reference instead of copying lists, and a new
category goes into that file instead of a test.

Sources: `All`, `UnicodeHazards` (UTF-16 length hazards), `DelimiterHazards` (collides with a
delimited text format), `NonAsciiDigits` (id columns), `SubmittableText` / `BlankText` (what the
API's `NotEmpty()` lets through as translated text, and what it does not), `PipeRuns` (not from the
corpus: every string over `{'a','|'}` up to six characters, 127 entries, so both `||` parsers are
tested against the delimiter collision of #597 exhaustively), and `AllValues`, the raw list, for a
suite that must filter the corpus to what its seam accepts **before** building a `TheoryData`, so
its assertion stays unconditional. Entries are de-duplicated, because a duplicate logs "Skipping
test case with duplicate ID" on every run.

Both sides of the `||` contract have hostile round-trip coverage:
`TranslationFileParserNaughtyStringTests` + `FragmentNaughtyStringTests` (patcher: the parser and
the VarLen/UTF-16 binary writer), `TranslationFileNaughtyStringTests` + `ParserContractParityTests`
(TMS: the serializer, the import parser and the cross-parser drift guard), and
`NaughtyStringValueObjectTests` (TMS domain: a VO factory answers with a `Result`, never an
exception).

**The corpus does not reach every interesting case.** It has no empty string, no real newline, no
literal `\r`/`\n` escape and no entry ending in an odd run of `|`, which are the collisions this
format is weakest at. Those are written by hand as `[InlineData]` theories next to the corpus ones
(and, for pipe runs, generated by `PipeRuns`). When you add a seam, check that its hazard is in the
data before you trust a `MemberData` theory to cover it.

A test that pins **known-lossy** behaviour says so in place and names its defect ticket. Do not
"fix" such a test: fix the defect, then change the assertion on purpose.
`Write_PieceLongerThanTheVarLenCeiling_ShouldThrow` is not such a pin: it tests the last resort
behind `Fragment.IsWritablePiece` (ADR-0043). **No known pin is open today.** The next one added
should name its ticket here.

The `||` rule lives in **two types per context, duplicated by design**: `TranslationLineEscaper`
(the content escape) and `TranslationLineCarver` (the field boundaries). Each has its own unit
suite in each context, plus `BothEscapers_…` / `BothCarvers_…` in `ParserContractParityTests`. The
contexts share the file, never code, so the parity tests are the only thing keeping the copies in
step: **change one copy, change the other in the same commit.** Parity alone is not enough either:
in #597 both parsers carved a line the same wrong way, and only the round trip through each
context's own writer caught it.

## Snapshot tests (Verify — #571)

A snapshot is for a large structured output that hand-written asserts only ever cover in part. Two
seams qualify today, each with a `Snapshots/` folder:

| Suite | Snapshots | Pins |
|---|---|---|
| `TranslationSystem.API.Tests.Integration/Tests/Snapshots/` | `TranslationContractSnapshotTests` (HATEOAS list, public list, middle page, empty envelope, HATEOAS detail, plain-JSON list), `DiscoveryContractSnapshotTests` (the service document, anonymous and as admin), `ProblemDetailsSnapshotTests` (400/404/422 + 401/403) | the whole response body: property names, nesting, JSON types, the complete link set |
| `Frontend.Tests.Unit/Snapshots/` | `HomeMarkupSnapshotTests` (4 states incl. zero-catalog), `TermsMarkupSnapshotTests` | the whole rendered SSR markup of the page |

Seed for the shape you are pinning, not the shape that is easy: the API fixture seeds three rows so
a **middle page** (the only place `previous-page` and `next-page` both appear) and the **empty
envelope** are covered, and the Home counters are five-figure so the NBSP group separator renders.

**Which tool owns what** (the root `CLAUDE.md` states the rule):

- **Golden fixtures** own the `||` file contract on both sides. Snapshots never replace them: the
  format changes only via ADR, and its fixtures are round-tripped, not compared.
- **Plain asserts** own behaviour: "an admin sees `approve`, a translator does not" is a statement
  about many inputs, so it stays a `[Theory]`.
- **Snapshots** own shape: the fields nobody thought to assert. The behavioural suites
  (`TranslationAggregateHateoasTests`, `ProblemDetailsContractTests`, `HomeTests`, `TermsTests`)
  all stay; deleting an assert because "the snapshot covers it" is wrong.

**Re-accepting a verified file is a deliberate, reviewed act.** `*.verified.*` files are committed
and ARE the pinned contract; `*.received.*` files are a run's scratch output and are git-ignored.
When a snapshot fails, read the diff: either the change was intended (rename the received file over
the verified one, in the same PR) or you found a regression. Never re-accept a batch without reading
each diff.

Everything is configured once in **`tests/Shared/VerifyModuleInitializer.cs`**, linked into every
snapshot suite, so all of them scrub the same way: inline GUIDs, ISO-8601 instants, 64-char hex
digests (the translation file's SHA-256, also the strong `ETag`) and the `traceId` of every
`ProblemDetails`. The diff-tool launcher is off there, because a GUI popping up would hang a
headless run.

Worth knowing before you add one:

- The scrubbers match by **shape**: a regex fires only on text that still looks like a GUID, an
  instant or a digest. A regression that changes the shape (an instant as a Unix epoch) leaves the
  raw value in the snapshot and fails the test.
- API snapshots go through `ApiSnapshot`, which re-indents the body via `JsonNode`, so what is
  pinned is the **logical** contract and Polish reads as Polish. Nothing else checks the raw
  encoding, so `ApiSnapshot.ShouldServeUnescapedUtf8Async` asserts it on the **raw** body, and
  every snapshot with Polish in it calls that too.
- A markup snapshot is the component's **rendered body only**. bUnit's `Markup` leaves out
  `<PageTitle>` / `<HeadContent>`, so head metadata needs its own assert.
- A snapshot suite reads its `*.verified.*` files from the source tree (and writes `*.received.*`
  on failure). That is the allowed exception to "unit tests are pure: no filesystem", because the
  file is the pinned contract, not generated output under test.

Do not snapshot a small `Result` or a three-field DTO: a plain assert says more, in the failure
message.

## Unit Tests (LotroKoniecDev.Tests.Unit)

- `Shared/`: `TestDataFactory` (binary SubFile builder; extend it, do not hand-roll bytes) and
  `InMemoryDatFileHandler` (a DAT in memory that keeps state across a patch, an update and launches).
- `Tests/Core/`: `Error`, `ValueObject`, `Result`, `VarLenEncoder`.
- `Tests/Extensions/`: the `Result` extensions and the `LoadSubFile` guards against corrupt data.
- `Tests/Features/`: the handlers and services (export, patch, launch with the Tier 0 wipe repair,
  preflight, TMS file sync, endpoint resolution, integrity, version baseline, update check).
- `Tests/Models/`: `Fragment`, `SubFile`, `Translation`, the source digest and the iteration
  snapshot.
- `Tests/Parsers/`: the translation file parser, `TranslationLineCarver` and
  `TranslationLineEscaper`.

Command handlers are built with their **real FluentValidation validator** (validators have no
dependencies) and mocked ports. Unit tests are **pure** (no filesystem assertions, no network, no
real DAT) and **platform-agnostic**: build paths with `Path.Combine`, never hardcode `C:\`.

## Architecture Tests (LotroKoniecDev.Architecture.Tests.Unit)

The mechanical guard for the structural house rules in `CLAUDE.md` and the ADRs. Pure assembly
reflection (no DB, no filesystem, no network), so it runs in the normal unit gate on every OS.

| File | Rule |
|---|---|
| `PatcherLayeringTests` | Cli / Infrastructure -> Application -> Domain -> Primitives; the lower layer never reaches up |
| `NoMediatorTests` | ADR-0001: no `Mediator` / `MediatR` type anywhere in production IL |
| `BoundedContextIsolationTests` | patcher <-> TMS share the `\|\|` file, never code; the Frontend sees `Contracts`, never a domain or DbContext; the TMS never links the auth server internals |
| `PersistenceDirectionTests` | patcher/TMS domain, read models and contracts carry no EF Core or Npgsql |
| `CqrsSeparationTests` | a query handler never injects a repository, `IUnitOfWork` or the write DbContext; a command handler injects `IValidator<TCommand>` |
| `HandlerConventionTests` | handlers are `internal sealed`; no `IValidator<TQuery>` (validators are command-only) |
| `SealedConventionTests` | every class is sealed unless another production type derives from it |
| `SuiteSelfTests` | the suite can still say "no": pins a dependency that really exists, and fails when a production project escapes the search set |

**NetArchTest.Rules** checks dependency rules (it scans the full IL of every member); **plain
reflection** checks the conventions its predicate DSL cannot express ("sealed unless something
inherits it", a validator's generic argument).

Adding a production project? Add the `ProjectReference` **and** the entry in
`Shared/ProductionAssemblies.All`; `SuiteSelfTests` fails until you do. The three patcher
`net10.0-windows` assemblies (Infrastructure, Cli and the desktop app's exe) are left out because a
`net10.0` project cannot reference them. They sit at the top of the layering, so rules about them
are stated as forbidden namespaces on the assemblies below.

`SealedConventionTests.KnownViolations` is **empty**: a new violation fails the gate. Adding an
entry instead of sealing the type needs a ticket to point at, and a second test fails once an entry
stops violating, so the list cleans itself.

## Infrastructure Tests (LotroKoniecDev.Tests.Infrastructure)

Tests of the patcher's real infrastructure adapters: the game launcher and its Authenticode check,
the `datexport.dll` interop, the sidecar stores (ledger, file cache, iteration snapshot), and the
network adapters (downloader, discovery client, forum fetcher). The network tests drive the
adapters' integrity, response-size and timeout checks over an in-memory stub
`HttpMessageHandler`, with no real network. TMS PostgreSQL/EF Core coverage lives in the
`*.Tests.Integration` projects. A test that asserts on real file output belongs here, never in
`Tests.Unit`.
`DesktopAppDependencyInjectionTests` sits here too: the desktop app's composition root wires the
real x86 adapters, so only this Windows suite can load it.

The project targets `net10.0-windows` with an x86 `PlatformTarget`. It builds everywhere and
**runs on Windows only**, where it needs the x86 .NET runtime (without it the run aborts and
`dotnet test` exits 1). Off Windows, `dotnet test` compiles it, runs none of its tests, prints one
`LotroKoniecDev.Tests.Infrastructure: not run` line and exits 0
(`tests/Shared/WindowsOnlyTestRun.targets`, #816). None of its tests count as Skipped, because none
start.

## E2E Tests (LotroKoniecDev.Tests.E2E)

Full-pipeline tests that drive the CLI (`ExportE2ETests`, `PatchE2ETests`, `RoundtripE2ETests`,
`ErrorPathE2ETests`) against a real DAT. They use `SkippableFact` and **skip** when
`TestData/client_local_English.dat` is missing (setup: the project's `README.md`). Off Windows the
project is not run at all, because the CLI is a win-x86 program: `dotnet test` prints one
`LotroKoniecDev.Tests.E2E: not run` line instead (`WindowsOnlyTestRun.targets`, #816).

## TMS Real-Process E2E Tests (LotroKoniecDev.TranslationSystem.E2E.Tests)

`E2ETestFixture` builds the `auth`/`tms`/`migrator` images (`SKIP_DOCKER_BUILD=true` reuses the
ones this worktree built last time) and starts, on a private network, PostgreSQL (with
`scripts/init-postgres.sh` bind-mounted for the second `lotro_auth` DB), the one-shot migrator,
`auth-api` (env `Testing`: the password-grant `lotrokoniecdev-test` client and a seeded Admin) and
`tms-api` on `http://+:8080`, waiting on `/health/live`. Tests drive the loop over real HTTP with
**real auth-api tokens, checked by tms-api through live JWKS, and lazy translator provisioning**:
the layer the in-process `*.Tests.Integration` suites fake with forged tokens.

- `AuthFlowE2ETests`: a real Admin token is accepted and the first write provisions the translator;
  a registered Translator's token is accepted; no token or a garbage token gets 401.
- `CoreLoopE2ETests`: register version → import → list → upsert → approve → download (+ ETag/304).
- `UpdateCycleE2ETests`: a later import rewords an approved row's source, so the row is invalidated
  and dropped from the file.

## Frontend Browser E2E Tests (LotroKoniecDev.Frontend.E2E.Tests)

A **Playwright** suite over the same Testcontainers idiom (ADR-0009: no compose file, no
FluentDocker). `PlaywrightStackFixture` builds the `auth`/`tms`/`frontend`/`migrator` images and
boots the whole browser-facing stack over HTTPS on one network, plus Mailpit for the real
confirmation e-mail and a Playwright browser container. The host test connects to that browser, so
`https://auth-api:8443` means the same thing to the browser and to the frontend's OIDC
back-channel (the single-`Authority` constraint of ADR-0006). It references no project: it drives
everything over HTTP and the browser. The flows live in `Flows/`: the register → confirm → login →
logout happy path, the auth pages' CSP, the one-time-link pages (back, reload, double click), the
submit-once button, the e-mail change success style, the cookie banner, self-hosted fonts and the
account's GDPR self-service.
Topology, local runs and image clean-up: the project's `README.md`.

Locators follow Playwright's guidance: `GetByRole` / `GetByLabel` first, `data-testid` only where
an element has no stable accessible name (`Infrastructure/Locators.cs`).

## Rules for both E2E suites

- **Off the PR gate by name.** The projects are named `…E2E.Tests` (not `*.Tests.Unit` /
  `*.Tests.Integration`), so the `pr-verify.yml` / `ci.yml` test globs skip them. They run through
  `.github/workflows/e2e.yml` (jobs `e2e` and `e2e-frontend`): by hand with `workflow_dispatch`, and
  on their own only on Dependabot PRs that touch `Directory.Packages.props`,
  `.config/dotnet-tools.json` or a Dockerfile (CI-03 / #433). Or run them locally. The solution
  build compiles them, so the zero-warning gate still covers them.
- **Containers start from image IDs, never from tags (#884).** A fixed tag let a second worktree's
  build move the tag while the first run was starting its containers, so that run tested the other
  worktree's code. `tests/Shared/E2EWorktree.cs`, linked into both projects, builds with
  `--iidfile` and hands each container the ID its own build produced. The tag only names the
  worktree (`<repository>:<suite>-<folder>-<8 hex of the solution path>`, label
  `lotrokoniecdev.e2e`), so `SKIP_DOCKER_BUILD=true` reuses this worktree's images. Never start a
  container from a tag, and never give a fixture its own copy of the build loop.
- **One run of a suite per worktree at a time.** A second run in the same worktree takes the tag,
  Docker's containerd store deletes the first run's image, and that run fails with
  `pull access denied for sha256` (#889).

## N-1 schema seam (integration factories — ADR-0024)

Both API integration factories carry `N1CompatSchemaSeam` (twin copies: keep them in sync). When
`N1_COMPAT_SCHEMA_SCRIPTS_DIR` is set, the factory applies a pre-generated idempotent HEAD schema
script (`translation.sql` / `auth.sql`) to its fresh PostgreSQL container before its own
`MigrateAsync()` (which then does nothing), so the suite runs **its** code against a **newer**
schema. Only `scripts/n1-compat.sh` (the N-1 proof, `n1-compat.yml`) sets the variable; normal
runs are untouched. Every misconfiguration throws. Keep `TRUNCATE … CASCADE` in test resets: a
plain TRUNCATE breaks once a newer schema adds a referencing child table. A new integration suite
with its own DbContext must adopt the seam (a new script file name and the same apply call).

## Conventions

- Test class naming: `{ClassUnderTest}Tests`
- Method naming: `MethodName_Scenario_ExpectedResult`
- AAA structure; assertions inline in the test method, never hidden in helpers
- One reason to fail per test (several Shouldly calls for one concept are fine)
- `[Theory]` + `[InlineData]` for boundary/unhappy-path matrices
- Shouldly style only, no raw `Assert.*`
- Never put `?.` in front of a Shouldly call: on a null value, `?.` skips the rest of the line, so
  nothing is checked and the test passes. Write `ContentType.ShouldNotBeNull().MediaType.ShouldBe(…)`,
  not `ContentType?.MediaType.ShouldBe(…)`. Where a null really is a pass, put the null-conditional
  part in parentheses so the check always runs (#887)
- `.Received()` only for side effects invisible in the return value (cleanup, "must NOT have been
  called on validation failure"), never to mirror internal call patterns
- Shared test data builders go in `Shared/` (extend `TestDataFactory`, do not hand-roll bytes)
- A file a unit or integration test reads is a build input. If an inert rule in
  `scripts/ci/classify-changes.sh` matches it (a script under `scripts/`, a root compose file),
  add it to `CODE_INPUTS` there in the same change, with a case in its self-test. Otherwise a PR
  that changes only that file skips the test, and the failure shows up later in someone else's run
  (#1163). Do not point a test at a file that `paths-ignore` in `.github/workflows/ci.yml` skips
  (`docs/`, `.claude/`, the inert Markdown): main never re-runs the test after a change to it
- Every test database starts from `PostgresImage.Name` (`tests/Shared/PostgresImage.cs`, linked
  into every suite that starts PostgreSQL), never from its own image tag (#1002)
- A test that needs a host outside Development and Testing (because Testing turns the rule off)
  boots it in **Production**, as both boxes run it (`compose.hetzner.yaml`). A Staging row may sit
  next to it, never in its place: a test that boots only Staging stays green when a change breaks
  the rule in Production (#1187)
- Every in-memory host factory overrides `CreateHost` with `HostStartOrder.BuildAndStart`
  (`tests/Shared/HostStartOrder.cs`, linked into the three in-memory host suites), and a new
  `WebApplicationFactory` does the same. Without it, a test that expects a refused start flakes in
  a busy full run (#1040). `HostStartOrderTests` checks the start order on every run
