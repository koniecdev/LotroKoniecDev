# Tests

## Run Tests

```bash
dotnet test                                          # All tests
dotnet test tests/LotroKoniecDev.Tests.Unit          # Unit only
dotnet test tests/LotroKoniecDev.Tests.Integration   # Integration only
dotnet test --filter "FullyQualifiedName~Fragment"    # Filter by name
```

## Framework & Libraries

- **xUnit 2.9.3** - Test framework (Fact, Theory, InlineData attributes)
- **FluentAssertions 8.0.0** - `.Should().BeTrue()`, `.Should().HaveCount()`, etc.
- **NSubstitute 5.3.0** - Mocking: `Substitute.For<IInterface>()`
- **coverlet.collector 6.0.4** - Code coverage

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
    ExporterTests.cs          Export orchestration (mocked IDatFileHandler)
    PatcherTests.cs           Patch orchestration (mocked IDatFileHandler + ITranslationParser)
    GameUpdateCheckerTests.cs Forum scraping + version comparison (mocked fetcher + store)
  Models/
    FragmentTests.cs          Binary parse/write roundtrip
    SubFileTests.cs           Serialization/deserialization
    TranslationTests.cs       Property validation
  Parsers/
    TranslationFileParserTests.cs  Line parsing, format validation, edge cases
```

## Integration Tests (LotroKoniecDev.Tests.Integration)

Reserved for tests that use real infrastructure (DAT files, database, HTTP).
Currently empty — will be populated in M2 (PostgreSQL + EF Core) and when real DAT file tests are needed.

## Conventions

- Test class naming: `{ClassUnderTest}Tests`
- Method naming: `MethodName_Scenario_ExpectedResult`
- One assertion concept per test (may have multiple `.Should()` calls)
- FluentAssertions style only, no raw `Assert.*`
- Shared test data builders go in `Shared/` directory
