# LOTRO Polish Patcher — Backlog

> Wygenerowane na podstawie: `PROJECT_PLAN.md`, `RUSSIAN_PROJECT_RESEARCH.md`, `CLAUDE.md`, analiza kodu.
> Numeracja `M{milestone}-{numer}`. Labele: `critical`, `high`, `medium`, `low`.
> Kazdy ticket: co, dlaczego, acceptance criteria, kontekst techniczny.

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
| `test` | Testy |

---

# M1: Porzadki CLI (MediatR + Launch + Update Fix)

## Faza A: Fundament

### M1-01: Rozdziel TFM per-project (usun globalny net10.0-windows/x86)
**Labels:** `critical` `infra`
**Blokuje:** M1-02..M1-21, M2-01..M2-22, M3-01, M4-01

**Stan obecny:**
`Directory.Build.props` wymusza `net10.0-windows` + `x86` na WSZYSTKIE projekty:
```xml
<TargetFramework>net10.0-windows</TargetFramework>
<PlatformTarget>x86</PlatformTarget>
```
Przez to nie da sie dodac Blazor (AnyCPU) ani EF Core/PostgreSQL (AnyCPU).

**Do zrobienia:**
1. W `Directory.Build.props` zostaw TYLKO ustawienia wspolne (Nullable, LangVersion, AnalysisLevel, EnforceCodeStyleInBuild). Usun `TargetFramework` i `PlatformTarget`.
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

**Acceptance criteria:**
- [ ] `dotnet build` przechodzi
- [ ] `dotnet test` — wszystkie testy przechodza
- [ ] `Directory.Build.props` nie ma TFM ani PlatformTarget
- [ ] Primitives, Domain, Application = `net10.0` AnyCPU
- [ ] Infrastructure, CLI = `net10.0-windows` x86

**Uwagi:**
- Testy integracyjne referencja Infrastructure (P/Invoke) — musza byc `net10.0-windows` x86.
- Testy unitowe referencja tylko Application/Domain — moga byc `net10.0` AnyCPU.

---

### M1-02: Dodaj MediatR do solution
**Labels:** `high` `infra`
**Blokuje:** M1-04..M1-12
**Zalezy od:** M1-01

**Do zrobienia:**
1. Dodaj NuGet do `Directory.Packages.props`:
   - `MediatR` (najnowsza wersja)
2. Dodaj `PackageReference` w `Application.csproj`.
3. W `ApplicationDependencyInjection.AddApplicationServices()` dodaj:
   ```csharp
   services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ApplicationDependencyInjection).Assembly));
   ```
4. W `Program.cs` CLI — resolve `IMediator` po budowie kontenera (jeszcze nie uzywany).

**Acceptance criteria:**
- [ ] `dotnet build` przechodzi
- [ ] MediatR jest zarejestrowany w DI
- [ ] Brak uzycia MediatR jeszcze (pure setup)
- [ ] Testy przechodza bez zmian

---

### M1-03: Zaprojektuj OperationProgress + IProgress<T>
**Labels:** `high` `refactor`
**Zalezy od:** M1-01

**Stan obecny:**
Exporter i Patcher uzywaja `Action<int, int>? progress` callbacks. CLI podpina `WriteProgress()`.

**Do zrobienia:**
1. Stworz `OperationProgress` record w Application:
   ```csharp
   public sealed record OperationProgress(int Current, int Total, string? Message = null);
   ```
2. Stworz `ConsoleProgressReporter : IProgress<OperationProgress>` w CLI.
3. Na razie NIE zmieniaj sygnatur Exporter/Patcher — to bedzie M1-04/M1-05.

**Acceptance criteria:**
- [ ] `OperationProgress` istnieje w Application
- [ ] `ConsoleProgressReporter` istnieje w CLI
- [ ] Brak breaking changes — stare `Action<int,int>` nadal dzialaja
- [ ] Build + testy ok

---

## Faza B: MediatR handlers

### M1-04: ExportTextsQuery + ExportTextsQueryHandler
**Labels:** `high` `feature`
**Zalezy od:** M1-02, M1-03

**Do zrobienia:**
1. **WAZNE: Przenies `ExportSummary` record** z `IExporter.cs` do osobnego pliku `Application/Features/Export/ExportSummary.cs`. Obecna definicja jest wewnatrz `IExporter.cs` (linia 26-29) — przy kasowaniu IExporter w M1-10 stracilibysmy ten typ.
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
6. Unit testy z NSubstitute dla IDatFileHandler.

**Acceptance criteria:**
- [ ] `ExportSummary` w osobnym pliku (NIE wewnatrz IExporter.cs)
- [ ] Handler zarejestrowany w MediatR
- [ ] `IMediator.Send(new ExportTextsQuery(...))` zwraca `Result<ExportSummary>`
- [ ] Unit testy: happy path, DAT not found, empty DAT
- [ ] Stary `Exporter` NADAL istnieje i dziala (jeszcze nie usuwamy)

---

### M1-05: ApplyPatchCommand + ApplyPatchCommandHandler
**Labels:** `high` `feature`
**Zalezy od:** M1-02, M1-03

**Do zrobienia:**
1. **WAZNE: Przenies `PatchSummary` record** z `IPatcher.cs` (linia 26-30) do osobnego pliku `Application/Features/Patch/PatchSummary.cs`. Przy kasowaniu IPatcher w M1-10 stracilibysmy ten typ.
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
5. Unit testy.

**Acceptance criteria:**
- [ ] `PatchSummary` w osobnym pliku (NIE wewnatrz IPatcher.cs)
- [ ] Handler dziala identycznie jak Patcher
- [ ] Unit testy: happy path, missing file, no translations, fragment not found
- [ ] Stary `Patcher` nadal istnieje

---

### M1-06: PreflightCheckQuery + Handler
**Labels:** `high` `feature`
**Zalezy od:** M1-02

**Stan obecny:**
`PreflightChecker` (CLI) miesza logike biznesowa z `Console.ReadLine()` i `Console.Write()`.

**Do zrobienia:**
1. Stworz `PreflightCheckQuery : IRequest<Result<PreflightReport>>`.
2. `PreflightReport` record: `bool IsGameRunning`, `bool HasWriteAccess`, `GameUpdateCheckResult? UpdateCheck`.
3. Handler TYLKO zbiera dane — zero Console I/O.
4. CLI czyta `PreflightReport` i decyduje co wyswietlic / o co zapytac usera.

**Acceptance criteria:**
- [ ] Handler nie ma zadnych zaleznosci od Console/UI
- [ ] `PreflightReport` zawiera wszystkie dane potrzebne do decyzji
- [ ] CLI nadal pyta usera "Continue anyway?" na podstawie raportu
- [ ] Unit testy z mockami

---

### M1-07: LoggingPipelineBehavior
**Labels:** `medium` `feature`
**Zalezy od:** M1-02

**Do zrobienia:**
1. Stworz `Application/Behaviors/LoggingPipelineBehavior<TRequest, TResponse> : IPipelineBehavior`.
2. Loguj: nazwa requestu, czas wykonania, czy sukces/failure.
3. Uzyj `ILogger` z Microsoft.Extensions.Logging (dodaj NuGet jesli brak).
4. Zarejestruj w DI jako open generic.

**Acceptance criteria:**
- [ ] Kazdy `IMediator.Send()` jest automatycznie logowany
- [ ] Log zawiera: request name, elapsed ms, success/failure
- [ ] Unit test z mock ILogger

---

### M1-08: ValidationPipelineBehavior
**Labels:** `medium` `feature`
**Zalezy od:** M1-02

**Do zrobienia:**
1. Stworz `ValidationPipelineBehavior<TRequest, TResponse> : IPipelineBehavior`.
2. Jesli `TResponse` jest `Result` lub `Result<T>`, waliduj request przed wykonaniem handlera.
3. Uzyj FluentValidation lub reczne walidatory (decyzja implementacyjna — FluentValidation preferowany).
4. Dodaj przykladowy walidator dla `ApplyPatchCommand` (sprawdz czy sciezki nie puste).

**Acceptance criteria:**
- [ ] Nieprawidlowe requesty zwracaja `Result.Failure` ZANIM handler sie wykona
- [ ] Unit testy: pusty path -> validation error

---

### M1-09: Refaktor Program.cs — przejscie na IMediator
**Labels:** `high` `refactor`
**Zalezy od:** M1-04, M1-05, M1-06

**Stan obecny:**
`Program.cs` uzywa `ExportCommand.Run()` i `PatchCommand.RunAsync()` — statyczne klasy z `IServiceProvider`.

**Do zrobienia:**
1. `export` -> `IMediator.Send(new ExportTextsQuery(...))`.
2. `patch` -> `IMediator.Send(new PreflightCheckQuery(...))`, potem `IMediator.Send(new ApplyPatchCommand(...))`.
3. **BackupManager zostaje jako CLI utility** — backup/restore to operacja plikowa specyficzna dla CLI flow. Nie przenosi sie do handlera. Program.cs wywoluje BackupManager.Create() miedzy preflight a patch, BackupManager.Restore() w razie failure. BackupManager.Create() uzywa ConsoleWriter (WriteInfo) — to jest OK, zostaje w CLI.
4. Zachowaj `DatPathResolver`, `BackupManager`, `ConsoleWriter` — to CLI-specific.
5. Przeplyw: resolve path -> preflight -> backup -> patch -> summary.
6. `ConsoleProgressReporter` jako IProgress<T>.

**Acceptance criteria:**
- [ ] CLI dziala IDENTYCZNIE jak przed refaktorem (te same komendy, te same outputy)
- [ ] `ExportCommand` i `PatchCommand` nie sa juz uzywane z Program.cs
- [ ] `PreflightChecker` nie jest juz uzywany bezposrednio
- [ ] `BackupManager` nadal w CLI, uzywany z Program.cs
- [ ] `dotnet run -- export` i `dotnet run -- patch polish` dzialaja

---

### M1-10: Usun stare serwisy
**Labels:** `high` `refactor`
**Zalezy od:** M1-09

**Do zrobienia:**
Usun martwy kod:
1. `IExporter` interface + `Exporter` class — `ExportSummary` juz przeniesiony do osobnego pliku w M1-04
2. `IPatcher` interface + `Patcher` class — `PatchSummary` juz przeniesiony do osobnego pliku w M1-05
3. `ExportCommand` static class
4. `PatchCommand` static class
5. `PreflightChecker` static class (logika przeniesiona do PreflightCheckQueryHandler + CLI)
6. Popraw DI registracje (usun stare `AddScoped<IExporter>`, `AddScoped<IPatcher>` z `ApplicationDependencyInjection.cs`)
7. Popraw testy ktore uzywaly starych interfejsow.

**NIE usuwaj:**
- `BackupManager` — nadal uzywany z Program.cs
- `DatPathResolver` — nadal uzywany z Program.cs
- `ConsoleWriter` — nadal uzywany
- `ExportSummary`, `PatchSummary` — juz przeniesione do Features/
- `TranslationFileParser`, `ITranslationParser` — nadal uzywane przez handlery

**Acceptance criteria:**
- [ ] Zero referencji do IExporter, IPatcher, ExportCommand, PatchCommand, PreflightChecker
- [ ] ExportSummary i PatchSummary istnieja w Features/ (NIE w starych interfejsach)
- [ ] DI nie rejestruje starych serwisow
- [ ] Build + testy ok
- [ ] CLI dziala bez zmian

---

### M1-11: Testy jednostkowe dla handlerow
**Labels:** `high` `test`
**Zalezy od:** M1-04, M1-05, M1-06

**Do zrobienia:**
1. `ExportTextsQueryHandlerTests` — mock IDatFileHandler, weryfikuj ExportSummary.
2. `ApplyPatchCommandHandlerTests` — mock IDatFileHandler + ITranslationParser.
3. `PreflightCheckQueryHandlerTests` — mock IGameProcessDetector, IWriteAccessChecker, IGameUpdateChecker.
4. `LoggingPipelineBehaviorTests`.
5. `ValidationPipelineBehaviorTests`.

**Acceptance criteria:**
- [ ] Minimum: happy path + 2 failure cases per handler
- [ ] FluentAssertions styl
- [ ] Naming: `MethodName_Scenario_ExpectedResult`

---

### M1-12: Testy integracyjne MediatR pipeline
**Labels:** `high` `test`
**Zalezy od:** M1-09, M1-11

**Do zrobienia:**
1. Test pelnego pipeline: request -> validation -> logging -> handler -> response.
2. Test z prawdziwym DI containerem (nie mocki).
3. Weryfikuj ze pipeline behaviors sa wykonywane w poprawnej kolejnosci.

**Acceptance criteria:**
- [ ] Pipeline test: Send request -> behaviors execute -> handler returns result
- [ ] Validation behavior blokuje nieprawidlowe requesty

---

## Faza C: Launch + Update Fix

### M1-13: IDatVersionReader — eksponuj vnum z OpenDatFileEx2
**Labels:** `high` `feature` `bug`
**Zalezy od:** M1-01

**Stan obecny:**
`DatFileHandler.Open()` linia 37-40 ignoruje vnum:
```csharp
int result = DatExportNative.OpenDatFileEx2(
    requestedHandle, path, DatExportNative.OpenFlagsReadWrite,
    out _, out _, out _, out _, out _, // <-- vnum wyrzucone!
    datIdStamp, firstIterGuid);
```

**Do zrobienia:**
1. Stworz `IDatVersionReader` w Application/Abstractions:
   ```csharp
   public interface IDatVersionReader
   {
       Result<DatVersionInfo> ReadVersion(string datFilePath);
   }
   public sealed record DatVersionInfo(int VnumDatFile, int VnumGameData);
   ```
2. Implementacja w Infrastructure — otwiera DAT normalnie (OpenFlagsReadWrite=130), czyta vnum z `OpenDatFileEx2` out parametrow, NATYCHMIAST zamyka. **NIE zakladaj read-only mode** — datexport.dll to zamkniety Turbine binary, nie wiadomo czy flags=2 (read-only) jest obslugiwany. Bezpieczniej: open ReadWrite, grab vnum, close.
3. Wywolanie PRZED normalnym Open() dla patcha — nie bedzie konfliktu handle'ow bo vnum reader otwiera i zamyka atomowo.

**Acceptance criteria:**
- [ ] `IDatVersionReader.ReadVersion()` zwraca vnum z DAT
- [ ] Otwiera i zamyka DAT atomowo (open -> read vnum -> close)
- [ ] NIE koliduje z pozniejszym Open() z DatFileHandler
- [ ] Unit test z mock, integration test z prawdziwym DAT (jesli dostepny)

**Kontekst:**
Rosjanie uzywaja `NinjaMark` (metadata w subfile 620750000) do wykrywania nadpisania. My uzywamy vnum — prostsze i bardziej niezawodne.

---

### M1-14: Napraw GameUpdateChecker — nie zapisuj wersji forum od razu
**Labels:** `critical` `bug`
**Zalezy od:** M1-13, M1-09

**UWAGA SEKWENCJI:** Ten ticket ZMIENIA zachowanie `IGameUpdateChecker` — po tej zmianie `CheckForUpdateAsync()` NIE zapisuje wersji. Stary `PreflightChecker` (CLI) polega na auto-save. Dlatego M1-09 (refaktor CLI na MediatR) MUSI byc zrobiony PRZED tym ticketem, inaczej stary CLI flow sie zepsuje (wykrywa update w kolko, nigdy nie zapisuje wersji).

**Stan obecny (GameUpdateChecker.cs:56-58):**
```csharp
if (updateDetected)
{
    Result saveResult = _versionFileStore.SaveVersion(versionFilePath, currentVersion);
```
Problem: zapisuje wersje z forum OD RAZU, zanim user faktycznie zainstalowal update. Jesli user kliknie "N" (nie aktualizuje) — patcher mysli ze update juz jest zainstalowany.

**Do zrobienia:**
1. `CheckForUpdateAsync()` NIE zapisuje wersji — tylko raportuje.
2. Dodaj nowa metode `ConfirmUpdateInstalled()` ktora:
   - Czyta vnum z DAT (via `IDatVersionReader`)
   - Porownuje z poprzednim vnum
   - Jesli vnum sie zmienil -> zapisuje nowa wersje forum
3. Zmien `GameUpdateCheckResult` zeby zawieralo `DatVersionInfo`.

**Nowy flow:**
```
1. Forum: "Jest 42.2" -> result.UpdateDetected = true
2. CLI pyta usera -> user odpala launcher -> instaluje update
3. CLI odpala ConfirmUpdateInstalled() -> czyta vnum z DAT
4. Vnum sie zmienil -> zapisujemy "42.2" do pliku
5. Dopiero teraz launch dozwolony
```

**Acceptance criteria:**
- [ ] `CheckForUpdateAsync()` nigdy nie zapisuje wersji
- [ ] Wersja zapisywana tylko po potwierdzeniu przez vnum z DAT
- [ ] Unit testy: wykrycie update -> brak zapisu; potwierdzenie vnum -> zapis
- [ ] Stare testy GameUpdateChecker zaktualizowane

---

### M1-15: IDatFileProtector — attrib +R/-R
**Labels:** `high` `feature`
**Zalezy od:** M1-01

**Stan obecny:**
Ochrona DAT jest w `lotro.bat`:
```batch
attrib +R "client_local_English.dat"
start /wait "" "TurbineLauncher.exe"
attrib -R "client_local_English.dat"
```

**Do zrobienia:**
1. Stworz `IDatFileProtector` w Application/Abstractions:
   ```csharp
   public interface IDatFileProtector
   {
       Result Protect(string datFilePath);
       Result Unprotect(string datFilePath);
       bool IsProtected(string datFilePath);
   }
   ```
2. Implementacja w Infrastructure: `File.SetAttributes()` z `FileAttributes.ReadOnly`.
3. NIE uzywaj `attrib.exe` (Process.Start) — uzyj .NET API.

**Acceptance criteria:**
- [ ] `Protect()` ustawia ReadOnly
- [ ] `Unprotect()` zdejmuje ReadOnly
- [ ] `IsProtected()` sprawdza atrybut
- [ ] Obsluga bledow: brak pliku, brak uprawnien
- [ ] Unit testy z temp plikami

---

### M1-16: IGameLauncher — Process.Start TurbineLauncher
**Labels:** `high` `feature`
**Zalezy od:** M1-01

**Do zrobienia:**
1. Stworz `IGameLauncher` w Application/Abstractions:
   ```csharp
   public interface IGameLauncher
   {
       Result<int> Launch(string lotroPath, bool waitForExit = true);
   }
   ```
2. Implementacja:
   - Auto-detect `TurbineLauncher.exe` wzgledem sciezki DAT
   - `Process.Start()` z `WaitForExit()` jesli `waitForExit`
   - Zwraca exit code procesu
3. NIE dodawaj flag `-disablePatch` — my uzywamy `attrib +R`.

**Acceptance criteria:**
- [ ] `Launch()` startuje TurbineLauncher.exe
- [ ] Czeka na zamkniecie jesli `waitForExit=true`
- [ ] Obsluga: TurbineLauncher not found, process error
- [ ] Unit test z mock (nie startuje prawdziwego procesu)

---

### M1-17: LaunchGameCommand + Handler
**Labels:** `high` `feature`
**Zalezy od:** M1-02, M1-13, M1-14, M1-15, M1-16

**Do zrobienia:**
1. Stworz `LaunchGameCommand : IRequest<Result>`.
2. Handler orchestruje:
   ```
   1. CheckForUpdate (forum)
   2. Jesli update wykryty -> ReadVersion (DAT vnum) -> porownaj
   3. Jesli wersje sie nie zgadzaja -> zwroc blad "zaktualizuj gre"
   4. Protect DAT (attrib +R)
   5. Launch gre
   6. Czekaj na zamkniecie
   7. Unprotect DAT (attrib -R)
   ```
3. `LaunchReport` record z detalami (wersja, czas gry, etc.)

**Acceptance criteria:**
- [ ] `dotnet run -- launch` startuje gre z ochrona DAT
- [ ] Update detection blokuje launch jesli wersje nie pasuja
- [ ] DAT jest chroniony PRZED i odchroniony PO grze
- [ ] Unit testy z mockami calego flow

---

### M1-18: Rejestracja komendy `launch` w Program.cs
**Labels:** `high` `feature`
**Zalezy od:** M1-17

**Do zrobienia:**
1. Dodaj `"launch"` do switch w `Program.cs`.
2. Resolve sciezka LOTRO (DatPathResolver).
3. `IMediator.Send(new LaunchGameCommand(...))`.
4. Wyswietl status (wersje, ochrone, czas gry).
5. Zaktualizuj `PrintUsage()`.

**Acceptance criteria:**
- [ ] `dotnet run -- launch` dziala
- [ ] `dotnet run -- launch C:\path\to\lotro` dziala
- [ ] PrintUsage() wyswietla komende launch
- [ ] Blad update -> komunikat + exit code

---

### M1-19: Testy Launch + Update Detection
**Labels:** `high` `test`
**Zalezy od:** M1-17, M1-18

**Do zrobienia:**
1. Unit testy `LaunchGameCommandHandler`:
   - Happy path: brak update, launch ok
   - Update detected + stary vnum -> blokada
   - Update detected + nowy vnum -> launch ok
   - Protect fail -> error
   - Launch fail -> unprotect + error
2. Integration testy (DI pipeline):
   - Caly flow z mockami
3. Testy `IDatFileProtector`:
   - Protect/Unprotect na temp plikach
   - IsProtected check

**Acceptance criteria:**
- [ ] Minimum 10 test cases dla launch flow
- [ ] Kazdy branch w orchestracji pokryty
- [ ] Edge case: DAT juz protected -> idempotent

---

## Faza D: Cleanup

### M1-20: Podlaczyc ArgsOrder/ArgsId w patcherze
**Labels:** `medium` `feature`

**Stan obecny:**
Pola `args_order` i `args_id` sa parsowane przez `TranslationFileParser` i przechowywane w `Translation` model, ale `Patcher` ustawia `fragment.Pieces` bez reorderingu argumentow. ArgsOrder jest uzywane do reorderingu `ArgRefs` w Fragment — sprawdz czy to dziala poprawnie.

**Do zrobienia:**
1. Zweryfikuj czy `Translation.ArgsOrder` jest przekazywane do `Fragment.ArgRefs` przy patchu.
2. Jesli nie — dodaj logike reorderingu:
   ```
   Jesli ArgsOrder = [2, 0, 1] (0-indexed, po konwersji z pliku)
   to ArgRefs powinny byc przelozone w tej kolejnosci
   ```
3. Dodaj testy z rzeczywistym przykladem.

**Acceptance criteria:**
- [ ] ArgsOrder reorderuje ArgRefs w fragment
- [ ] Testy z przykladem: "arg2 arg0 arg1" z ArgsOrder=[2,0,1]
- [ ] Brak regression w istniejacych testach

---

### M1-21: Pole `approved` — ignoruj w CLI, zachowaj w formacie
**Labels:** `low` `refactor`

**Stan obecny:**
Parser czyta `approved` z pliku ale nigdzie nie uzywa. Pole jest w formacie pliku.

**Do zrobienia:**
1. Zweryfikuj ze `approved` jest parsowane i przechowywane.
2. Upewnij sie ze CLI je ignoruje (patchuje wszystko niezaleznie od approved).
3. Dodaj komentarz ze `approved` bedzie uzywane w M2 (DB: IsApproved).
4. Dodaj property `IsApproved` do `Translation` model jesli brakuje.

**Acceptance criteria:**
- [ ] `approved=0` linie sa patchowane tak samo jak `approved=1`
- [ ] Model ma property IsApproved (przygotowanie na M2)

---

# M2: Baza danych

## Faza A: PostgreSQL + EF Core

### M2-01: docker-compose.yml z PostgreSQL
**Labels:** `critical` `infra`
**Zalezy od:** M1-01

**Do zrobienia:**
1. Stworz `docker-compose.yml` w rootcie:
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
2. Dodaj `.env.example` z credentialami.
3. Dodaj `docker-compose.yml` do .gitignore NIE — to plik dev-env, powinien byc w repo.

**Acceptance criteria:**
- [ ] `docker-compose up -d` startuje PostgreSQL na localhost:5432
- [ ] Mozna sie polaczyc: `psql -h localhost -U lotro -d lotro_translations`
- [ ] Volume persystuje dane miedzy restartami

---

### M2-02: Rozdziel Infrastructure na osobne projekty
**Labels:** `high` `infra`
**Zalezy od:** M1-01

**Stan obecny:**
Jeden projekt `Infrastructure` z `net10.0-windows` x86 (bo datexport.dll). Zawiera:
- `DatFile/` — P/Invoke, DatFileHandler, DatFileLocator (Windows x86)
- `Diagnostics/` — GameProcessDetector (Windows), WriteAccessChecker (cross-platform)
- `Network/` — ForumPageFetcher (cross-platform)
- `Storage/` — VersionFileStore (cross-platform)

Problem: EF Core + Npgsql wymaga `net10.0` AnyCPU. WebApp tez potrzebuje ForumPageFetcher (update checking) ale NIE MOZE referencjowac x86 projektu.

**Do zrobienia — TRZY projekty (nie dwa):**

1. **`LotroKoniecDev.Infrastructure.DatFile`** (`net10.0-windows`, x86):
   - DatFile/ (DatExportNative, DatFileHandler)
   - Discovery/DatFileLocator (Windows Registry)
   - Diagnostics/GameProcessDetector (Process.GetProcessesByName — Windows)
   - datexport.dll + native DLLs
   - Reference: Application

2. **`LotroKoniecDev.Infrastructure.Common`** (`net10.0`, AnyCPU):
   - Network/ForumPageFetcher
   - Storage/VersionFileStore
   - Diagnostics/WriteAccessChecker
   - Reference: Application

3. **`LotroKoniecDev.Infrastructure.Persistence`** (`net10.0`, AnyCPU):
   - EF Core, Npgsql
   - AppDbContext, Entities, Repositories
   - Reference: Application

**Referencje:**
- CLI -> DatFile + Common (+ Persistence jesli import/export DB)
- WebApp -> Common + Persistence (BEZ DatFile)
- WPF -> DatFile + Common

**Alternatywa (prostsza, ale mniej czysta):** Dwa projekty — zostaw stary Infrastructure jak jest (x86, datexport + wszystko inne), dodaj Persistence (AnyCPU). WebApp traci dostep do ForumPageFetcher — akceptowalne jesli update checking jest tylko w CLI/WPF.

4. Zaktualizuj .slnx, project references, DI registration (osobne `AddDatFileServices()`, `AddCommonServices()`, `AddPersistenceServices()`).

**Acceptance criteria:**
- [ ] Trzy projekty Infrastructure: .DatFile (x86), .Common (AnyCPU), .Persistence (AnyCPU)
- [ ] Build przechodzi
- [ ] Testy przechodza
- [ ] WebApp moze referencjowac .Common + .Persistence BEZ .DatFile
- [ ] CLI referencja do wszystkich trzech

---

### M2-03: EF Core + Npgsql — NuGet + AppDbContext
**Labels:** `high` `infra`
**Zalezy od:** M2-01, M2-02

**Do zrobienia:**
1. Dodaj do `Directory.Packages.props`:
   - `Microsoft.EntityFrameworkCore` (najnowsza wersja)
   - `Npgsql.EntityFrameworkCore.PostgreSQL`
   - `Microsoft.EntityFrameworkCore.Design` (tools)
2. Stworz `AppDbContext` w Infrastructure.Persistence:
   ```csharp
   public class AppDbContext : DbContext
   {
       public DbSet<LanguageEntity> Languages => Set<LanguageEntity>();
       public DbSet<ExportedTextEntity> ExportedTexts => Set<ExportedTextEntity>();
       public DbSet<TranslationEntity> Translations => Set<TranslationEntity>();
       // ... reszta
   }
   ```
3. Konfiguracja polaczenia: connection string z `appsettings.json` lub env variable.

**Acceptance criteria:**
- [ ] `AppDbContext` kompiluje sie
- [ ] Connection string konfigurowalny
- [ ] `dotnet ef` tool dziala z projektem Persistence

---

### M2-04: Entities — zaprojektuj modele bazodanowe
**Labels:** `high` `feature`
**Zalezy od:** M2-02

**Do zrobienia:**
Stworz entities w `Infrastructure.Persistence/Entities/`:

1. **LanguageEntity**: Code (PK, `pl`/`en`), Name, IsActive
2. **ExportedTextEntity**: Id, FileId, GossipId, EnglishContent, ImportedAt. UNIQUE(FileId, GossipId).
3. **TranslationEntity**: Id, FileId, GossipId, LanguageCode (FK), Content, ArgsOrder (string), ArgsId, IsApproved, Notes, CreatedAt, UpdatedAt. UNIQUE(FileId, GossipId, LanguageCode).
4. **TranslationHistoryEntity**: Id, TranslationId (FK), OldContent, NewContent, ChangedAt.
5. **GlossaryTermEntity**: Id, EnglishTerm, PolishTerm, Notes, Category, CreatedAt. UNIQUE(EnglishTerm, Category).
6. **TextContextEntity**: Id, FileId, GossipId, ContextType, ParentName, ParentCategory, ParentLevel, NpcName, Region, SourceFile, ImportedAt. UNIQUE(FileId, GossipId, ContextType).
7. **DatVersionEntity**: Id, VnumDatFile, VnumGameData, ForumVersion, DetectedAt.

**Kluczowe decyzje:**

1. Dwa modele `Translation`:
   - `Domain.Models.Translation` — DTO dla DAT pipeline (init-only, brak DB deps)
   - `Persistence.Entities.TranslationEntity` — EF entity (timestamps, FK)
   - Mapping w repository

2. **GossipId typ: `long` (bigint) w bazie.** W Domain `Translation.GossipId` jest `int`, ale `Fragment.FragmentId` jest `ulong` (8 bajtow). Konwersja `(ulong)GossipId` w `Translation.FragmentId` moze tracic dane dla duzych wartosci. W DB entities uzyj `long` (bigint w PostgreSQL) dla bezpieczenstwa. FileId tez `int` ale bezpieczny (32-bit w DAT).

**Acceptance criteria:**
- [ ] Wszystkie entities stworzone
- [ ] GossipId jako `long` (bigint) we WSZYSTKICH entities
- [ ] EF konfiguracja (Fluent API lub Data Annotations) z unique constraints
- [ ] Indexes na (FileId, GossipId) gdzie potrzebne
- [ ] Build przechodzi

---

### M2-05: Migracja EF Core + auto-migrate w dev
**Labels:** `high` `infra`
**Zalezy od:** M2-03, M2-04

**Do zrobienia:**
1. `dotnet ef migrations add InitialCreate` — stworz pierwsza migracje.
2. Dodaj `MigrateAsync()` przy starcie dev:
   ```csharp
   if (env.IsDevelopment())
       await dbContext.Database.MigrateAsync();
   ```
3. NIE auto-migrate w produkcji.

**Acceptance criteria:**
- [ ] `docker-compose up` + app start -> schema stworzona
- [ ] Wszystkie tabele z poprawnymi kolumnami i constraintami
- [ ] Ponowne uruchomienie nie psuje istniejacych danych

---

### M2-06: Seed jezyka polskiego
**Labels:** `medium` `infra`
**Zalezy od:** M2-05

**Do zrobienia:**
1. W migracji lub w seed method:
   ```sql
   INSERT INTO Languages (Code, Name, IsActive)
   VALUES ('pl', 'Polish', true), ('en', 'English', true)
   ON CONFLICT DO NOTHING;
   ```
2. Uzyj `HasData()` w EF lub custom seed.

**Acceptance criteria:**
- [ ] Po migracji: tabela Languages ma `pl` i `en`
- [ ] Seed jest idempotentny

---

## Faza B: Repozytoria + Import

### M2-07: IExportedTextRepository + implementacja
**Labels:** `high` `feature`
**Zalezy od:** M2-05

**Do zrobienia:**
1. Stworz `IExportedTextRepository` w Application/Abstractions:
   ```csharp
   public interface IExportedTextRepository
   {
       Task<Result> UpsertBatchAsync(IEnumerable<ExportedText> texts);
       Task<Result<ExportedText?>> GetByIdsAsync(int fileId, long gossipId);
       Task<Result<int>> GetCountAsync();
   }
   ```
2. Domain DTO: `ExportedText` record (FileId, GossipId, EnglishContent).
3. Implementacja w Persistence z EF Core.
4. Upsert = INSERT ON CONFLICT UPDATE (PostgreSQL).

**Acceptance criteria:**
- [ ] Batch upsert 100k+ rekordow w < 30s
- [ ] Duplicate (FileId, GossipId) -> update content
- [ ] Integration test z prawdziwa baza (TestContainers lub docker-compose)

---

### M2-08: ITranslationRepository + implementacja
**Labels:** `high` `feature`
**Zalezy od:** M2-05

**Do zrobienia:**
1. Stworz `ITranslationRepository` w Application/Abstractions:
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
2. `TranslationDto` = Application-level DTO, mapping do `TranslationEntity`.
3. Auto-history: przy upsert z innym content -> dodaj `TranslationHistoryEntity`.

**Acceptance criteria:**
- [ ] CRUD dziala
- [ ] Historia zmian jest automatycznie rejestrowana
- [ ] UNIQUE constraint (FileId, GossipId, LanguageCode) enforced
- [ ] Integration test

---

### M2-09: ImportExportedTextsCommand + Handler
**Labels:** `high` `feature`
**Zalezy od:** M2-07

**Do zrobienia:**
1. Stworz `ImportExportedTextsCommand : IRequest<Result<ImportSummary>>`:
   ```csharp
   public sealed record ImportExportedTextsCommand(string FilePath) : IRequest<Result<ImportSummary>>;
   public sealed record ImportSummary(int Imported, int Updated, int Skipped);
   ```
2. Handler:
   - Parsuj `exported.txt` uzywajac `TranslationFileParser` (format identyczny)
   - **UWAGA SEMANTYCZNA:** Parser zwraca `List<Translation>` gdzie `Content` = angielski tekst zrodlowy. Mapping: `Translation.Content` -> `ExportedTextEntity.EnglishContent`, `Translation.FileId` -> `ExportedTextEntity.FileId`, `Translation.GossipId` -> `ExportedTextEntity.GossipId`. ArgsOrder/ArgsId/Approved ignorowane przy imporcie exported texts.
   - Batch upsert do `ExportedTexts`
   - Raportuj ile nowych / zaktualizowanych
3. Plik `exported.txt` ma format: `file_id||gossip_id||content||args||args_id||approved`
4. **Uwaga:** `TranslationFileParser` uzywa `line.Split("||")` (linia 69) — jesli tresc zawiera `||`, parsowanie sie psuje. W praktyce eksportowany tekst z DAT nie powinien zawierac `||`, ale rozwazyc defensywne parsowanie (split z limitem 6 pol, ostatnie pola skladane) PRZED masowym importem. Patrz M2-13.

**Acceptance criteria:**
- [ ] Import 500k+ linii w rozsadnym czasie (< 2 min)
- [ ] Drugi import tego samego pliku -> updates, nie duplikaty
- [ ] Mapping Translation -> ExportedText jest jawny i przetestowany
- [ ] Unit + integration test

---

### M2-10: Translation CRUD — Commands/Queries
**Labels:** `high` `feature`
**Zalezy od:** M2-08

**Do zrobienia:**
1. `CreateTranslationCommand(FileId, GossipId, LanguageCode, Content, ArgsOrder?, Notes?)`
2. `UpdateTranslationCommand(Id, Content, ArgsOrder?, Notes?)`
3. `ApproveTranslationCommand(Id)`
4. `GetTranslationQuery(FileId, GossipId, LanguageCode)`
5. `ListTranslationsQuery(LanguageCode, Page, PageSize, Filter?)`

Handlery uzywaja `ITranslationRepository`.

**Acceptance criteria:**
- [ ] Full CRUD + approve/reject
- [ ] Paginacja dziala
- [ ] Filter po content / FileId
- [ ] Historia zmian przy kazdym upsert

---

### M2-11: ExportTranslationsQuery — DB -> polish.txt
**Labels:** `high` `feature`
**Zalezy od:** M2-08

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
- [ ] Test: porownanie export z oryginalna zawartoscia

---

### M2-12: Migracja istniejacego polish.txt do bazy
**Labels:** `medium` `feature`
**Zalezy od:** M2-08, M2-09

**Do zrobienia:**
1. Stworz komende `import-translations` (CLI) lub handler:
   - Parsuj `translations/polish.txt`
   - Dla kazdej linii: `ITranslationRepository.UpsertAsync()` z `LanguageCode = "pl"`
2. Ustaw `IsApproved = true` dla wszystkich (juz przetlumaczone i przetestowane).

**Acceptance criteria:**
- [ ] Wszystkie linie z `polish.txt` sa w bazie
- [ ] `IsApproved = true`
- [ ] Duplikat import -> update, nie blad

---

### M2-13: Obsluga separatora || w tresci (defensywne parsowanie)
**Labels:** `medium` `bug`
**Zalezy od:** M1-01 (brak zaleznosci od M2 — to fix w istniejacym parserze)
**Blokuje:** M2-09 (opcjonalnie — import dziala bez tego jesli tresc nie zawiera ||, ale bezpieczniej zrobic przed)

**Stan obecny:**
`TranslationFileParser.ParseLine()` (linia 69): `line.Split([FieldSeparator], StringSplitOptions.None)`.
Format ma 6 pol: `file_id||gossip_id||content||args_order||args_id||approved`.
Jesli `content` zawiera `||` — parser widzi 7+ pol i bierze zly indeks dla args_order.

**Do zrobienia:**
Najlepsza strategia: **parsuj od lewej z limitem pol** (nie escaping).
1. Zmien `Split()` na: najpierw wyciagnij pierwsze 2 pola (file_id, gossip_id) i ostatnie 3 (args_order, args_id, approved) — content to WSZYSTKO pomiedzy.
   ```csharp
   // Split na max 3 czesci od prawej (args_order||args_id||approved)
   // lub: Split na max 6, sklejaj srodkowe jesli wiecej
   ```
2. Alternatywa: `line.Split("||", 6)` — C# Split z count bierze pierwsze N-1 separatorow, reszta idzie do ostatniego elementu. Ale to wrzuca nadmiar do `approved`, nie do `content`. Trzeba od prawej.
3. Przetestuj roundtrip: parse -> export -> parse -> identyczny wynik.

**Acceptance criteria:**
- [ ] Tresc z `||` jest poprawnie parsowana
- [ ] Roundtrip test: content z `||` -> export -> parse -> identyczny
- [ ] Istniejace testy TranslationFileParser nadal przechodza

---

## Faza C: LOTRO Companion + Glossary

### M2-14: TextContexts entity + repository
**Labels:** `high` `feature`
**Zalezy od:** M2-05

**Do zrobienia:**
1. Stworz `ITextContextRepository` w Application:
   ```csharp
   public interface ITextContextRepository
   {
       Task<Result> UpsertBatchAsync(IEnumerable<TextContext> contexts);
       Task<Result<IReadOnlyList<TextContext>>> GetByIdsAsync(int fileId, long gossipId);
   }
   ```
2. `TextContext` DTO: FileId, GossipId, ContextType, ParentName, ParentCategory, ParentLevel, NpcName, Region, SourceFile.
3. Implementacja w Persistence.

**Acceptance criteria:**
- [ ] Batch upsert dziala
- [ ] Jeden (FileId, GossipId) moze miec wiele kontekstow (rozne ContextType)
- [ ] UNIQUE(FileId, GossipId, ContextType) enforced

---

### M2-15: LOTRO Companion XML parser
**Labels:** `high` `feature`

**Kontekst:**
https://github.com/LotroCompanion/lotro-data zawiera XML z metadanymi:
- `quests.xml` (~574k linii) — questy z dialogami
- `deeds.xml` — deedy
- `NPCs.xml` — NPC
- itd.

Format kluczowy: `key:{file_id}:{gossip_id}` — ID zgadzaja sie 1:1 z naszym exportem.

**Do zrobienia:**
1. Stworz parser(y) w Application/Features/Context/:
   - `QuestXmlParser` — parsuj quests.xml
   - `DeedXmlParser` — parsuj deeds.xml
   - Wyciagnij: `key:{file_id}:{gossip_id}`, nazwa questa/deeda, region, level, NPC
2. Uzyj `XmlReader` (streaming) — pliki sa DUZE.
3. Output: `IEnumerable<TextContext>`.

**Acceptance criteria:**
- [ ] Parsowanie quests.xml -> lista TextContext z poprawnymi FileId/GossipId
- [ ] Streaming (nie laduj calego XML do pamieci)
- [ ] Unit test z malym XML sample

---

### M2-16: ImportContextCommand + Handler
**Labels:** `high` `feature`
**Zalezy od:** M2-14, M2-15

**Do zrobienia:**
1. `ImportContextCommand(string XmlDirectoryPath)`:
   - Skanuj katalog na quests.xml, deeds.xml, NPCs.xml itd.
   - Parsuj kazdy plik
   - Batch upsert do TextContexts
2. Progress reporting (IProgress<T>).

**Acceptance criteria:**
- [ ] Import pelnego lotro-data -> baza pena kontekstow
- [ ] Duplikat import -> update
- [ ] Raport: ile zaimportowano per ContextType

---

### M2-17: GlossaryTerms entity + CRUD
**Labels:** `medium` `feature`
**Zalezy od:** M2-05

**Do zrobienia:**
1. `IGlossaryRepository` w Application.
2. CRUD: `CreateTerm`, `UpdateTerm`, `DeleteTerm`, `SearchTerms(query)`, `ListTerms(category?)`.
3. Kategorie: ProperNouns, Locations, Items, Skills, UI, General.
4. Seed z podstawowymi terminami Tolkienowskimi (Moria = Moria, Shire = Shire/Hrabstwo, etc.)

**Acceptance criteria:**
- [ ] CRUD dziala
- [ ] Search po angielskim/polskim terminie
- [ ] UNIQUE(EnglishTerm, Category)
- [ ] Seed z ~20 podstawowych terminow

---

### M2-18: DatVersions entity — historia wersji
**Labels:** `medium` `feature`
**Zalezy od:** M2-05

**Do zrobienia:**
1. Entity + prosta metoda `RecordVersion(vnumDatFile, vnumGameData, forumVersion)`.
2. Query: `GetLatestVersion()`, `GetHistory(count)`.
3. Integracja z `GameUpdateChecker` (M1-14) — zapisuj do bazy zamiast pliku.

**Acceptance criteria:**
- [ ] Kazda zmiana wersji DAT/forum jest rejestrowana
- [ ] Historia dostepna do przegladania

---

## Faza D: Testy M2

### M2-19: Testy unit — repozytoria, parsery, handlery
**Labels:** `high` `test`
**Zalezy od:** M2-07..M2-18

**Do zrobienia:**
1. Testy repozytoriow z InMemory provider lub TestContainers.
2. Testy parserow XML z sample danymi.
3. Testy handlerow z mockami.

**Acceptance criteria:**
- [ ] Kazdy handler ma min. 3 test cases
- [ ] Parsery XML przetestowane z edge cases
- [ ] Repo testy z prawdziwa baza (TestContainers preferowane)

---

### M2-20: Testy integracyjne — pelen pipeline
**Labels:** `high` `test`
**Zalezy od:** M2-19

**Do zrobienia:**
1. Test: CLI export -> import to DB -> translate in DB -> export from DB -> CLI patch.
2. Test: import exported.txt + import Companion XML -> context jest widoczny.
3. Test: glossary CRUD.

**Acceptance criteria:**
- [ ] Pelen roundtrip przechodzi
- [ ] Kontekst z Companion jest polaczony z ExportedTexts

---

# M3: Aplikacja webowa (Blazor SSR)

### M3-01: Stworz projekt Blazor SSR
**Labels:** `high` `infra`
**Zalezy od:** M1-01, M2-02

**Do zrobienia:**
1. `dotnet new blazor -n LotroKoniecDev.WebApp --interactivity Server`
2. TFM: `net10.0`, AnyCPU.
3. Reference: Application, Infrastructure.Persistence (NIE Infrastructure.DatFile).
4. Dodaj do .slnx.
5. DI: MediatR, EF Core, DbContext.

**Acceptance criteria:**
- [ ] `dotnet run --project src/LotroKoniecDev.WebApp` startuje na localhost:5000
- [ ] Defaultowa strona Blazor widoczna
- [ ] Brak referencji do DatFile/P/Invoke

---

### M3-02: Layout i nawigacja
**Labels:** `high` `feature`
**Zalezy od:** M3-01

**Do zrobienia:**
1. Bootstrap layout (sidebar + main content).
2. Nawigacja: Translations, Quests, Glossary, Import/Export, Dashboard.
3. Polish UI text.

**Acceptance criteria:**
- [ ] Nawigacja dziala
- [ ] Responsive na desktop

---

### M3-03: DI setup — MediatR + EF Core
**Labels:** `high` `infra`
**Zalezy od:** M3-01

**Do zrobienia:**
1. W `Program.cs` WebApp:
   ```csharp
   builder.Services.AddApplicationServices();
   builder.Services.AddPersistenceServices(connectionString);
   ```
2. Auto-migrate w Development.
3. Health check na PostgreSQL.

**Acceptance criteria:**
- [ ] MediatR resolve'uje handlery
- [ ] DbContext injected i dziala
- [ ] Health check `/health` zwraca 200

---

### M3-04: Lista tlumaczen (tabela, filtruj, paginacja)
**Labels:** `high` `feature`
**Zalezy od:** M3-03

**Do zrobienia:**
1. Strona `/translations` — tabela z kolumnami: FileId, GossipId, English, Polish, Status, Context.
2. Filtrowanie: po tekscie, po statusie (approved/not), po ContextType.
3. Paginacja (25/50/100 per page).
4. Sortowanie po kolumnach.
5. Context z TextContexts (jesli dostepny): nazwa questa, NPC, region.

**Acceptance criteria:**
- [ ] Tabela wyswietla tlumaczenia z bazy
- [ ] Filtrowanie po tekscie dziala
- [ ] Paginacja dziala
- [ ] Kontekst widoczny (jezeli zaimportowany)

---

### M3-05: Edytor tlumaczen (side-by-side EN/PL + kontekst)
**Labels:** `high` `feature`
**Zalezy od:** M3-04

**Do zrobienia:**
1. Strona `/translations/{id}/edit` lub modal.
2. Lewy panel: angielski tekst (read-only).
3. Prawy panel: polski tekst (edytowalny textarea).
4. Panel kontekstu: quest name, NPC, region, level (z TextContexts).
5. Podswietlenie `<--DO_NOT_TOUCH!-->` na czerwono.
6. Save -> `IMediator.Send(new UpdateTranslationCommand(...))`.

**Acceptance criteria:**
- [ ] Side-by-side widok
- [ ] Edycja i zapis dziala
- [ ] Placeholdery podswietlone
- [ ] Kontekst widoczny

---

### M3-06: Podswietlanie DO_NOT_TOUCH i walidacja placeholderow
**Labels:** `medium` `feature`
**Zalezy od:** M3-05

**Do zrobienia:**
1. W edytorze: `<--DO_NOT_TOUCH!-->` wyrozniany kolorem/stylem.
2. Walidacja przy save: liczba placeholderow w PL == liczba w EN.
3. Ostrzezenie jesli niezgodnosc.

**Acceptance criteria:**
- [ ] Placeholder wizualnie wyrozniany
- [ ] Walidacja: rozna liczba placeholderow -> warning

---

### M3-07: Dashboard — statystyki
**Labels:** `medium` `feature`
**Zalezy od:** M3-03

**Do zrobienia:**
1. Strona `/dashboard`:
   - Total strings, translated, approved, untranslated
   - Progress bar (%)
   - Ostatnie edycje (10 newest)
   - Stats per ContextType (quests: 40%, deeds: 20%, etc.)
2. Uzyj MediatR queries.

**Acceptance criteria:**
- [ ] Statystyki sa poprawne
- [ ] Progress bar widoczny

---

### M3-08: Import/Export endpoints
**Labels:** `medium` `feature`
**Zalezy od:** M3-03, M2-09 (ImportExportedTextsCommand), M2-11 (ExportTranslationsQuery)

**Do zrobienia:**
1. `POST /api/v1/db-update` — upload `exported.txt`, import do bazy via `IMediator.Send(new ImportExportedTextsCommand(...))`.
2. `GET /api/v1/translations/export?lang=pl` — download `polish.txt` via `IMediator.Send(new ExportTranslationsQuery(...))`.
3. Minimal API endpoints w WebApp.
4. Walidacja pliku (rozmiar, format).

**Acceptance criteria:**
- [ ] Upload exported.txt przez API -> import do DB
- [ ] Download polish.txt -> kompatybilny z CLI patch
- [ ] Error handling (zly format, pusty plik)

---

### M3-09: Glossary UI
**Labels:** `medium` `feature`
**Zalezy od:** M3-03, M2-17

**Do zrobienia:**
1. Strona `/glossary` — lista terminow.
2. Dodawanie / edycja terminow.
3. Szukanie.
4. Kategorie (filter).

**Acceptance criteria:**
- [ ] CRUD terminow w UI
- [ ] Szukanie po EN/PL

---

### M3-10: Keyboard shortcuts
**Labels:** `medium` `feature`
**Zalezy od:** M3-05

**Do zrobienia:**
1. `Ctrl+S` — save.
2. `Ctrl+Enter` — save + next.
3. `Ctrl+Shift+Enter` — approve + next.
4. JS interop dla keyboard events.

**Acceptance criteria:**
- [ ] Skroty dzialaja w edytorze
- [ ] Nie koliduja z browser shortcuts

---

### M3-11: Przegladarka questow/deedow
**Labels:** `medium` `feature`
**Zalezy od:** M3-03, M2-14

**Do zrobienia:**
1. Strona `/quests` — lista questow z TextContexts.
2. Kliknij quest -> lista stringow nalezacych do tego questa.
3. Grupowanie po region, level, NPC.

**Acceptance criteria:**
- [ ] Questy widoczne z pogrupowanymi stringami
- [ ] Nawigacja quest -> stringi -> edycja

---

### M3-12: Bulk operations
**Labels:** `medium` `feature`
**Zalezy od:** M3-04

**Do zrobienia:**
1. Checkbox na kazdym wierszu tabeli.
2. "Approve selected" / "Reject selected" button.
3. Batch MediatR command.

**Acceptance criteria:**
- [ ] Zaznacz 50 stringow -> Approve -> wszystkie approved
- [ ] Batch operation w jednej transakcji

---

### M3-13: Docker — Web App + PostgreSQL
**Labels:** `low` `infra`
**Zalezy od:** M3-01, M2-01

**Do zrobienia:**
1. `Dockerfile` dla WebApp.
2. Dodaj do `docker-compose.yml`:
   ```yaml
   webapp:
     build: .
     ports:
       - "5000:8080"
     depends_on:
       - db
   ```

**Acceptance criteria:**
- [ ] `docker-compose up` startuje WebApp + PostgreSQL
- [ ] WebApp laczy sie z DB automatycznie

---

### M3-14: Style guide page
**Labels:** `low` `feature`
**Zalezy od:** M3-01

**Do zrobienia:**
1. Statyczna strona `/style-guide` z zasadami:
   - Tolkienowskie nazwy wlasne (Moria, Shire, etc.)
   - Konwencje plci (ty/Ty, wy/Wy)
   - Placeholdery — jak obchodzic DO_NOT_TOUCH
   - Styl: formalny vs nieformalny
2. Markdown renderowany do HTML.

**Acceptance criteria:**
- [ ] Strona dostepna
- [ ] Edytowalna jako markdown

---

### M3-15: Testy Web App
**Labels:** `high` `test`
**Zalezy od:** M3-04, M3-05

**Do zrobienia:**
1. API endpoint tests (WebApplicationFactory).
2. Component tests (bUnit) dla kluczowych komponentow.
3. Integration: import -> list -> edit -> export roundtrip.

**Acceptance criteria:**
- [ ] API endpoints przetestowane
- [ ] Kluczowe flows majae2e testy

---

# M4: Desktop App (LotroPoPolsku.exe)

### M4-01: Stworz projekt WPF
**Labels:** `high` `infra`
**Zalezy od:** M1-05 (ApplyPatchCommand), M1-17 (LaunchGameCommand), M2-02 (split infra — potrzebny Infrastructure.DatFile + Infrastructure.Common)

**Do zrobienia:**
1. `dotnet new wpf -n LotroKoniecDev.DesktopApp`
2. TFM: `net10.0-windows`, x86.
3. Reference: Application, Infrastructure.DatFile.
4. DI: MediatR, ten sam co CLI.
5. Dodaj do .slnx.

**Acceptance criteria:**
- [ ] Projekt kompiluje sie
- [ ] Puste okno WPF sie otwiera
- [ ] DI dziala

---

### M4-02: Ikona pierscienia + splash screen
**Labels:** `medium` `feature`
**Zalezy od:** M4-01

**Do zrobienia:**
1. Ikona aplikacji (pierscien/tolkienowski motyw) — .ico.
2. Splash screen przy starcie.
3. Tray icon (opcjonalnie).

**Acceptance criteria:**
- [ ] Ikona widoczna na pasku zadan i pulpicie
- [ ] Splash screen przy starcie

---

### M4-03: Glowne okno — status + przyciski
**Labels:** `high` `feature`
**Zalezy od:** M4-01

**Do zrobienia:**
1. Layout:
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
   │  Patchowanie... 600/1000       │
   └────────────────────────────────┘
   ```
2. Binding na ViewModel (MVVM).
3. Status zmienia sie dynamicznie.

**Acceptance criteria:**
- [ ] Okno wyswietla status
- [ ] Przyciski "Patchuj" i "Graj" widoczne

---

### M4-04: Przycisk "Patchuj"
**Labels:** `high` `feature`
**Zalezy od:** M4-03, M3-08 (API endpoint GET /api/v1/translations/export)

**Do zrobienia:**
1. Click -> pobierz `polish.txt` z web API (GET /api/v1/translations/export). URL z ustawien (M4-08).
2. Zapisz do temp pliku.
3. `IMediator.Send(new ApplyPatchCommand(tempFile, datPath))`.
4. Progress bar + status text.
5. Disable przycisk podczas patchowania.

**Acceptance criteria:**
- [ ] Pobiera polish.txt z API (URL konfigurowalny)
- [ ] Patchuje DAT
- [ ] Progress bar animowany
- [ ] Blokada UI podczas patcha
- [ ] Fallback: jesli API niedostepne -> komunikat bledu

---

### M4-05: Przycisk "Graj"
**Labels:** `high` `feature`
**Zalezy od:** M4-03

**Do zrobienia:**
1. Click -> `IMediator.Send(new LaunchGameCommand(...))`.
2. Protect DAT -> launch -> wait -> unprotect.
3. Status: "Gra uruchomiona..." -> "Gotowe".

**Acceptance criteria:**
- [ ] Launch gry z ochrona DAT
- [ ] Status updating

---

### M4-06: Auto-detekcja LOTRO (zero konfiguracji)
**Labels:** `high` `feature`
**Zalezy od:** M4-01

**Do zrobienia:**
1. Uzyj `IDatFileLocator` (juz istnieje).
2. Przy pierwszym uruchomieniu: auto-detect LOTRO path.
3. Jesli nie znalezione: dialog "Wybierz folder LOTRO".
4. Zapisz w `%AppData%/LotroPoPolsku/config.json`.

**Acceptance criteria:**
- [ ] Przy pierwszym starcie: auto-detect lub dialog
- [ ] Sciezka zapisana i reuse przy kolejnych uruchomieniach

---

### M4-07: Game update alert
**Labels:** `high` `feature`
**Zalezy od:** M4-03, M1-14

**Do zrobienia:**
1. Banner na gorze okna: "Wykryto aktualizacje gry! Zaktualizuj gre przed patchowaniem."
2. Przycisk "Graj" zablokowany dopoki wersje nie pasuja.
3. Instrukcja: "Uruchom oficjalny launcher, zaktualizuj, wróc tutaj."

**Acceptance criteria:**
- [ ] Banner widoczny przy mismatch wersji
- [ ] Przycisk "Graj" disabled
- [ ] Po aktualizacji: banner znika

---

### M4-08: Ustawienia
**Labels:** `medium` `feature`
**Zalezy od:** M4-01

**Do zrobienia:**
1. Okno/panel ustawien:
   - Sciezka LOTRO (browse dialog)
   - Jezyk tlumaczenia (dropdown, default: Polish)
   - Auto-patch on launch (checkbox)
   - **URL serwera tlumaczen** (default: `http://localhost:5000`, potem produkcyjny URL) — potrzebny dla "Patchuj" (M4-04)
2. Zapis do `%AppData%/LotroPoPolsku/config.json`.

**Acceptance criteria:**
- [ ] Ustawienia edytowalne i persystowane
- [ ] Zmiana sciezki -> walidacja (czy istnieje DAT)
- [ ] URL serwera ma rozsadny default

---

### M4-09: Auto-update apki
**Labels:** `medium` `feature`
**Zalezy od:** M4-01

**Do zrobienia:**
1. Sprawdz GitHub Releases API przy starcie.
2. Jesli nowa wersja -> dialog "Dostepna aktualizacja X.Y. Zaktualizowac?"
3. Pobranie + zastapienie exe.

**Acceptance criteria:**
- [ ] Detekcja nowej wersji na GitHub
- [ ] Download + replace dziala
- [ ] Opcjonalnosc (user moze odmowic)

---

### M4-10: Installer (MSIX lub Inno Setup)
**Labels:** `medium` `infra`
**Zalezy od:** M4-01

**Do zrobienia:**
1. Wybierz: MSIX (modern, Windows Store ready) lub Inno Setup (klasyczny).
2. Ikona na pulpicie.
3. Start Menu entry.
4. Uninstaller.

**Acceptance criteria:**
- [ ] Installer tworzy skrot na pulpicie
- [ ] Deinstalacja czysta

---

### M4-11: Testy WPF
**Labels:** `high` `test`
**Zalezy od:** M4-04, M4-05

**Do zrobienia:**
1. ViewModel unit testy (NSubstitute dla IMediator).
2. Test: Patchuj flow (mock API + mock patch).
3. Test: Launch flow.

**Acceptance criteria:**
- [ ] ViewModele przetestowane
- [ ] Kluczowe flows pokryte

---

# M5: Community & Auth (pozniej)

### M5-01: Auth (OpenIddict) — Users + JWT + role
**Labels:** `low` `feature` — when needed

### M5-02: UserLanguageRoles — role per jezyk
**Labels:** `low` `feature` — when needed

### M5-03: Review workflow — submit -> review -> approve/reject
**Labels:** `low` `feature` — when needed

### M5-04: TranslationHistory z ChangedBy
**Labels:** `low` `feature` — when needed

### M5-05: AI review — LLM sprawdza placeholders, grammar, terminologie
**Labels:** `low` `feature` — when needed

### M5-06: Powiadomienia — Discord webhook
**Labels:** `low` `feature` — when needed

### M5-07: Public REST API
**Labels:** `low` `feature` — when needed

---

# Podsumowanie

## Statystyki

| Milestone | Tickety | Critical | High | Medium | Low |
|-----------|---------|----------|------|--------|-----|
| M1 | 21 | 2 | 15 | 3 | 1 |
| M2 | 20 | 1 | 13 | 5 | 1 |
| M3 | 15 | 0 | 6 | 7 | 2 |
| M4 | 11 | 0 | 7 | 3 | 1 |
| M5 | 7 | 0 | 0 | 0 | 7 |
| **Total** | **74** | **3** | **41** | **18** | **12** |

## Critical path

```
M1-01 (TFM split)
  ├── M1-02 (MediatR) ─┐
  │                     ├── M1-04..M1-06 (handlery)
  │                     ├── M1-09 (refaktor CLI) ──── M1-10 (cleanup)
  │                     │                      \
  │                     │                       └── M1-14 (update fix) ─┐
  │                     │                                               │
  ├── M1-13 (vnum) ────────────────────────────────────────────────────┤
  ├── M1-15 (protector) ──────────────────────────────────────────────┤
  ├── M1-16 (launcher) ───────────────────────────────────────────────┤
  │                                                                    │
  │                                                            M1-17 (launch cmd)
  │
  ├── M2-01 (docker) -> M2-03..M2-20 (cala baza)
  └── M2-02 (split infra: 3 projekty) -> M3-01 (Blazor)
```

**KLUCZOWA SEKWENCJA M1-14:** `M1-09 (refaktor CLI) -> M1-14 (update fix)`.
M1-14 zmienia zachowanie GameUpdateChecker (usuwa auto-save). Stary PreflightChecker polega na auto-save. Dlatego CLI MUSI byc na MediatR ZANIM zmienisz GameUpdateChecker.

**Wszystko zaczyna sie od M1-01.** Dopoki `Directory.Build.props` wymusza `net10.0-windows` x86 globalnie, nie ruszy ani baza, ani web app.

## Rownolegle sciezki (po M1-01)

```
Sciezka A: M1-02 -> M1-04..M1-06 -> M1-09 -> M1-10 + M1-14 (MediatR + refaktor CLI + update fix)
Sciezka B: M1-13, M1-15, M1-16 (nowe abstrakcje — rownolegle ze sciezka A)
           laczy sie z A na M1-17 (launch = potrzebuje MediatR + vnum + protector + launcher)
Sciezka C: M2-01..M2-06 (PostgreSQL setup — rownolegle z A i B)
Sciezka D: M2-02 (split infra na 3 projekty — rownolegle)
Sciezka E: M2-13 (|| escaping — mozna robic od razu po M1-01, nie wymaga DB)
Sciezka F: M2-15 (LOTRO Companion XML parser — zero zaleznosci, mozna zaczac od razu)
```
