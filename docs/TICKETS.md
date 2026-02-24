# LOTRO Polish Patcher — Backlog

> Przepisane po audycie PM. Oryginalne 74 tickety skonsolidowane do 41.
> Poprawione: falszywe zaleznosci, brakujace feature'y, overengineering, priorytetyzacja.
> Numeracja `M{milestone}-{numer}`. Testy wliczone w feature ticket (nie osobne).
>
> **Pre-M1 cleanup (zrobione):**
> - Mock-based ExporterTests/PatcherTests przeniesione z Integration do Unit
> - 8 zduplikowanych plikow testowych usuniete z Integration
> - `TestDataFactory` (shared binary SubFile builder) w `Tests.Unit/Shared/`
> - 16 unit testow GameUpdateChecker dodane (mock fetcher + store)
> - Integration project pusty — zarezerwowany na prawdziwe testy (DAT, DB)

---

## Legenda

| Label | Znaczenie |
|-------|-----------|
| `critical` | Blokuje inne tickety / milestone |
| `high` | Core feature milestonu |
| `medium` | Wazne ale nie blokujace |
| `low` | Nice-to-have, moze czekac |
| `bug` | Znany blad do naprawy |
| `refactor` | Poprawa jakosci bez zmiany zachowania |
| `infra` | Build, CI, Docker, konfiguracja |
| `feature` | Nowa funkcjonalnosc |

---

# M1: Porzadki CLI (MediatR + Launch + Update Fix)

## Faza A: MediatR setup

### M1-01: Dodaj MediatR do solution + OperationProgress
**Labels:** `high` `infra`
**Blokuje:** M1-02, M1-03, M1-04
**Zalezy od:** —

**UWAGA:** Ten ticket NIE wymaga TFM split (M1-06). MediatR dziala na `net10.0-windows` x86 bez problemow.

**Do zrobienia:**
1. Dodaj NuGet do `Directory.Packages.props`:
   - `MediatR` (najnowsza wersja — od v12 DI registration jest wbudowane, NIE potrzeba osobnego pakietu `MediatR.Extensions.Microsoft.DependencyInjection`)
2. Dodaj `PackageReference` w `Application.csproj`.
3. W `ApplicationDependencyInjection.AddApplicationServices()` dodaj:
   ```csharp
   services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ApplicationDependencyInjection).Assembly));
   ```
4. Stworz `OperationProgress` record w Application:
   ```csharp
   public sealed record OperationProgress(int Current, int Total, string? Message = null);
   ```
5. Stworz `ConsoleProgressReporter : IProgress<OperationProgress>` w CLI.
6. W `Program.cs` CLI — resolve `IMediator` po budowie kontenera (jeszcze nie uzywany).

**Acceptance criteria:**
- [ ] `dotnet build` przechodzi
- [ ] MediatR jest zarejestrowany w DI
- [ ] `OperationProgress` istnieje w Application
- [ ] `ConsoleProgressReporter` istnieje w CLI
- [ ] Testy przechodza bez zmian (brak breaking changes)

---

## Faza B: MediatR handlers

### M1-02: ExportTextsQuery + Handler + testy
**Labels:** `high` `feature`
**Zalezy od:** M1-01
**Blokuje:** M1-05a

**Do zrobienia:**
1. **Przenies `ExportSummary` record** z `IExporter.cs` (linia 26-29) do osobnego pliku `Application/Features/Export/ExportSummary.cs`. Przy kasowaniu IExporter w M1-05a stracilibysmy ten typ.
2. Stworz `Application/Features/Export/ExportTextsQuery.cs`:
   ```csharp
   public sealed record ExportTextsQuery(
       string DatFilePath,
       string OutputPath,
       IProgress<OperationProgress>? Progress = null
   ) : IRequest<Result<ExportSummary>>;
   ```
3. Stworz `ExportTextsQueryHandler : IRequestHandler<ExportTextsQuery, Result<ExportSummary>>`.
4. Handler uzywa `IDatFileHandler` (ten sam DI co Exporter).
5. Logika IDENTYCZNA z obecnym `Exporter.ExportAllTexts()` — kopiuj, nie wymyslaj.
6. Zaktualizuj istniejace `Tests.Unit/Tests/Features/ExporterTests.cs` (7 testow, uzywa `TestDataFactory`) — zmien na testowanie handlera zamiast klasy Exporter. Zachowaj scenariusze: happy path, DAT open fail, null args, non-text skip, progress callback.

**Acceptance criteria:**
- [ ] `ExportSummary` w osobnym pliku (NIE wewnatrz IExporter.cs)
- [ ] Handler zarejestrowany w MediatR (auto-discovery)
- [ ] `IMediator.Send(new ExportTextsQuery(...))` zwraca `Result<ExportSummary>`
- [ ] Istniejace ExporterTests zaktualizowane na handler (nie nowe testy od zera)
- [ ] Stary `Exporter` NADAL istnieje i dziala (jeszcze nie usuwamy)

---

### M1-03: ApplyPatchCommand + Handler + testy
**Labels:** `high` `feature`
**Zalezy od:** M1-01
**Blokuje:** M1-05b

**Do zrobienia:**
1. **Przenies `PatchSummary` record** z `IPatcher.cs` (linia 26-30) do osobnego pliku `Application/Features/Patch/PatchSummary.cs`.
2. Stworz `Application/Features/Patch/ApplyPatchCommand.cs`:
   ```csharp
   public sealed record ApplyPatchCommand(
       string TranslationsPath,
       string DatFilePath,
       IProgress<OperationProgress>? Progress = null
   ) : IRequest<Result<PatchSummary>>;
   ```
3. Stworz `ApplyPatchCommandHandler`.
4. Logika IDENTYCZNA z `Patcher.ApplyTranslations()`.
5. Zaktualizuj istniejace `Tests.Unit/Tests/Features/PatcherTests.cs` (11 testow, uzywa `TestDataFactory`) — zmien na testowanie handlera. Zachowaj scenariusze: happy path, no translations, parse error, DAT open fail, file not in DAT, non-text file, fragment not found, batch optimization.

**Acceptance criteria:**
- [ ] `PatchSummary` w osobnym pliku
- [ ] Handler dziala identycznie jak Patcher
- [ ] Istniejace PatcherTests zaktualizowane na handler
- [ ] Stary `Patcher` nadal istnieje

---

### M1-04: PreflightCheckQuery + Handler + testy
**Labels:** `medium` `feature`
**Zalezy od:** M1-01
**Blokuje:** M1-05b

**Stan obecny:**
`PreflightChecker` (CLI) miesza logike biznesowa z `Console.ReadLine()` i `Console.Write()`.

**Do zrobienia:**
1. Stworz `PreflightCheckQuery : IRequest<Result<PreflightReport>>`.
2. `PreflightReport` record: `bool IsGameRunning`, `bool HasWriteAccess`, `GameUpdateCheckResult? UpdateCheck`.
3. Handler TYLKO zbiera dane — zero Console I/O.
4. CLI czyta `PreflightReport` i decyduje co wyswietlic / o co zapytac usera.
5. Unit testy z mockami.

**Acceptance criteria:**
- [ ] Handler nie ma zadnych zaleznosci od Console/UI
- [ ] `PreflightReport` zawiera wszystkie dane potrzebne do decyzji
- [ ] CLI nadal pyta usera "Continue anyway?" na podstawie raportu
- [ ] Min. 3 unit testy

---

### M1-05a: Wire export na IMediator + usun stare export serwisy
**Labels:** `high` `refactor`
**Zalezy od:** M1-02
**Blokuje:** M1-05b

**Prostsza z dwoch podmian — ustala wzorzec dla M1-05b.**

**Do zrobienia:**
1. W `Program.cs`: `"export"` -> resolve datPath via `DatPathResolver`, potem `IMediator.Send(new ExportTextsQuery(datPath, outputPath, progress))`.
2. CLI czyta `Result<ExportSummaryResponse>`, drukuje summary (WriteSuccess/WriteError).
3. Usun `ExportCommand.cs` (static class w Commands/).
4. Usun `IExporter` interface + `Exporter` class — `ExportSummaryResponse` juz przeniesiony w M1-02.
5. Usun DI registration `AddScoped<IExporter, Exporter>`.
6. Zachowaj `DatPathResolver`, `ConsoleWriter`, `ConsoleProgressReporter` — to CLI-specific.

**Acceptance criteria:**
- [ ] `dotnet run -- export` dziala IDENTYCZNIE jak przed refaktorem
- [ ] Zero referencji do IExporter, Exporter, ExportCommand
- [ ] DI nie rejestruje IExporter
- [ ] Build + testy ok

---

### M1-05b: Wire patch na IMediator + usun stare patch/preflight serwisy
**Labels:** `high` `refactor`
**Zalezy od:** M1-03, M1-04, M1-05a
**Blokuje:** M1-08

**Bardziej zlozona podmiana — patch flow ma preflight + backup + restore on failure.**

**Do zrobienia:**
1. W `Program.cs`: `"patch"` flow:
   ```
   1. DatPathResolver.Resolve(args)
   2. IMediator.Send(new PreflightCheckQuery(datPath, versionPath))
   3. CLI czyta PreflightReport i SAMO decyduje:
      - IsGameRunning? → Console.ReadLine("Continue? y/N")
      - !HasWriteAccess? → WriteError, return
      - UpdateDetected? → Console.ReadLine("Continue? y/N")
   4. BackupManager.Create(datPath)
   5. IMediator.Send(new ApplyPatchCommand(translationsPath, datPath, progress))
   6. if failure → BackupManager.Restore(datPath)
   7. CLI drukuje summary
   ```
2. **BackupManager zostaje jako CLI utility** — backup/restore to operacja plikowa specyficzna dla CLI flow. Program.ps wywoluje BackupManager.Create() miedzy preflight a patch.
3. Usun `PatchCommand.cs` (static class w Commands/).
4. Usun `PreflightChecker.cs` (CLI implementacja w Commands/).
5. Usun `IPreflightChecker` interface w Application/Abstractions — handler ja zastepuje.
6. Usun `IPatcher` interface + `Patcher` class — `PatchSummaryResponse` juz przeniesiony w M1-03.
7. Usun DI registrations: `AddScoped<IPatcher, Patcher>`, `AddSingleton<IPreflightChecker, PreflightChecker>`.

**NIE usuwaj:**
- `BackupManager`, `DatPathResolver`, `ConsoleWriter` — CLI-specific, nadal uzywane
- `ExportSummaryResponse`, `PatchSummaryResponse` — juz w Features/
- `TranslationFileParser`, `ITranslationParser` — nadal uzywane przez handlery

**Acceptance criteria:**
- [ ] `dotnet run -- patch polish` dziala IDENTYCZNIE jak przed refaktorem
- [ ] CLI pyta "Continue anyway?" na podstawie PreflightReport (nie handler)
- [ ] Zero referencji do IPatcher, Patcher, PatchCommand, PreflightChecker, IPreflightChecker
- [ ] DI nie rejestruje starych serwisow
- [ ] Build + testy ok

---

## Faza C: Launch + Update Fix

### M1-06: Rozdziel TFM per-project
**Labels:** `critical` `infra`
**Blokuje:** M2-01, M3-01

**UWAGA:** Ten ticket blokuje TYLKO M2 i M3 (projekty AnyCPU). NIE blokuje M1-01..M1-05b ani M1-07..M1-10. MediatR, handlery i refaktor CLI dzialaja na `net10.0-windows` x86.

**Stan obecny:**
`Directory.Build.props` wymusza `net10.0-windows` + `x86` na WSZYSTKIE projekty.

**Do zrobienia:**
1. W `Directory.Build.props` zostaw TYLKO ustawienia wspolne (Nullable, LangVersion, AnalysisLevel, EnforceCodeStyleInBuild, ImplicitUsings). Usun `TargetFramework` i `PlatformTarget`.
2. W kazdym `.csproj` dodaj wlasciwy TFM:

| Projekt | TFM | Platform |
|---------|-----|----------|
| Primitives | `net10.0` | AnyCPU |
| Domain | `net10.0` | AnyCPU |
| Application | `net10.0` | AnyCPU |
| Infrastructure | `net10.0-windows` | x86 (datexport.dll) |
| CLI | `net10.0-windows` | x86 |
| Tests.Unit | `net10.0` | AnyCPU |
| Tests.Integration | `net10.0-windows` | x86 (referencja Infrastructure) |

3. Upewnij sie ze Infrastructure.csproj zachowa `AllowUnsafeBlocks`, kopie DLL-ek natywnych.
4. **Zaktualizuj NuGet**: `Microsoft.Extensions.DependencyInjection` z 9.0.0 na 10.0.x w `Directory.Packages.props` (powinien pasowac do TFM).

**Acceptance criteria:**
- [ ] `dotnet build` przechodzi
- [ ] `dotnet test` — wszystkie testy przechodza
- [ ] `Directory.Build.props` nie ma TFM ani PlatformTarget
- [ ] Primitives, Domain, Application = `net10.0` AnyCPU
- [ ] Infrastructure, CLI = `net10.0-windows` x86
- [ ] NuGet versions aligned z .NET 10

---

### M1-07: Launch infrastructure — IDatVersionReader + IDatFileProtector + IGameLauncher
**Labels:** `high` `feature`
**Zalezy od:** —
**Blokuje:** M1-08

**UWAGA:** Ten ticket nie ma zadnych zaleznosci — mozna go robic rownolegle z M1-01..M1-05b.

**Do zrobienia — TRZY male abstrakcje + implementacje:**

**1. IDatVersionReader** (Application/Abstractions):
```csharp
public interface IDatVersionReader
{
    Result<DatVersionInfo> ReadVersion(string datFilePath);
}
public sealed record DatVersionInfo(int VnumDatFile, int VnumGameData);
```
Implementacja w Infrastructure — otwiera DAT (`OpenDatFileEx2` z `OpenFlagsReadWrite=130`), czyta vnum z out parametrow, NATYCHMIAST zamyka. Wywolanie PRZED normalnym Open() — atomowe open/read/close, nie koliduje z pozniejszym handlerem.

**UWAGA:** NIE zakladaj read-only mode (flags=2) — datexport.dll to zamkniety Turbine binary, nie wiadomo czy jest obslugiwany.

**2. IDatFileProtector** (Application/Abstractions):
```csharp
public interface IDatFileProtector
{
    Result Protect(string datFilePath);
    Result Unprotect(string datFilePath);
    bool IsProtected(string datFilePath);
}
```
Implementacja: `File.SetAttributes()` z `FileAttributes.ReadOnly`. NIE uzywaj `attrib.exe` (Process.Start).

**3. IGameLauncher** (Application/Abstractions):
```csharp
public interface IGameLauncher
{
    Result<int> Launch(string lotroPath, bool waitForExit = true);
}
```
Implementacja: Auto-detect `TurbineLauncher.exe` wzgledem sciezki DAT. `Process.Start()` z `WaitForExit()`. NIE dodawaj flag `-disablePatch`.

**Testy** (w `Tests.Unit/Tests/Features/` lub `Tests.Unit/Tests/Infrastructure/`):
- IDatVersionReader: unit test z mock IDatFileHandler. Uzyj `TestDataFactory` z `Shared/` do tworzenia binary test data.
- IDatFileProtector: unit testy z temp plikami (Protect/Unprotect/IsProtected)
- IGameLauncher: unit test z mock (nie startuje prawdziwego procesu)

**Acceptance criteria:**
- [ ] Trzy interfejsy w Application/Abstractions
- [ ] Trzy implementacje w Infrastructure
- [ ] DI registration
- [ ] Min. 6 unit testow (2 per abstrakcja)
- [ ] Build + testy ok

---

### M1-08: Napraw GameUpdateChecker + LaunchGameCommand + CLI `launch`
**Labels:** `critical` `feature` `bug`
**Zalezy od:** M1-05b (CLI patch na MediatR), M1-07 (launch infrastructure)

**UWAGA SEKWENCJI:** Ten ticket zmienia zachowanie `GameUpdateChecker` — po zmianie `CheckForUpdateAsync()` NIE zapisuje wersji. Stary `PreflightChecker` polegal na auto-save. Dlatego M1-05b (refaktor patch na MediatR) MUSI byc zrobiony PRZED tym ticketem.

**Do zrobienia — TRZY czesci:**

**Czesc 1: Napraw GameUpdateChecker (bug)**

Stan obecny (GameUpdateChecker.cs:56-58):
```csharp
if (updateDetected)
{
    Result saveResult = _versionFileStore.SaveVersion(versionFilePath, currentVersion);
```
Problem: zapisuje wersje z forum OD RAZU, zanim user zainstalowal update.

Fix:
1. `CheckForUpdateAsync()` NIE zapisuje wersji — tylko raportuje.
2. Dodaj nowa metode `ConfirmUpdateInstalled()` ktora:
   - Czyta vnum z DAT (via `IDatVersionReader`)
   - Porownuje z poprzednim vnum
   - Jesli vnum sie zmienil -> zapisuje nowa wersje forum
3. Zmien `GameUpdateCheckResult` zeby zawieralo `DatVersionInfo`.
4. Zaktualizuj istniejace 16 testow w `Tests.Unit/Tests/Features/GameUpdateCheckerTests.cs` — testy aktualnie testuja stare zachowanie (auto-save). Po zmianie: testy "save on detect" -> failure, nowe testy dla `ConfirmUpdateInstalled()`.

**Czesc 2: LaunchGameCommand + Handler**

Stworz `LaunchGameCommand : IRequest<Result<LaunchReport>>`.
Handler orchestruje:
```
1. CheckForUpdate (forum)
2. Jesli update wykryty -> ReadVersion (DAT vnum) -> porownaj
3. Jesli wersje sie nie zgadzaja -> zwroc blad "zaktualizuj gre"
4. Protect DAT (attrib +R)
5. Launch gre
6. Czekaj na zamkniecie
7. Unprotect DAT (attrib -R)
```
`LaunchReport` record z detalami (wersja, czas gry, etc.)

**Czesc 3: CLI wiring**

1. Dodaj `"launch"` do switch w `Program.cs`.
2. Resolve sciezka LOTRO (DatPathResolver).
3. `IMediator.Send(new LaunchGameCommand(...))`.
4. Zaktualizuj `PrintUsage()`.

**Testy:**
- `GameUpdateCheckerTests` (istniejace 16 testow — zaktualizuj): wykrycie update -> brak zapisu; nowe testy dla `ConfirmUpdateInstalled()` z vnum
- `LaunchGameCommandHandlerTests` (nowe):
  - Happy path: brak update, launch ok
  - Update detected + stary vnum -> blokada
  - Update detected + nowy vnum -> launch ok
  - Protect fail -> error
  - Launch fail -> unprotect + error
  - DAT juz protected -> idempotent

**Acceptance criteria:**
- [ ] `CheckForUpdateAsync()` nigdy nie zapisuje wersji
- [ ] Wersja zapisywana tylko po potwierdzeniu przez vnum z DAT
- [ ] `dotnet run -- launch` startuje gre z ochrona DAT
- [ ] Update detection blokuje launch jesli wersje nie pasuja
- [ ] DAT chroniony PRZED i odchroniony PO grze
- [ ] `PrintUsage()` wyswietla komende launch
- [ ] Min. 10 test cases

---

## Faza D: Cleanup

### M1-11: E2E test infrastructure + export E2E
**Labels:** `high` `test` `infra`
**Zalezy od:** —
**Blokuje:** M1-12
**Rekomendacja:** Zrob PRZED M1-05 — safety net na refaktor.

**Kontekst:**
Obecne unit testy mockuja IDatFileHandler — nigdy nie dotykaja prawdziwego pliku DAT ani datexport.dll. Potrzeba prawdziwych testow E2E ktore odpalaja skompilowany exe przez `Process.Start` — identycznie jak uzytkownik w terminalu. Zero pomijanych warstw.

**Do zrobienia:**

**Czesc 1: Folder testdata/**
- Stworz `tests/LotroKoniecDev.Tests.Integration/testdata/`
- Dodaj do `.gitignore`: `tests/LotroKoniecDev.Tests.Integration/testdata/*.dat`
- Stworz `testdata/README.md` (committowany) z instrukcja: skopiuj `client_local_English.dat` tu
- Inny dev chce odpalic testy? Kopiuje swoj DAT do testdata/ i jazda.

**Czesc 2: Tests.Integration.csproj**
- Projekt NIE potrzebuje referencji do Infrastructure (nie laduje DLL — odpala exe jako proces)
- Zachowaj referencje do Application (TranslationFileParser do walidacji eksportu)
- TFM moze zostac `net10.0` AnyCPU (test runner nie laduje x86 DLL)

**Czesc 3: E2ETestFixture**
- xUnit Collection Fixture (`[CollectionDefinition("E2E")]`) + `IAsyncLifetime`
- Waliduje ze `testdata/client_local_English.dat` istnieje — jesli nie, SKIP wszystkie testy
- Lokalizuje zbudowany `LotroKoniecDev.exe` (sciezka wzgledem test output dir)
- Helper `RunCliAsync(string args)` — `Process.Start` z redirected stdout/stderr, zwraca (exitCode, stdout, stderr)
- Helper `CreateTempDatCopy()` — kopiuje DAT do temp dir (dla testow patch)
- Helper `CreateTempDir()` — temp directory na output pliki
- Cleanup: usuwa temp directories
- Sciezki do `translations/polish.txt` (wzgledne od solution root)

**Czesc 4: ExportE2ETests**
- `[Collection("E2E")]` — testy seryjnie
- `Export_RealDatFile_ExitCodeZero` — `LotroKoniecDev.exe export <datPath> <tempOutput>`, sprawdz exit code 0
- `Export_RealDatFile_ProducesValidFile` — plik istnieje, niepusty, header present
- `Export_RealDatFile_OutputMatchesExpectedFormat` — linie w formacie `file_id||gossip_id||text||args_order||args_id||approved`, FileIds z high byte 0x25
- `Export_RealDatFile_AllLinesParseableByTranslationFileParser` — eksportuj, potem parsuj real TranslationFileParser — zero bledow
- `Export_RealDatFile_ProducesThousandsOfFragments` — rozsadna liczba (>1000) fragmentow

**Acceptance criteria:**
- [ ] `dotnet test tests/LotroKoniecDev.Tests.Integration` odpala testy z prawdziwym DAT
- [ ] Bez pliku DAT w testdata/ — testy SKIP (nie FAIL)
- [ ] Testy odpalaja prawdziwy skompilowany exe przez Process.Start
- [ ] Export produkuje prawidlowy, parsowalny plik
- [ ] Min. 5 testow
- [ ] Istniejace unit testy nadal przechodza

---

### M1-12: Patch E2E + roundtrip (pelny pipeline)
**Labels:** `high` `test`
**Zalezy od:** M1-11
**Rekomendacja:** Zrob PRZED M1-05 — pelny safety net na refaktor.

**Do zrobienia:**

**Czesc 1: PatchE2ETests**
- `Patch_RealDatWithPolishTxt_ExitCodeZero` — kopiuj DAT do temp, `LotroKoniecDev.exe patch polish <tempDat>`, sprawdz exit code 0
- `Patch_RealDatWithPolishTxt_StdoutContainsApplied` — stdout zawiera informacje o zastosowanych tlumaczeniach

**Czesc 2: RoundtripE2ETests — pelny cykl dowodzacy ze patch zadziala**
- `Roundtrip_ExportPatchExport_PatchedTextsArePolish`:
  1. `export <originalDat> <before.txt>`
  2. Kopiuj DAT do temp
  3. `patch polish <tempDat>`
  4. `export <tempDat> <after.txt>`
  5. Parsuj oba pliki (TranslationFileParser)
  6. Znajdz gossip IDs z `polish.txt` w obu plikach
  7. `before.txt`: gossipId 218649169 = angielski tekst
  8. `after.txt`:  gossipId 218649169 = polski tekst z `polish.txt`
- `Roundtrip_ExportPatchExport_UnpatchedTextsUnchanged`:
  1. Ten sam roundtrip
  2. Gossip IDs ktore NIE sa w `polish.txt` — identyczne miedzy before i after

**Acceptance criteria:**
- [ ] Patch przez exe na prawdziwym DAT przechodzi bez bledow
- [ ] Roundtrip dowodzi ze patchowane teksty sa po polsku
- [ ] Roundtrip dowodzi ze niepatchowane teksty sa nienaruszone
- [ ] Min. 4 testy
- [ ] Testy na kopii DAT (oryginal nienaruszony)

---

### M1-09: Pipeline behaviors (LoggingBehavior + ValidationBehavior)
**Labels:** `low` `feature`
**Zalezy od:** M1-01

**UWAGA:** Nice-to-have. MediatR dziala bez pipeline behaviors. Zrob gdy masz czas.

**Do zrobienia:**
1. `LoggingPipelineBehavior<TRequest, TResponse> : IPipelineBehavior` — loguj request name, elapsed ms, success/failure. Uzyj `ILogger` (dodaj `Microsoft.Extensions.Logging` jesli brak).
2. `ValidationPipelineBehavior<TRequest, TResponse> : IPipelineBehavior` — waliduj request PRZED handlerem. Jesli `TResponse` jest `Result<T>`, zwroc `Result.Failure` dla nieprawidlowych requestow.
3. Przykladowy walidator dla `ApplyPatchCommand` (sprawdz czy sciezki nie puste).
4. Zarejestruj w DI jako open generics.
5. Unit testy: logging verify, validation reject.
6. Integration test: pelny pipeline request -> validation -> logging -> handler -> response.

**Acceptance criteria:**
- [ ] Kazdy `IMediator.Send()` jest automatycznie logowany
- [ ] Nieprawidlowe requesty zwracaja `Result.Failure` ZANIM handler sie wykona
- [ ] Min. 4 unit testy

---

### M1-10: ArgsOrder w patcherze + pole approved w modelu
**Labels:** `medium` `feature` `bug`
**Zalezy od:** M1-03

**Stan obecny — DVA problemy:**

**Problem 1: ArgsOrder NIE jest uzywany w patcherze.**
`Patcher.cs:131` robi TYLKO:
```csharp
fragment.Pieces = translation.GetPieces().ToList();
```
ArgsOrder jest parsowane przez `TranslationFileParser` i przechowywane w `Translation`, ale Patcher NIGDY go nie uzywa. ArgRefs w Fragment nie sa reorderowane. To jest brakujaca funkcjonalnosc, nie bug do weryfikacji.

**Problem 2: Pole `approved` NIE jest parsowane.**
`TranslationFileParser.ParseLine()` czyta `parts[0]` do `parts[4]`. Format ma 6 pol: `file_id||gossip_id||content||args_order||args_id||approved`. Pole `approved` (index 5) jest calkowicie ignorowane. Model `Translation` nie ma `IsApproved`.

**Do zrobienia:**
1. **ArgsOrder**: Dodaj logike reorderingu ArgRefs w handlerze (lub osobnej metodzie na Fragment):
   ```
   Jesli ArgsOrder = [2, 0, 1] (0-indexed, po konwersji z pliku)
   to ArgRefs powinny byc przelozone w tej kolejnosci
   ```
2. **Approved**: Dodaj `bool IsApproved` do `Translation` model. Zmien parser aby czytal `parts[5]` (jesli istnieje). CLI ignoruje wartosc (patchuje wszystko), ale wartosc jest zachowana dla M2 (DB).
3. Testy z przykladem: "arg2 arg0 arg1" z ArgsOrder=[2,0,1].

**Acceptance criteria:**
- [ ] ArgsOrder reorderuje ArgRefs w fragment
- [ ] `Translation.IsApproved` istnieje i jest parsowane z pliku
- [ ] CLI patchuje niezaleznie od `approved` (zachowanie bez zmian)
- [ ] Testy: ArgsOrder reorder + approved parsing
- [ ] Brak regression w istniejacych testach

---

### M1-13: Gitleaks pre-commit hook + CI + .gitignore hardening
**Labels:** `medium` `infra`
**Zalezy od:** —
**Blokuje:** —
**Rekomendacja:** Zrob PRZED M3/M5 (API + Auth w publicznym repo). Im wczesniej tym lepiej.

**Kontekst:**
API + AuthServer (M3, M5) beda w publicznym repo. Sekrety (connection strings, signing keys, client secrets) nie moga trafic do historii gita. GitGuardian/GitHub secret scanning wykrywaja po fakcie — ktos mogl zobaczyc secret zanim zostal usuniety. Potrzeba prewencji PRZED commitem.

**Do zrobienia:**

**Czesc 1: Pre-commit hook (gitleaks)**
1. Dodaj `.pre-commit-config.yaml` do repo root:
   ```yaml
   repos:
     - repo: https://github.com/gitleaks/gitleaks
       rev: v8.21.2
       hooks:
         - id: gitleaks
   ```
2. Dodaj `.gitleaks.toml` z custom rules jesli potrzeba (np. allowlist dla test data, false positives).
3. Zaktualizuj README/CONTRIBUTING z instrukcja: `pip install pre-commit && pre-commit install`.

**Czesc 2: CI safety net (GitHub Actions)**
1. Dodaj `.github/workflows/gitleaks.yml`:
   ```yaml
   name: gitleaks
   on: [pull_request, push]
   jobs:
     scan:
       runs-on: ubuntu-latest
       steps:
         - uses: actions/checkout@v4
           with:
             fetch-depth: 0
         - uses: gitleaks/gitleaks-action@v2
           env:
             GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
   ```
2. Odpalany na kazdym PR i push do main — PR z secretem nie przejdzie.

**Czesc 3: .gitignore hardening**
1. Upewnij sie ze `.gitignore` zawiera:
   - `*.env`, `.env.*`, `!.env.example`
   - `appsettings.*.local.json`
   - `**/secrets.json`
2. Dodaj `.env.example` z placeholder values (committowany).
3. W README: instrukcja uzycia `dotnet user-secrets` dla dev environment.

**Acceptance criteria:**
- [ ] `git commit` z secretem w staged files -> commit zablokowany (pre-commit hook)
- [ ] PR z secretem -> CI fails (GitHub Actions)
- [ ] `.gitignore` pokrywa typowe pliki z sekretami
- [ ] `.env.example` istnieje z placeholderami
- [ ] Dokumentacja setup dla nowych devow (pre-commit install)
- [ ] False positives w testach nie blokuja commita (allowlist w `.gitleaks.toml`)

---

# M2: Baza danych

## Faza A: PostgreSQL + EF Core

### M2-01: Docker + Infrastructure.Persistence + EF Core setup
**Labels:** `critical` `infra`
**Zalezy od:** M1-06 (TFM split — Persistence musi byc AnyCPU)

**Do zrobienia — TRZY czesci w jednym tickecie:**

**Czesc 1: docker-compose.yml**
```yaml
services:
  db:
    image: postgres:17
    ports:
      - "5432:5432"
    environment:
      POSTGRES_DB: lotro_translations
      POSTGRES_USER: lotro
      POSTGRES_PASSWORD: lotro_dev
    volumes:
      - pgdata:/var/lib/postgresql/data
volumes:
  pgdata:
```
Dodaj `.env.example` z credentialami.

**Czesc 2: Nowy projekt Infrastructure.Persistence**

Stworz `LotroKoniecDev.Infrastructure.Persistence` (`net10.0`, AnyCPU):
- EF Core + Npgsql
- AppDbContext
- Reference: Application

**UPROSZCZENIE vs oryginalne tickety:** Tylko DWA projekty Infrastructure (nie trzy):
- `LotroKoniecDev.Infrastructure` — zostaje jak jest (x86, datexport.dll + caly reszta)
- `LotroKoniecDev.Infrastructure.Persistence` — nowy (AnyCPU, EF Core)

Nie ma potrzeby rozbijania na .Common — ForumPageFetcher, VersionFileStore itp. zostaja w obecnym Infrastructure. WebApp NIE potrzebuje ForumPageFetcher (update checking to feature CLI/WPF).

**Referencje po zmianie:**
- CLI -> Infrastructure + Infrastructure.Persistence
- WebApp -> Infrastructure.Persistence (BEZ Infrastructure)
- WPF -> Infrastructure + Infrastructure.Persistence

**Czesc 3: EF Core NuGet**

Dodaj do `Directory.Packages.props`:
- `Microsoft.EntityFrameworkCore`
- `Npgsql.EntityFrameworkCore.PostgreSQL`
- `Microsoft.EntityFrameworkCore.Design` (tools)

Stworz `AppDbContext` w Infrastructure.Persistence. Connection string z env variable lub `appsettings.json`.

Zaktualizuj .slnx, DI registration (`AddPersistenceServices(connectionString)`).

**Acceptance criteria:**
- [ ] `docker-compose up -d` startuje PostgreSQL na localhost:5432
- [ ] `AppDbContext` kompiluje sie
- [ ] Connection string konfigurowalny
- [ ] `dotnet ef` tool dziala z projektem Persistence
- [ ] Build przechodzi, testy przechodza
- [ ] Dwa projekty Infrastructure: obecny (x86) + .Persistence (AnyCPU)

---

### M2-02: Entities + migracje + seed
**Labels:** `high` `feature`
**Zalezy od:** M2-01

**Do zrobienia:**

**Czesc 1: Entities** w `Infrastructure.Persistence/Entities/`:

1. **LanguageEntity**: Code (PK, `pl`/`en`), Name, IsActive
2. **ExportedTextEntity**: Id, FileId, GossipId (`long`/bigint), EnglishContent, ImportedAt. UNIQUE(FileId, GossipId).
3. **TranslationEntity**: Id, FileId, GossipId (`long`/bigint), LanguageCode (FK), Content, ArgsOrder (string), ArgsId, IsApproved, Notes, CreatedAt, UpdatedAt. UNIQUE(FileId, GossipId, LanguageCode).
4. **TranslationHistoryEntity**: Id, TranslationId (FK), OldContent, NewContent, ChangedAt.
5. **GlossaryTermEntity**: Id, EnglishTerm, PolishTerm, Notes, Category, CreatedAt. UNIQUE(EnglishTerm, Category).
6. **TextContextEntity**: Id, FileId, GossipId (`long`/bigint), ContextType, ParentName, ParentCategory, ParentLevel, NpcName, Region, SourceFile, ImportedAt. UNIQUE(FileId, GossipId, ContextType).
7. **DatVersionEntity**: Id, VnumDatFile, VnumGameData, ForumVersion, DetectedAt.

**WAZNE — GossipId typ `long` (bigint):**
W Domain `Translation.GossipId` jest `int`, ale `Fragment.FragmentId` jest `ulong` (8 bajtow). Konwersja `(ulong)GossipId` w `Translation.FragmentId` moze tracic dane. W DB entities uzyj `long` (bigint w PostgreSQL).

**Dwa modele Translation:**
- `Domain.Models.Translation` — init-only DTO dla DAT pipeline (brak DB deps)
- `Persistence.Entities.TranslationEntity` — EF entity (timestamps, FK)
- Mapping w repository

**Czesc 2: EF konfiguracja**
- Fluent API z unique constraints
- Indexes na (FileId, GossipId) gdzie potrzebne

**Czesc 3: Migracja + seed**
1. `dotnet ef migrations add InitialCreate`
2. Auto-migrate w dev: `if (env.IsDevelopment()) await dbContext.Database.MigrateAsync();`
3. Seed `pl` i `en` do Languages (uzyj `HasData()`)

**Acceptance criteria:**
- [ ] Wszystkie entities stworzone z GossipId jako `long`
- [ ] `docker-compose up` + app start -> schema stworzona
- [ ] Po migracji: Languages ma `pl` i `en`
- [ ] Seed jest idempotentny
- [ ] Build przechodzi

---

## Faza B: Repozytoria + Import

### M2-03: ExportedText repository + batch import handler
**Labels:** `high` `feature`
**Zalezy od:** M2-02

**Do zrobienia:**

**Czesc 1: IExportedTextRepository** (Application/Abstractions):
```csharp
public interface IExportedTextRepository
{
    Task<Result> UpsertBatchAsync(IEnumerable<ExportedText> texts);
    Task<Result<ExportedText?>> GetByIdsAsync(int fileId, long gossipId);
    Task<Result<int>> GetCountAsync();
}
```
Domain DTO: `ExportedText` record (FileId, GossipId, EnglishContent).
Implementacja w Persistence z EF Core. Upsert = INSERT ON CONFLICT UPDATE.

**Czesc 2: ImportExportedTextsCommand + Handler**
```csharp
public sealed record ImportExportedTextsCommand(string FilePath) : IRequest<Result<ImportSummary>>;
public sealed record ImportSummary(int Imported, int Updated, int Skipped);
```

Handler:
- Parsuj `exported.txt` uzywajac `TranslationFileParser` (format identyczny)
- **UWAGA SEMANTYCZNA:** Parser zwraca `List<Translation>` gdzie `Content` = angielski tekst zrodlowy. Mapping: `Translation.Content` -> `ExportedTextEntity.EnglishContent`. ArgsOrder/ArgsId/Approved ignorowane przy imporcie exported texts.
- Batch upsert do `ExportedTexts`

**Testy:**
- Batch upsert 100k+ rekordow w < 30s
- Duplicate (FileId, GossipId) -> update content
- Integration test z prawdziwa baza (TestContainers preferowane)

**Acceptance criteria:**
- [ ] Batch upsert dziala wydajnie
- [ ] Drugi import tego samego pliku -> updates, nie duplikaty
- [ ] Mapping Translation -> ExportedText jest jawny i przetestowany
- [ ] Min. 3 testy

---

### M2-04: Translation repository + CRUD handlers
**Labels:** `high` `feature`
**Zalezy od:** M2-02

**Do zrobienia:**

**Czesc 1: ITranslationRepository** (Application/Abstractions):
```csharp
public interface ITranslationRepository
{
    Task<Result> UpsertAsync(TranslationDto dto);
    Task<Result<TranslationDto?>> GetAsync(int fileId, long gossipId, string languageCode);
    Task<Result<IReadOnlyList<TranslationDto>>> GetAllForLanguageAsync(string languageCode);
    Task<Result<int>> GetCountAsync(string languageCode);
    Task<Result<int>> GetApprovedCountAsync(string languageCode);
}
```
`TranslationDto` = Application-level DTO. Auto-history: przy upsert z innym content -> dodaj `TranslationHistoryEntity`.

**Czesc 2: CRUD Commands/Queries**
1. `CreateTranslationCommand(FileId, GossipId, LanguageCode, Content, ArgsOrder?, Notes?)`
2. `UpdateTranslationCommand(Id, Content, ArgsOrder?, Notes?)`
3. `ApproveTranslationCommand(Id)`
4. `GetTranslationQuery(FileId, GossipId, LanguageCode)`
5. `ListTranslationsQuery(LanguageCode, Page, PageSize, Filter?)`

**Acceptance criteria:**
- [ ] Full CRUD + approve
- [ ] Paginacja dziala
- [ ] Filter po content / FileId
- [ ] Historia zmian przy kazdym upsert z innym content
- [ ] UNIQUE constraint (FileId, GossipId, LanguageCode) enforced
- [ ] Min. 5 testow

---

### M2-05: Export translations DB -> polish.txt
**Labels:** `high` `feature`
**Zalezy od:** M2-04

**Do zrobienia:**
1. `ExportTranslationsQuery(LanguageCode, OnlyApproved?)`:
   - Pobierz wszystkie tlumaczenia z DB
   - Sformatuj do `file_id||gossip_id||content||args_order||args_id||approved`
   - Posortuj po FileId, GossipId
   - Zapisz do pliku
2. Output kompatybilny z istniejacym `TranslationFileParser`.

**Acceptance criteria:**
- [ ] Wyeksportowany plik jest identyczny formatowo z recznym `polish.txt`
- [ ] Roundtrip: import -> export -> parse -> patch -> dziala
- [ ] Test: porownanie export z oryginalem

---

### M2-06: Migracja istniejacego polish.txt do bazy
**Labels:** `medium` `feature`
**Zalezy od:** M2-04

**Do zrobienia:**
1. Stworz komende `import-translations` w CLI:
   - Parsuj `translations/polish.txt`
   - Dla kazdej linii: `ITranslationRepository.UpsertAsync()` z `LanguageCode = "pl"`
2. Ustaw `IsApproved = true` dla wszystkich (juz przetlumaczone i przetestowane).

**Acceptance criteria:**
- [ ] Wszystkie linie z `polish.txt` sa w bazie
- [ ] `IsApproved = true`
- [ ] Duplikat import -> update, nie blad

---

### M2-07: Defensywne parsowanie separatora || w tresci
**Labels:** `medium` `bug`
**Zalezy od:** — (brak zaleznosci, mozna robic w dowolnym momencie)

**Stan obecny:**
`TranslationFileParser.ParseLine()` (linia 69): `line.Split([FieldSeparator], StringSplitOptions.None)`.
Format ma 6 pol: `file_id||gossip_id||content||args_order||args_id||approved`.
Jesli `content` zawiera `||` — parser widzi 7+ pol i bierze zly indeks dla args_order.

**Do zrobienia:**
Strategia: parsuj od lewej i prawej, srodek = content.
1. Split na `||`
2. Pierwsze 2 elementy = file_id, gossip_id
3. Ostatnie 3 elementy = args_order, args_id, approved
4. Wszystko pomiedzy = content (sklejone z powrotem `||`)
5. Przetestuj roundtrip: parse -> export -> parse -> identyczny wynik.

**Acceptance criteria:**
- [ ] Tresc z `||` jest poprawnie parsowana
- [ ] Roundtrip test: content z `||` -> export -> parse -> identyczny
- [ ] Istniejace testy TranslationFileParser nadal przechodza

---

## Faza C: LOTRO Companion + Glossary

### M2-08: LOTRO Companion XML parser + TextContexts import
**Labels:** `high` `feature`
**Zalezy od:** M2-02

**Kontekst:**
https://github.com/LotroCompanion/lotro-data zawiera XML z metadanymi:
- `quests.xml` (~574k linii) — questy z dialogami
- `deeds.xml` — deedy
- `NPCs.xml` — NPC
Format kluczowy: `key:{file_id}:{gossip_id}` — ID zgadzaja sie 1:1 z naszym exportem.

**Do zrobienia:**

**Czesc 1: ITextContextRepository** (Application/Abstractions):
```csharp
public interface ITextContextRepository
{
    Task<Result> UpsertBatchAsync(IEnumerable<TextContext> contexts);
    Task<Result<IReadOnlyList<TextContext>>> GetByIdsAsync(int fileId, long gossipId);
}
```

**Czesc 2: XML parsery**
- `QuestXmlParser`, `DeedXmlParser` — parsuj quests.xml, deeds.xml
- Wyciagnij: `key:{file_id}:{gossip_id}`, nazwa questa/deeda, region, level, NPC
- Uzyj `XmlReader` (streaming) — pliki sa DUZE

**Czesc 3: ImportContextCommand + Handler**
- `ImportContextCommand(string XmlDirectoryPath)` — skanuj katalog, parsuj, batch upsert
- Progress reporting

**Acceptance criteria:**
- [ ] Parsowanie quests.xml -> lista TextContext z poprawnymi FileId/GossipId
- [ ] Streaming (nie laduj calego XML do pamieci)
- [ ] Jeden (FileId, GossipId) moze miec wiele kontekstow (rozne ContextType)
- [ ] Duplikat import -> update
- [ ] Min. 3 unit testy z malym XML sample

---

### M2-09: Glossary + DatVersions
**Labels:** `medium` `feature`
**Zalezy od:** M2-02

**Do zrobienia:**

**Czesc 1: Glossary**
1. `IGlossaryRepository` w Application.
2. CRUD: `CreateTerm`, `UpdateTerm`, `DeleteTerm`, `SearchTerms(query)`, `ListTerms(category?)`.
3. Kategorie: ProperNouns, Locations, Items, Skills, UI, General.
4. Seed z ~20 podstawowych terminow Tolkienowskich (Moria, Shire = Hrabstwo, etc.)

**Czesc 2: DatVersions**
1. Entity + prosta metoda `RecordVersion(vnumDatFile, vnumGameData, forumVersion)`.
2. Query: `GetLatestVersion()`, `GetHistory(count)`.

**Acceptance criteria:**
- [ ] Glossary CRUD dziala
- [ ] Search po EN/PL terminie
- [ ] UNIQUE(EnglishTerm, Category)
- [ ] DatVersions rejestruje historie
- [ ] Min. 4 testy

---

### M2-10: Testy integracyjne — pelen pipeline M2
**Labels:** `high` `test`
**Zalezy od:** M2-03..M2-09

**UWAGA:** Projekt `Tests.Integration` jest przygotowany (pusty, z referencja do Infrastructure). Tutaj trafiaja prawdziwe testy integracyjne z baza danych (TestContainers + PostgreSQL).

**Do zrobienia:**
1. Dodaj TestContainers + PostgreSQL NuGet do `Tests.Integration.csproj` (wzor: TheKittySaver).
2. `AppFactory` / `IAsyncLifetime` fixture z prawdziwa baza w Dockerze.
3. Test: CLI export -> import to DB -> translate in DB -> export from DB -> CLI patch.
4. Test: import exported.txt + import Companion XML -> context jest widoczny.
5. Test: glossary CRUD.

**Acceptance criteria:**
- [ ] Pelen roundtrip przechodzi
- [ ] Kontekst z Companion jest polaczony z ExportedTexts
- [ ] TestContainers z prawdziwa baza PostgreSQL (nie in-memory)

---

## Faza D: Przygotowanie do M3

### M2-11: Rozdziel Application na Core + DatFile + Persistence
**Labels:** `high` `refactor`
**Zalezy od:** M2-04 (pierwsze handlery DB-owe istnieja)
**Blokuje:** M3-01 (Web referencuje tylko Application.Core + Application.Persistence)

**Kontekst:**
Obecne Application zawiera handlery dla dwoch roznych concerns:
- DAT-owe (ExportTextsQuery, ApplyPatchCommand, LaunchGameCommand) — uzywane przez CLI + WPF
- DB-owe (Translation CRUD, Import/Export) — uzywane przez Web

Web App nie potrzebuje `IDatFileHandler`, a CLI/WPF nie potrzebuja `ITranslationRepository`.
Jeden Application = naruszenie ISP + bledne wiring wykrywane w runtime zamiast compile-time.
Split daje symetrie z Infrastructure (Infrastructure + Infrastructure.Persistence).

**Do zrobienia:**

**1. Application.Core** (`net10.0`, AnyCPU):
- `ITranslationParser`, `TranslationFileParser`
- `OperationProgress`, `IOperationStatusReporter`
- Pipeline behaviors (Logging, Validation) — jesli istnieja po M1-09
- Shared abstractions i extensions
- Reference: Domain, Primitives

**2. Application.DatFile** (`net10.0`, AnyCPU):
- `ExportTextsQuery` + Handler
- `ApplyPatchCommand` + Handler
- `LaunchGameCommand` + Handler
- `IDatFileHandler`, `IDatFileLocator`, `IDatPathResolver`, `IDatVersionReader`, `IDatFileProtector`, `IGameLauncher`
- `IGameProcessDetector`, `IWriteAccessChecker`
- Reference: Application.Core

**3. Application.Persistence** (`net10.0`, AnyCPU):
- Translation CRUD handlers (z M2-04)
- `ImportExportedTextsCommand` + Handler (z M2-03)
- `ExportTranslationsQuery` + Handler (z M2-05)
- `ITranslationRepository`, `IExportedTextRepository`, `ITextContextRepository`, `IGlossaryRepository`
- Reference: Application.Core

**Referencje po zmianie:**

| Host | References |
|------|-----------|
| CLI | Application.Core + Application.DatFile |
| WPF | Application.Core + Application.DatFile |
| Web | Application.Core + Application.Persistence |
| Tests.Unit | Application.Core + Application.DatFile (+ .Persistence gdy DB testy) |

**Zaktualizuj:**
- .slnx — trzy projekty zamiast jednego
- DI registration per host (`AddDatFileApplicationServices()`, `AddPersistenceApplicationServices()`)
- Istniejace test references
- CLAUDE.md (architektura, project structure)

**Acceptance criteria:**
- [ ] Trzy projekty Application: Core, DatFile, Persistence
- [ ] Web App NIE referencuje Application.DatFile
- [ ] CLI/WPF NIE referencuje Application.Persistence
- [ ] Bledne wiring = compile error, nie runtime error
- [ ] Build + testy ok
- [ ] Istniejace testy przechodza bez zmian

---

# M3: API + Aplikacja webowa (Blazor SSR)

**Architektura:** Trzy osobne hosty — API (centralny backend), Blazor SSR (UI dla tlumaczen), AuthServer (M5).
Blazor NIE laczy sie z baza bezposrednio — wszystko przez API via `IApiClient` (typed HttpClient).

## Faza A: Backend API

### M3-01: Projekt API — Minimal API + Translation endpoints
**Labels:** `high` `infra`
**Zalezy od:** M1-06 (TFM split), M2-01 (Persistence), M2-11 (Application split)
**Blokuje:** M3-02, M3-05

**Kontekst:**
API to centralny backend dla WSZYSTKICH konsumentow: Blazor WebApp, CLI (download polish.txt), WPF (download polish.txt), przyszli klienci zewnetrzni.

**Do zrobienia:**
1. `dotnet new webapi -n LotroKoniecDev.Api --use-minimal-apis`
2. TFM: `net10.0`, AnyCPU.
3. Reference: Application.Core, Application.Persistence, Infrastructure.Persistence.
4. Dodaj do .slnx.
5. DI: MediatR + EF Core — `AddCoreApplicationServices()`, `AddPersistenceApplicationServices()`, `AddPersistenceServices(connectionString)`.
6. Health check (`/health`).
7. Swagger/OpenAPI (`/swagger`).
8. Translation CRUD endpoints (deleguja do MediatR handlers z M2-04):
   - `GET /api/v1/translations?lang=pl&page=1&pageSize=25&filter=...`
   - `GET /api/v1/translations/{id}`
   - `POST /api/v1/translations`
   - `PUT /api/v1/translations/{id}`
   - `POST /api/v1/translations/{id}/approve`
9. CORS policy dla Blazor WebApp origin.
10. Auto-migrate w Development.

**Acceptance criteria:**
- [ ] `dotnet run --project src/LotroKoniecDev.Api` startuje na localhost:5100
- [ ] Swagger UI widoczny na /swagger
- [ ] CRUD endpoints odpowiadaja JSON
- [ ] Health check zwraca 200
- [ ] Brak referencji do Application.DatFile, Infrastructure (x86)
- [ ] CORS umozliwia wywolania z WebApp

---

## Faza B: Blazor SSR

### M3-02: Projekt Blazor SSR + DI + layout
**Labels:** `high` `infra`
**Zalezy od:** M3-01 (API musi istniec)
**Blokuje:** M3-03, M3-04, M3-06, M3-07, M3-08

**Do zrobienia:**
1. `dotnet new blazor -n LotroKoniecDev.WebApp --interactivity Server`
2. TFM: `net10.0`, AnyCPU.
3. Reference: Application.Core (TYLKO dla DTO/kontraktow — response records). BEZ Application.Persistence, BEZ Infrastructure.
4. Dodaj do .slnx.
5. `IApiClient` interface + implementacja — typed HttpClient wrapper do komunikacji z API.
6. DI: `builder.Services.AddHttpClient<IApiClient, ApiClient>(...)`.
7. Bootstrap layout (sidebar + main content).
8. Nawigacja: Translations, Quests, Glossary, Import/Export, Dashboard.
9. Polish UI text.
10. Konfiguracja URL API w `appsettings.json` (`ApiBaseUrl`).

**Acceptance criteria:**
- [ ] `dotnet run --project src/LotroKoniecDev.WebApp` startuje na localhost:5000
- [ ] Layout z nawigacja widoczny
- [ ] Brak referencji do Application.Persistence, Application.DatFile, Infrastructure
- [ ] `IApiClient` zarejestrowany w DI
- [ ] Wywolanie health check API z WebApp dziala

---

## Faza C: Funkcjonalnosci

### M3-03: Lista tlumaczen (tabela, filtruj, paginacja)
**Labels:** `high` `feature`
**Zalezy od:** M3-02
**Blokuje:** M3-04

**Do zrobienia:**
1. Strona `/translations` — tabela z kolumnami: FileId, GossipId, English, Polish, Status, Context.
2. Filtrowanie: po tekscie, po statusie (approved/not), po ContextType.
3. Paginacja (25/50/100 per page).
4. Sortowanie po kolumnach.
5. Context z TextContexts (jesli dostepny): nazwa questa, NPC, region.
6. Wszystkie dane przez `IApiClient` → `GET /api/v1/translations?...`.

**Acceptance criteria:**
- [ ] Tabela wyswietla tlumaczenia z API
- [ ] Filtrowanie po tekscie dziala
- [ ] Paginacja dziala
- [ ] Kontekst widoczny (jezeli zaimportowany)

---

### M3-04: Edytor tlumaczen (side-by-side EN/PL + kontekst + placeholdery)
**Labels:** `high` `feature`
**Zalezy od:** M3-03

**Do zrobienia:**
1. Strona `/translations/{id}/edit` lub modal.
2. Lewy panel: angielski tekst (read-only).
3. Prawy panel: polski tekst (edytowalny textarea).
4. Panel kontekstu: quest name, NPC, region, level (z TextContexts via API).
5. Podswietlenie `<--DO_NOT_TOUCH!-->` na czerwono (CSS/regex highlight).
6. Walidacja przy save: liczba placeholderow w PL == liczba w EN. Ostrzezenie jesli niezgodnosc.
7. Save → `IApiClient` → `PUT /api/v1/translations/{id}`.

**Acceptance criteria:**
- [ ] Side-by-side widok
- [ ] Edycja i zapis przez API dziala
- [ ] Placeholdery podswietlone
- [ ] Walidacja placeholderow: rozna liczba -> warning
- [ ] Kontekst widoczny

---

### M3-05: API — Import/Export endpoints
**Labels:** `medium` `feature`
**Zalezy od:** M3-01, M2-03, M2-05

**Do zrobienia:**
1. `POST /api/v1/import/exported-texts` — upload `exported.txt`, import do bazy via MediatR handler (M2-03).
2. `GET /api/v1/translations/export?lang=pl&onlyApproved=false` — download `polish.txt` via handler (M2-05).
3. Endpointy w projekcie Api.
4. Walidacja pliku (rozmiar, format).
5. Streaming response dla duzych eksportow.
6. Uzywane przez: Blazor (upload/download UI), CLI (download polish.txt), WPF (download polish.txt).

**Acceptance criteria:**
- [ ] Upload exported.txt przez API → import do DB
- [ ] Download polish.txt → kompatybilny z CLI patch
- [ ] Error handling (zly format, pusty plik)
- [ ] Streaming dla duzych plikow

---

### M3-06: Dashboard — statystyki
**Labels:** `medium` `feature`
**Zalezy od:** M3-02

**Do zrobienia:**
1. API endpoints w projekcie Api:
   - `GET /api/v1/stats` — total, translated, approved, untranslated, per ContextType
   - `GET /api/v1/translations/recent?count=10`
2. Strona `/dashboard` w WebApp:
   - Progress bar (%)
   - Ostatnie edycje
   - Stats per ContextType (quests: 40%, deeds: 20%, etc.)
3. Dane przez `IApiClient`.

**Acceptance criteria:**
- [ ] Statystyki sa poprawne
- [ ] Progress bar widoczny

---

### M3-07: Quest browser + Glossary UI
**Labels:** `medium` `feature`
**Zalezy od:** M3-02, M2-08 (kontekst), M2-09 (glossary)

**Do zrobienia:**
1. API endpoints w projekcie Api:
   - `GET /api/v1/quests?region=...&level=...`
   - `GET /api/v1/quests/{id}/strings`
   - `GET /api/v1/glossary?search=...&category=...`
   - `POST/PUT/DELETE /api/v1/glossary`
2. Strona `/quests` w WebApp — lista questow, klik -> stringi, grupowanie po region/level/NPC.
3. Strona `/glossary` w WebApp — lista terminow, dodawanie/edycja, szukanie, kategorie.
4. Dane przez `IApiClient`.

**Acceptance criteria:**
- [ ] Questy widoczne z pogrupowanymi stringami
- [ ] Nawigacja quest -> stringi -> edycja
- [ ] Glossary CRUD w UI
- [ ] Szukanie po EN/PL

---

### M3-08: UX — keyboard shortcuts + bulk operations
**Labels:** `low` `feature`
**Zalezy od:** M3-04

**Do zrobienia:**
1. `Ctrl+S` — save, `Ctrl+Enter` — save + next, `Ctrl+Shift+Enter` — approve + next.
2. JS interop dla keyboard events.
3. Checkbox na kazdym wierszu tabeli, "Approve selected" button.
4. Bulk approve → `POST /api/v1/translations/bulk-approve` (endpoint w Api).

**Acceptance criteria:**
- [ ] Skroty dzialaja w edytorze
- [ ] Bulk approve 50 stringow -> wszystkie approved

---

## Faza D: DevOps

### M3-09: Docker + style guide + testy
**Labels:** `medium` `infra` `test`
**Zalezy od:** M3-03, M3-04

**Do zrobienia:**
1. `Dockerfile` dla Api. `Dockerfile` dla WebApp. Dodaj do `docker-compose.yml`.
2. `docker-compose up` uruchamia: PostgreSQL + Api + WebApp (3 serwisy).
3. Statyczna strona `/style-guide` w WebApp z zasadami tlumaczenia (markdown -> HTML).
4. API endpoint tests (WebApplicationFactory).
5. Blazor component tests (bUnit) dla kluczowych komponentow.

**Acceptance criteria:**
- [ ] `docker-compose up` startuje PostgreSQL + Api + WebApp
- [ ] Api dostepny na :5100, WebApp na :5000
- [ ] Style guide dostepny
- [ ] Kluczowe flows przetestowane

---

# M4: Desktop App (LotroPoPolsku.exe)

### M4-01: Projekt WPF + DI + glowne okno
**Labels:** `high` `infra`
**Zalezy od:** M1-03 (ApplyPatchCommand), M1-08 (LaunchGameCommand)

**Do zrobienia:**
1. `dotnet new wpf -n LotroKoniecDev.DesktopApp`
2. TFM: `net10.0-windows`, x86.
3. Reference: Application.Core, Application.DatFile, Infrastructure.
4. DI: MediatR, ten sam co CLI.
5. Dodaj do .slnx.
6. Ikona aplikacji (pierscien/tolkienowski motyw) — .ico.
7. Splash screen przy starcie.
8. Glowne okno layout:
   ```
   ┌────────────────────────────────┐
   │  LOTRO po Polsku    [_][X]    │
   ├────────────────────────────────┤
   │  Status: Gotowe do gry        │
   │  Wersja gry: 42.1             │
   │  Wersja patcha: 2024-01-15    │
   │                                │
   │  [ Patchuj ]    [ Graj ]      │
   │                                │
   │  ████████░░░░░░░ 60%          │
   └────────────────────────────────┘
   ```
9. MVVM ViewModel binding.

**Acceptance criteria:**
- [ ] Projekt kompiluje sie
- [ ] Okno WPF sie otwiera z layoutem
- [ ] DI dziala
- [ ] Ikona widoczna

---

### M4-02: Przyciski Patchuj + Graj
**Labels:** `high` `feature`
**Zalezy od:** M4-01

**Do zrobienia:**

**Przycisk "Patchuj":**
1. Click -> wczytaj `polish.txt` z LOKALNEGO PLIKU (sciezka w ustawieniach).
2. `IMediator.Send(new ApplyPatchCommand(filePath, datPath))`.
3. Progress bar + status text.
4. Disable przycisk podczas patchowania.

**UWAGA:** Wersja MVP uzywa LOKALNEGO pliku (jak CLI). Pobieranie z API (`GET /api/v1/translations/export`) to enhancement na pozniej (po M3-05).

**Przycisk "Graj":**
1. Click -> `IMediator.Send(new LaunchGameCommand(...))`.
2. Protect DAT -> launch -> wait -> unprotect.
3. Status: "Gra uruchomiona..." -> "Gotowe".

**Acceptance criteria:**
- [ ] Patchowanie z lokalnego pliku dziala
- [ ] Launch gry z ochrona DAT
- [ ] Progress bar animowany
- [ ] Status updating

---

### M4-03: Auto-detekcja LOTRO + ustawienia
**Labels:** `high` `feature`
**Zalezy od:** M4-01

**Do zrobienia:**
1. Uzyj `IDatFileLocator` (juz istnieje) do auto-detect LOTRO path.
2. Jesli nie znalezione: dialog "Wybierz folder LOTRO".
3. Okno/panel ustawien:
   - Sciezka LOTRO (browse dialog)
   - Sciezka do polish.txt (browse dialog)
   - Jezyk tlumaczenia (dropdown, default: Polish)
   - Auto-patch on launch (checkbox)
   - URL API (default: pusty — dopiero po M3-05)
4. Zapis do `%AppData%/LotroPoPolsku/config.json`.

**Acceptance criteria:**
- [ ] Przy pierwszym starcie: auto-detect lub dialog
- [ ] Ustawienia edytowalne i persystowane
- [ ] Zmiana sciezki -> walidacja (czy istnieje DAT)

---

### M4-04: Game update alert
**Labels:** `high` `feature`
**Zalezy od:** M4-01, M1-08

**Do zrobienia:**
1. Banner na gorze okna: "Wykryto aktualizacje gry! Zaktualizuj gre przed patchowaniem."
2. Przycisk "Graj" zablokowany dopoki wersje nie pasuja.
3. Instrukcja: "Uruchom oficjalny launcher, zaktualizuj, wroc tutaj."

**Acceptance criteria:**
- [ ] Banner widoczny przy mismatch wersji
- [ ] Przycisk "Graj" disabled
- [ ] Po aktualizacji: banner znika

---

### M4-05: Auto-update apki + installer
**Labels:** `medium` `feature` `infra`
**Zalezy od:** M4-01

**Do zrobienia:**
1. Sprawdz GitHub Releases API przy starcie.
2. Jesli nowa wersja -> dialog "Dostepna aktualizacja X.Y. Zaktualizowac?"
3. Pobranie + zastapienie exe.
4. Installer (MSIX lub Inno Setup): ikona na pulpicie, Start Menu entry, uninstaller.

**Acceptance criteria:**
- [ ] Detekcja nowej wersji na GitHub
- [ ] Download + replace dziala
- [ ] Installer tworzy skrot na pulpicie
- [ ] Deinstalacja czysta

---

### M4-06: Testy WPF
**Labels:** `high` `test`
**Zalezy od:** M4-02

**Do zrobienia:**
1. ViewModel unit testy (NSubstitute dla IMediator).
2. Test: Patchuj flow (mock patch).
3. Test: Launch flow.

**Acceptance criteria:**
- [ ] ViewModele przetestowane
- [ ] Kluczowe flows pokryte

---

# M5: Auth & Community

**Architektura:** Osobny host `LotroKoniecDev.AuthServer` (OpenIddict).
Wydaje JWT tokeny, zarzadza uzytkownikami i rolami. API waliduje tokeny, Blazor loguje przez redirect.

## Faza A: Auth Server

### M5-01: OpenIddict Auth Server project
**Labels:** `high` `infra`
**Zalezy od:** M2-01 (Persistence — user/identity tables w shared DbContext)
**Blokuje:** M5-02, M5-03

**Do zrobienia:**
1. `dotnet new web -n LotroKoniecDev.AuthServer`
2. TFM: `net10.0`, AnyCPU.
3. NuGet: `OpenIddict` (server components), `Microsoft.AspNetCore.Identity.EntityFrameworkCore`.
4. Reference: Infrastructure.Persistence (shared AppDbContext rozszerzony o Identity tables).
5. Entities: `ApplicationUser : IdentityUser`, `ApplicationRole : IdentityRole`.
6. Migracja: Identity tables + OpenIddict tables (clients, tokens, scopes).
7. OpenIddict konfiguracja:
   - Authorization code flow + PKCE (dla Blazor WebApp)
   - Client credentials flow (dla CLI/WPF)
   - Token endpoint (`/connect/token`)
   - Authorization endpoint (`/connect/authorize`)
   - Userinfo endpoint (`/connect/userinfo`)
8. Seed: admin user + client registrations (WebApp, Api, CLI, WPF).
9. Login/Register/Logout Razor Pages (minimalne UI).
10. Dockerfile + wpis w docker-compose.

**Acceptance criteria:**
- [ ] Auth Server startuje na localhost:5200
- [ ] Token endpoint zwraca JWT
- [ ] Login page dziala
- [ ] Seed tworzy admin user + 4 client registrations
- [ ] `docker-compose up` startuje PostgreSQL + Api + WebApp + AuthServer

---

## Faza B: Integracja auth

### M5-02: API auth integration (JWT + [Authorize])
**Labels:** `high` `feature`
**Zalezy od:** M5-01, M3-01
**Blokuje:** M5-04, M5-05

**Do zrobienia:**
1. Dodaj OpenIddict validation do Api projektu.
2. JWT Bearer authentication z Auth Server jako issuer.
3. `[Authorize]` / `.RequireAuthorization()` na endpointach wymagajacych autentykacji.
4. Polityki autoryzacji: `Translator`, `Reviewer`, `Admin`.
5. Anonimowy dostep do: `/health`, `/swagger`, `GET /api/v1/translations/export` (CLI/WPF bez logowania).
6. Wymagana autentykacja: CRUD, import, approve, bulk operations.

**Acceptance criteria:**
- [ ] Nieautoryzowane requesty do chronionych endpointow → 401
- [ ] Poprawny JWT → dostep
- [ ] Role-based access dziala (Translator moze edytowac, Reviewer moze approve)
- [ ] Export endpoint publiczny (CLI/WPF pobieraja bez logowania)
- [ ] Swagger pokazuje wymagania auth

---

### M5-03: Blazor auth integration (login, session)
**Labels:** `high` `feature`
**Zalezy od:** M5-01, M3-02

**Do zrobienia:**
1. OpenIddict client w WebApp (authorization code flow + PKCE).
2. Login button w navbar → redirect do Auth Server → callback → cookie session.
3. `IApiClient` dodaje JWT Bearer token do requestow.
4. `[Authorize]` na stronach wymagajacych logowania (edycja, approve).
5. Strony read-only (lista, dashboard) dostepne bez logowania.
6. User info w navbar (nazwa uzytkownika, rola).
7. Logout → clear session + redirect do Auth Server logout.

**Acceptance criteria:**
- [ ] Login flow: WebApp → AuthServer → callback → zalogowany
- [ ] Session persystowana (cookie)
- [ ] Chronione strony wymagaja logowania
- [ ] IApiClient wysyla token
- [ ] User info widoczny w navbar
- [ ] Logout dziala

---

## Faza C: Role i workflow

### M5-04: UserLanguageRoles — role per jezyk
**Labels:** `low` `feature`
**Zalezy od:** M5-02

**Do zrobienia:**
1. Entity `UserLanguageRole`: UserId, LanguageCode, Role (Translator/Reviewer).
2. Admin UI: przypisywanie rol per jezyk.
3. API endpoint: `GET/POST /api/v1/admin/roles`.
4. Autoryzacja: edycja tlumaczenia PL wymaga roli Translator dla PL.

---

### M5-05: Review workflow — submit → review → approve/reject
**Labels:** `low` `feature`
**Zalezy od:** M5-02

**Do zrobienia:**
1. Status flow: Draft → Submitted → Approved/Rejected.
2. Translator submituje, Reviewer zatwierdza lub odrzuca z komentarzem.
3. API endpoints: `POST /api/v1/translations/{id}/submit`, `POST /api/v1/translations/{id}/review`.
4. UI: lista do review, approve/reject buttons.

---

### M5-06: TranslationHistory z ChangedBy
**Labels:** `low` `feature`
**Zalezy od:** M5-02

**Do zrobienia:**
1. `TranslationHistory.ChangedByUserId` — FK do ApplicationUser.
2. Automatyczne wypelnianie z JWT claim.
3. UI: historia zmian z nazwa uzytkownika.

---

## Faza D: Extras

### M5-07: AI review — LLM sprawdza placeholders, grammar, terminologie
**Labels:** `low` `feature`
**Zalezy od:** M5-05

---

### M5-08: Powiadomienia — Discord webhook
**Labels:** `low` `feature`
**Zalezy od:** M5-05

---

### M5-09: Public REST API — dokumentacja + versioning
**Labels:** `low` `feature`
**Zalezy od:** M5-02

**Do zrobienia:**
1. API versioning (`/api/v1/`, `/api/v2/`).
2. OpenAPI spec generowana automatycznie.
3. Rate limiting dla publicznych endpointow.
4. API key authentication dla external clients.
5. Dokumentacja endpointow.

---

# Podsumowanie

## Statystyki

| Milestone | Tickety | Critical | High | Medium | Low |
|-----------|---------|----------|------|--------|-----|
| M1 | 14 | 2 | 8 | 3 | 1 |
| M2 | 11 | 1 | 7 | 3 | 0 |
| M3 | 9 | 0 | 4 | 4 | 1 |
| M4 | 6 | 0 | 5 | 1 | 0 |
| M5 | 9 | 0 | 3 | 0 | 6 |
| **Total** | **49** | **3** | **27** | **11** | **8** |

## Poprawiony critical path

```
=== ROWNOLEGLE SCIEZKI (od startu!) ===

Track A: MediatR + CLI refaktor (BEZ CZEKANIA na TFM split)
  M1-01 (MediatR setup)
    ├── M1-02 (ExportTextsQuery) ──→ M1-05a (wire export + usun IExporter)
    ├── M1-03 (ApplyPatchCommand) ─┐
    └── M1-04 (PreflightCheckQuery)┼→ M1-05b (wire patch + usun IPatcher/PreflightChecker)
                          M1-05a ──┘

Track B: Launch infrastructure (BRAK ZALEZNOSCI — start od razu)
  M1-07 (IDatVersionReader + IDatFileProtector + IGameLauncher)
    └── M1-05b (Track A) + M1-07 ──→ M1-08 (fix GameUpdateChecker + LaunchGameCommand)

Track C: TFM split → M2 → M3 (API + Blazor)
  M1-06 (TFM per-project)
    ├── M2-01 (Docker + Persistence) ──→ M2-02..M2-10
    │                                      └── M2-04 ──→ M2-11 (split Application) ──┐
    └── M3-01 (API) ←─────────────────────────────────────────────────────────────────┘
          └── M3-02 (Blazor SSR) ──→ M3-03..M3-08

Track D: Testy integracyjne (PRZED M1-05!)
  M1-11 (integration test infra + DatFileHandler) ──→ M1-12 (Exporter + Patcher pipeline)

Track E: Niezalezne (dowolna kolejnosc)
  M1-09 (pipeline behaviors — low priority)
  M1-10 (ArgsOrder + approved — medium)
  M1-13 (gitleaks + CI + .gitignore — PRZED M3/M5!)
  M2-07 (parser || fix — brak zaleznosci)

Track F: Auth (po M2 + M3)
  M5-01 (Auth Server) ← M2-01
    ├── M5-02 (API auth) ← M3-01 ──→ M5-04, M5-05
    └── M5-03 (Blazor auth) ← M3-02
```

## Stan testow (po pre-M1 cleanup)

```
Tests.Unit (156 testow):
  Shared/TestDataFactory.cs          Binary SubFile builder (wspoldzielony)
  Tests/Core/                        Error, ValueObject, Result, VarLenEncoder
  Tests/Extensions/                  ResultExtensions (Map, Bind, Match, Combine)
  Tests/Features/                    ExporterTests, PatcherTests, GameUpdateCheckerTests
  Tests/Models/                      Fragment, SubFile, Translation
  Tests/Parsers/                     TranslationFileParser

Tests.Integration (0 testow — M1-11/M1-12 + M2-10):
  M1-11: DatFileHandler z prawdziwym DAT + datexport.dll
  M1-12: Exporter/Patcher pelny pipeline z prawdziwym DAT
  M2-10: TestContainers + PostgreSQL
```

## Kluczowe poprawki vs oryginal

| Problem w oryginale | Poprawka |
|---------------------|----------|
| M1-01 (TFM) blokuje WSZYSTKO w M1 | TFM blokuje TYLKO M2/M3. MediatR idzie od razu |
| 74 tickety | Skonsolidowane do 45 |
| 7 osobnych ticket testowych | Testy wliczone w feature tickety |
| M1-17 + M1-18 duplikaty | Scalone w jeden M1-08 |
| M1-21 "zweryfikuj approved" | Poprawione: "DODAJ parsowanie approved" (nie jest parsowane!) |
| M1-20 "zweryfikuj ArgsOrder" | Poprawione: "DODAJ ArgsOrder do patchera" (nie jest uzywany!) |
| M2-02 trzy projekty Infrastructure | Uproszczone do dwoch (obecny + Persistence) |
| M4-04 zalezy od M3-08 (web API) | Usunieto: WPF uzywa lokalnych plikow MVP |
| Brak ticketu na NuGet versions | Wliczone w M1-06 (TFM split) |
| M1-07 i M1-08 osobne tickety | Scalone w jeden M1-09 (low priority) |
