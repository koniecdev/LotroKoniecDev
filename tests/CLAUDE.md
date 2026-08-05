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

Full project inventory (14 projects):

| Project | Kind | Needs |
|---|---|---|
| `LotroKoniecDev.Tests.Unit` | patcher unit | nothing (pure) |
| `LotroKoniecDev.Architecture.Tests.Unit` | repo-wide structural rules over assembly IL (NetArchTest.Rules) | nothing (pure) |
| `LotroKoniecDev.SharedKernel.Tests.Unit` | SharedKernel unit (monads, BuildingBlocks, Ensure, strongly-typed IDs) | nothing (pure); Stryker target |
| `LotroKoniecDev.Logging.Tests.Unit` | `Utilities/Logging` redaction unit | nothing (pure) |
| `LotroKoniecDev.TranslationSystem.Domain.Tests.Unit` | TMS domain unit | nothing (pure); Stryker target |
| `LotroKoniecDev.TranslationSystem.API.Tests.Unit` | TMS handler/auth/endpoint unit (fake read-DbContext, stub accessor/provisioner) | nothing (pure) |
| `LotroKoniecDev.Frontend.Tests.Unit` | Blazor SSR component + infrastructure unit (bUnit-style) | nothing (pure) |
| `LotroKoniecDev.AuthSystem.API.Tests.Unit` | auth-api unit (cold-start seed retry policy) | nothing (pure) |
| `LotroKoniecDev.TranslationSystem.API.Tests.Integration` | in-process API against real PostgreSQL (Testcontainers; forged test tokens) | Docker |
| `LotroKoniecDev.AuthSystem.API.Tests.Integration` | in-process auth-api against real PostgreSQL (Testcontainers) | Docker |
| `LotroKoniecDev.TranslationSystem.E2E.Tests` | real-process TMS stack over HTTP | Docker (3 images) |
| `LotroKoniecDev.Frontend.E2E.Tests` | Playwright browser stack | Docker (4 images + browser) |
| `LotroKoniecDev.Tests.Infrastructure` | patcher real-infrastructure adapters | Windows to run |
| `LotroKoniecDev.Tests.E2E` | patcher CLI full pipeline | Windows + real DAT (else skipped) |

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
- **NaughtyStrings** — the Big List of Naughty Strings, for hostile-input theories (see below)
- **NetArchTest.Rules** — architecture tests (assembly IL only; `LotroKoniecDev.Architecture.Tests.Unit`)
- **Verify.Xunit** — snapshot tests for API response contracts and rendered SSR markup (see below)
- **Xunit.SkippableFact** — E2E tests that need Windows + a real DAT
- **coverlet.collector** — code coverage
- Versions: `Directory.Packages.props` is the single source of truth.

## Hostile-string theories (`tests/Shared/NaughtyStringCases.cs` — #569)

String-heavy seams are hardened with the **Big List of Naughty Strings** (the `NaughtyStrings`
package): emoji and other surrogate pairs, RTL/bidi controls, zero-width and combining marks,
quote/escape/injection soup, non-ASCII digits. The theory sources live in **one file**,
`tests/Shared/NaughtyStringCases.cs`, **linked** (`<Compile Include="..\Shared\…" Link="Shared\…" />`)
into every pure unit suite that needs them — `Tests.Unit`, `TranslationSystem.API.Tests.Unit`,
`TranslationSystem.Domain.Tests.Unit`. Add the link + the `NaughtyStrings` PackageReference to a new
suite rather than copy-pasting lists; add a category source there rather than inlining one in a test.

Sources: `All` (everything), `UnicodeHazards` (UTF-16 length hazards), `DelimiterHazards` (collides
with a delimited text format), `NonAsciiDigits` (id columns), `SubmittableText` / `BlankText` (what
the API's `NotEmpty()` does and does not let through as translated text), plus `AllValues` — the raw
list, for a suite that must filter the corpus down to what its seam accepts **before** building a
`TheoryData`, so its assertion stays unconditional instead of hiding in an `if`. Entries are
de-duplicated: the upstream list repeats two, and a duplicate logs a "Skipping test case with
duplicate ID" line on every run.

Both sides of the `||` contract carry hostile round-trip coverage:
`TranslationFileParserNaughtyStringTests` + `FragmentNaughtyStringTests` (patcher — parser and the
VarLen/UTF-16 binary writer), `TranslationFileNaughtyStringTests` + `ParserContractParityTests`
(TMS — serializer, import parser and the cross-parser drift guard),
`NaughtyStringValueObjectTests` (TMS domain — a VO factory answers with a `Result`, never an
exception).

**The corpus does not reach every interesting case.** It carries no empty string, no real newline,
no literal `\r`/`\n` escape sequence and no entry ending in an odd run of `|` — exactly the
delimiter collisions this format is weakest at. Those are composed by hand as explicit
`[InlineData]` theories next to the corpus-driven ones; when you add a seam, check whether its
hazard is actually present in the data before trusting a `MemberData` theory to cover it.

Several tests pin **known-lossy** behavior on purpose, say so in-place, and name the defect ticket
they document (#596 escape asymmetry, #597 odd trailing pipe, #598 over-long piece). Do not "fix"
such a test — fix the defect, then change the assertion deliberately.

## Snapshot tests (Verify — #571)

Snapshot testing is for **one specific shape of assertion**: the target is a large structured output
that hand-written asserts only ever cover in part. Two seams qualify today, and the pilot lives in a
`Snapshots/` folder in each:

| Suite | Snapshots | Pins |
|---|---|---|
| `TranslationSystem.API.Tests.Integration/Tests/Snapshots/` | `TranslationContractSnapshotTests` (HATEOAS list, public list, middle page, empty envelope, HATEOAS detail, plain-JSON list), `ProblemDetailsSnapshotTests` (400/404/422 + 401/403) | the whole response body — property names, nesting, JSON types, the complete link set |
| `Frontend.Tests.Unit/Snapshots/` | `HomeMarkupSnapshotTests` (4 states incl. zero-catalog), `TermsMarkupSnapshotTests` | the whole rendered SSR markup of the page |

Seed for the shape you are pinning, not for the shape that is easy: the API fixture seeds three rows
so a **middle page** (the only place `previous-page` and `next-page` both appear) and the **empty
envelope** are covered, and the Home counters are five-figure so the NBSP group separator actually
renders. A snapshot of the one well-formed happy payload is the easiest kind to write and the least
worth having.

**When a snapshot is the right tool:**

- **Golden fixtures** own the `||` file contract on both sides. Snapshots add nothing there and must
  never replace them — that format changes only via ADR, and its fixtures are round-tripped, not
  compared.
- **Plain asserts** own behavior: "an admin sees `approve`, a translator does not" is a statement
  about many inputs, so it stays a `[Theory]`. A snapshot answers "did *anything* about this payload
  change", which is a different question.
- **Snapshots** own shape. They exist for the fields nobody thought to assert. They complement the
  behavioral suites (`TranslationAggregateHateoasTests`, `ProblemDetailsContractTests`, `HomeTests`,
  `TermsTests` all stay) — deleting an assert because "the snapshot covers it" is the wrong move.

**Re-accepting a verified file is a deliberate, reviewed act.** `*.verified.*` files are committed
and ARE the pinned contract; `*.received.*` files are that run's scratch output and are git-ignored.
When a snapshot fails, read the diff: either the change was intended (rename the received file over
the verified one, in the same PR, so the diff is reviewable) or you just found a regression. Never
re-accept a batch of snapshots without reading each diff.

Everything is configured once in **`tests/Shared/VerifyModuleInitializer.cs`**, linked into every
snapshot suite (same idiom as `NaughtyStringCases.cs`) so all of them scrub identically — inline
GUIDs, ISO-8601 instants, 64-char hex digests (the translation file's SHA-256, which is also the
strong `ETag`) and the `traceId` ASP.NET Core stamps on every `ProblemDetails`. The diff-tool
launcher is disabled there too: most runs are headless, and a GUI popping up would hang the run.

Two properties worth knowing before you add one:

- The scrubbers are **shape-matched**, not positional — a regex only fires on text that still looks
  like a GUID / an instant / a digest. A contract regression that changes the shape (an instant
  serialized as a Unix epoch) leaves the raw value in the snapshot and fails the test, which is the
  point.
- API snapshots go through `ApiSnapshot`, which re-indents the body via `JsonNode`. What is pinned is
  the **logical** contract (names, nesting, JSON types, values), not the transport's encoding of
  non-ASCII — so Polish reads as Polish in the verified file. Nothing else covers that encoding
  (`ContentNegotiationTests` pins the media type and `Vary`, not the bytes), so `ApiSnapshot`
  carries its own `ShouldServeUtf8Async` assert on the **raw** body and every snapshot with Polish
  in it calls that too.
- A markup snapshot is the component's **rendered body only**. bUnit's `Markup` does not include
  `<PageTitle>` / `<HeadContent>`, so head metadata needs its own assert.
- A snapshot suite is the sanctioned exception to "unit tests are pure: no filesystem". It reads its
  `*.verified.*` from the source tree at runtime (and writes `*.received.*` on failure) — that file is
  the pinned contract, not an assertion about generated output, so `Frontend.Tests.Unit` stays a pure
  suite in every sense the rule is about.

Add a snapshot when the payload is big and the existing asserts cover a corner of it. Do not add one
to a small `Result` or a three-field DTO — a plain assert says more, and says it in the failure
message.

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
    DatFileHandlerExtensionsTests.cs  LoadSubFile guards against corrupt subfile data
    ResultExtensionsTests.cs  Map, Bind, OnSuccess, OnFailure, Match, Combine
  Features/
    ExportTextsQueryHandlerTests.cs    Export handler (mocked IDatFileHandler) + validation guards
    ApplyPatchCommandHandlerTests.cs   Patch handler (mocked IPatchingService, real validator)
    GameLaunchingCommandHandlerTests.cs Launch handler + SimplifiedGameLaunchingStrategy branches
    PreflightCheckQueryHandlerTests.cs Preflight checks (process, write access, update check)
    PatchingServiceTests.cs            Patch orchestration (mocked IDatFileHandler + ITranslationParser)
    SyncTranslationFileCommandHandlerTests.cs TMS file sync (HTTPS-only URL guard, integrity rejection, ETag/304, offline fallback)
    TranslationFileContentIntegrityTests.cs   Downloaded-file SHA-256/ETag integrity check (AUDIT-SEC-01)
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

## Architecture Tests (LotroKoniecDev.Architecture.Tests.Unit)

**The mechanical guard for the structural house rules** — the ones CLAUDE.md and the ADRs state in prose
and code review used to be the only thing enforcing. Pure assembly reflection (no DB, no filesystem, no
network), so it runs in the normal unit gate on every OS.

| File | Rule |
|---|---|
| `PatcherLayeringTests` | Cli / Infrastructure -> Application -> Domain -> Primitives; the lower layer never reaches up |
| `NoMediatorTests` | ADR-0001 — no `Mediator` / `MediatR` type anywhere in production IL |
| `BoundedContextIsolationTests` | patcher <-> TMS share the `\|\|` file, never code; the Frontend sees `Contracts`, never a domain or DbContext; the TMS never links the auth server internals |
| `PersistenceDirectionTests` | patcher/TMS domain, read models and contracts carry no EF Core or Npgsql |
| `CqrsSeparationTests` | a query handler never injects a repository, `IUnitOfWork` or the write DbContext; a command handler injects `IValidator<TCommand>` |
| `HandlerConventionTests` | handlers are `internal sealed`; no `IValidator<TQuery>` (validators are command-only) |
| `SealedConventionTests` | every class is sealed unless another production type derives from it |
| `SuiteSelfTests` | the suite can still say "no" — pins a dependency that genuinely exists, and fails when a production project escapes the search set |

Two mechanisms, on purpose: **NetArchTest.Rules** for dependency rules (it scans the full IL of every
member), **plain reflection** for convention rules its predicate DSL cannot express ("sealed unless
something inherits it", a validator's generic argument).

Adding a production project? Add the `ProjectReference` **and** the entry in
`Shared/ProductionAssemblies.All` — `SuiteSelfTests` fails until you do. The two patcher
`net10.0-windows` assemblies (Infrastructure, Cli) are absent on purpose: a `net10.0` project cannot
reference them, and they sit at the TOP of the layering, so rules about them are stated as forbidden
namespaces on the assemblies below.

`SealedConventionTests.KnownViolations` quarantines pre-existing violations, each keyed to its own
ticket (#570 is a test-only change). The list is **self-cleaning** — a second test fails once an entry
stops violating, so a fix cannot land without removing it.

## Infrastructure Tests (LotroKoniecDev.Tests.Infrastructure)

Tests that touch real infrastructure adapters (today: `GameLauncherTests`,
`AuthenticodeLauncherSignatureVerifierTests`, `DatExportNativeTests`,
`TranslationFileDownloaderTests` and `ForumPageFetcherTests` — the network ones drive the
adapters' integrity, response-size-cap and timeout enforcement points over an in-memory stub
`HttpMessageHandler`, no real network). This project is patcher-side only — TMS
PostgreSQL/EF Core coverage lives in the `*.Tests.Integration` projects. Asserting on real
file output belongs here, never in `Tests.Unit`. Targets `net10.0-windows`: builds everywhere,
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
`*.Tests.Integration`), so the `pr-verify.yml`/`ci.yml` test-discovery globs skip it. It runs via
`.github/workflows/e2e.yml` (`workflow_dispatch`, plus PRs touching `Directory.Packages.props`,
`.config/dotnet-tools.json` or any Dockerfile — CI-03/#433, so Dependabot bumps exercise it) or a local
`dotnet test` of the project. It IS compiled by the solution build, so the zero-warning gate still covers it.

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
name** (`...E2E.Tests`) — runs via `e2e.yml` (the `e2e-frontend` job; `workflow_dispatch` or PRs touching
package/Dockerfile dependency manifests — CI-03/#433) or a local `dotnet test`. Compiled by the
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
