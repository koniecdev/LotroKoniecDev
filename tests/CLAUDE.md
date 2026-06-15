# Tests

## Run Tests

```bash
dotnet test                                            # everything runnable on this OS
dotnet test tests/LotroKoniecDev.Tests.Unit            # unit only — always green, every OS
dotnet test tests/LotroKoniecDev.Tests.Infrastructure  # real-infrastructure tests
dotnet test tests/LotroKoniecDev.Tests.E2E             # full CLI pipeline — auto-skips off-Windows
dotnet test --filter "FullyQualifiedName~Fragment"     # filter by name
```

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

Tests that touch real infrastructure adapters (today: `GameLauncherTests`). Grows in M2
(PostgreSQL + EF Core) and whenever file-content verification is needed — asserting on real file
output belongs here, never in `Tests.Unit`. Targets `net10.0-windows`: builds everywhere,
**runs on Windows only** (on macOS the run aborts — expected).

## E2E Tests (LotroKoniecDev.Tests.E2E)

Full-pipeline tests driving the CLI (`ExportE2ETests`, `PatchE2ETests`, `RoundtripE2ETests`,
`ErrorPathE2ETests`) against a real DAT. They use `SkippableFact` and **skip automatically**
off-Windows or without a DAT available — `Skipped` on macOS is expected, not a failure.

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
