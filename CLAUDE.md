# LOTRO Polish Patcher

CLI tool for injecting Polish translations into LOTRO (Lord of the Rings Online) game DAT files.
Exports text from DAT files, patches them with translations.
Part of a larger platform: CLI + Web App (Blazor SSR) + Desktop App (WPF).

## Build & Run

```bash
dotnet build src/LotroKoniecDev             # Build CLI
dotnet test                                  # All tests (~550 assertions)
dotnet test tests/LotroKoniecDev.Tests.Unit
dotnet test tests/LotroKoniecDev.Tests.Integration

dotnet run --project src/LotroKoniecDev -- export
dotnet run --project src/LotroKoniecDev -- patch polish
```

Output: `src/LotroKoniecDev/bin/Debug/net10.0-windows/LotroKoniecDev.exe`

## Tech Stack

- C# 13, .NET 10.0, modern `.slnx` solution
- DI: Microsoft.Extensions.DependencyInjection
- Testing: xUnit 2.9.3 + FluentAssertions 8.0.0 + NSubstitute 5.3.0
- Native interop: datexport.dll (Turbine C++ library for DAT I/O via P/Invoke)
- DB (planned): PostgreSQL + EF Core + Npgsql
- Web (planned): Blazor SSR
- Desktop (planned): WPF

## Architecture

Clean Architecture, 5 layers (dependency flows downward):

```
CLI (Presentation, net10.0-windows x86)
  -> Application (orchestration, abstractions, net10.0 AnyCPU)
    -> Domain (models, Result monad, errors, net10.0 AnyCPU)
  -> Infrastructure (current: single LotroKoniecDev.Infrastructure project)
      - DatFile (P/Invoke, net10.0-windows x86, existing)
      - Persistence (EF Core, PostgreSQL — planned, net10.0 AnyCPU)
    -> Application
Primitives (constants, enums, net10.0 AnyCPU, zero dependencies)
```

Target: 3 presentation layers sharing MediatR handlers (zero duplication):
- **CLI** — power users, CI/CD (`export`, `patch`, `launch`)
- **Blazor SSR Web App** — translation platform for translators
- **WPF Desktop App** (`LotroPoPolsku.exe`) — one-click patch+play for gamers

## Project Structure

```
src/
  LotroKoniecDev/                 CLI entry point (Program.cs, ~80 lines)
    Commands/                     ExportCommand, PatchCommand, PreflightChecker, BackupManager
    ConsoleWriter.cs              Colored output (Info/Success/Warning/Error/Progress)
    DatPathResolver.cs            Auto-detect or prompt for LOTRO install path
    ExitCodes.cs                  0=Success, 1=InvalidArgs, 2=FileNotFound, 3=OperationFailed
  LotroKoniecDev.Application/
    Abstractions/                 IDatFileHandler, IDatFileLocator, IExporter, IPatcher, etc.
    Features/Export/              Exporter (exports text fragments from DAT)
    Features/Patch/               Patcher (batch-applies translations to DAT)
    Features/UpdateCheck/         GameUpdateChecker (forum scraping + version file)
    Parsers/                      TranslationFileParser (|| delimited format)
    Extensions/                   DI registration, DatFileHandler helpers
  LotroKoniecDev.Domain/
    Core/BuildingBlocks/          Error (sealed), ValueObject (abstract)
    Core/Monads/                  Result, Result<T>, Maybe<T>
    Core/Extensions/              ResultExtensions (Map, Bind, OnSuccess, OnFailure, Match)
    Core/Utilities/               VarLenEncoder (1-2 byte variable-length int encoding)
    Core/Errors/                  DomainErrors — static factories per domain area
    Models/                       Fragment, SubFile, Translation
  LotroKoniecDev.Infrastructure/
    DatFile/                      DatExportNative (P/Invoke), DatFileHandler (thread-safe)
    Discovery/                    DatFileLocator (SSG, Steam, Registry, disk scan, local fallback)
    Diagnostics/                  GameProcessDetector, WriteAccessChecker
    Network/                      ForumPageFetcher
    Storage/                      VersionFileStore
  LotroKoniecDev.Primitives/
    Constants/                    TextFileMarker=0x25, PieceSeparator="<--DO_NOT_TOUCH!-->"
    Enums/                        DatFileSource, ErrorType
tests/
  LotroKoniecDev.Tests.Unit/     ~300 assertions (Result, Fragment, SubFile, Parser, VarLen, Error)
  LotroKoniecDev.Tests.Integration/ ~250 assertions (full DI stack, Exporter, Patcher workflows)
translations/                    polish.txt, example_polish.txt
data/                            Fallback DAT location (gitignored)
docs/
  PROJECT_PLAN.md                Full roadmap with milestones, step-by-step execution guide
  RUSSIAN_PROJECT_RESEARCH.md    Research on translate.lotros.ru (reference project)
```

## Key Patterns

### Result Monad (Railway-Oriented Programming)
All operations return `Result` or `Result<T>`, never throw for domain errors.
```csharp
Result<PatchSummary> result = patcher.ApplyTranslations(...);
result.Match(
    onSuccess: summary => /* ... */,
    onFailure: error => /* ... */);
```
Extensions: `Map()`, `Bind()`, `OnSuccess()`, `OnFailure()`, `Match()`, `Combine()`.

### Error Handling
`DomainErrors` static class with categories: `DatFile`, `SubFile`, `Fragment`, `Translation`, `Export`, `Backup`, `DatFileLocation`, `GameUpdateCheck`. Each returns typed `Error(Code, Message, Type)`.

### DI Registration
```csharp
services.AddApplicationServices();       // Parser(Singleton), Exporter/Patcher(Scoped), UpdateChecker(Singleton)
services.AddInfrastructureServices();     // DatFileHandler(Scoped), Locator/Detector/Checker(Singleton)
```

### Native Interop (datexport.dll)
`DatExportNative` — `[LibraryImport]` with `CallConvCdecl`. Key functions:
- `OpenDatFileEx2(handle, path, flags, out vnumDatFile, out vnumGameData, ...)` → handle
- `GetNumSubfiles`, `GetSubfileSizes`, `GetSubfileData`, `PutSubfileData`, `Flush`, `CloseDatFile`

`DatFileHandler` — thread-safe (`lock`), `Marshal.AllocHGlobal`/`FreeHGlobal`, tracks handles in `HashSet<int>`.

**Known bug:** `DatFileHandler.Open()` discards `vnumDatFile`/`vnumGameData` (`out _`). Needs `IDatVersionReader` (M1 #13).

## DAT Binary Format

```
SubFile (text, FileId high byte = 0x25):
  FileId (4B) | Unknown1 (4B) | Unknown2 (1B) | FragCount (VarLen)
  Fragment[]:
    FragmentId (8B ulong = GossipId) | PieceCount (int)
    Piece[]: VarLen length + UTF-16LE bytes
    ArgRefCount (int) | ArgRef[]: 4B each
    ArgStringGroupCount (byte) | Group[]: Count(int) + VarLen UTF-16LE strings

VarLen: 0-127 = 1 byte; 128-32767 = 2 bytes (high bit flag)
```

## Translation File Format

```
# Comments start with #
file_id||gossip_id||translated_text||args_order||args_id||approved
620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1
620756992||1002||Tekst z <--DO_NOT_TOUCH!--> argumentem||1||1||1
```

- `<--DO_NOT_TOUCH!-->` = argument placeholder
- `args_order`: `NULL` or `1-2-3` (1-indexed in file, 0-indexed internally)
- `\r`, `\n` in content are unescaped by parser
- Results sorted by FileId then GossipId for sequential DAT I/O

## CLI Commands

```
LotroKoniecDev export [dat_file] [output.txt]    # Export texts from DAT
LotroKoniecDev patch <name> [dat_file]            # Patch DAT (<name> -> translations/<name>.txt)
```

Planned (M1): `launch` — attrib +R -> TurbineLauncher -> attrib -R.

Auto-detects LOTRO: SSG default -> Steam -> Registry -> disk scan -> local fallback.
Pre-flight: checks game running, write access, creates `.backup`.

## LOTRO Companion Integration (planned M2)

https://github.com/LotroCompanion/lotro-data — XML data for quests, deeds, NPCs, items, etc.
Uses format `key:{file_id}:{gossip_id}` — IDs match 1:1 with our DAT export.

Provides **context** for each translatable string:
- Quest name, description, bestower text, objectives, dialogs
- Deed name/description, NPC dialogs, skill names, titles, items

This goes into `TextContexts` table so translators see context instead of raw IDs.

## Database Schema (planned M2)

PostgreSQL (Docker). Two data sources:
1. DAT export -> `ExportedTexts` (actual English text)
2. LOTRO Companion XML -> `TextContexts` (what each string IS)

```sql
Languages (Code PK, Name, IsActive)
ExportedTexts (Id, FileId, GossipId, EnglishContent, ImportedAt)
TextContexts (Id, FileId, GossipId, ContextType, ParentName, ParentCategory, ParentLevel, NpcName, Region, SourceFile, ImportedAt)
Translations (Id, FileId, GossipId, LanguageCode FK, Content, ArgsOrder, ArgsId, IsApproved, Notes, CreatedAt, UpdatedAt)
TranslationHistory (TranslationId, OldContent, NewContent, ChangedAt)
GlossaryTerms (Id, EnglishTerm, PolishTerm, Notes, Category, CreatedAt)
DatVersions (Id, VnumDatFile, VnumGameData, ForumVersion, DetectedAt)
```

Two `Translation` models:
- `Domain.Models.Translation` — init-only DTO for DAT pipeline (no DB deps)
- `Persistence.Entities.TranslationEntity` — EF Core entity (timestamps, LanguageCode)

## Game Update Detection

Two-source model:
1. **Forum checker** (proactive): scrapes lotro.com release notes, regex `Update\s+(\d+(?:\.\d+)*)\s+Release\s+Notes`
2. **DAT vnum** (confirmation): `OpenDatFileEx2()` -> `vnumGameData` (currently discarded — M1 #13-14)

**Known bug:** `GameUpdateChecker` saves forum version immediately (should wait for DAT vnum confirmation).

## DAT File Protection

`attrib +R` on `client_local_English.dat` — OS-level read-only, launcher cannot bypass.
Stronger than Russian project's `-disablePatch` flag. Intentionally blocks game updates too (forces explicit update workflow). Currently in `lotro.bat`, moving to C# in M1.

## Code Style (.editorconfig)

- File-scoped namespaces, Allman braces
- `var` when type apparent, explicit for built-in types
- `_camelCase` fields, `PascalCase` types/members, `IPrefix` interfaces
- Expression-bodied members when single line
- FluentAssertions only (no raw `Assert.*`)
- Test naming: `MethodName_Scenario_ExpectedResult`

## Build Config

- `Directory.Build.props`: global settings (nullable, latest C#, code style enforcement)
- `Directory.Packages.props`: centralized NuGet versions
- **Known issue (#1):** `Directory.Build.props` forces `net10.0-windows` + `x86` globally. Must split to per-project TFMs for Web App (AnyCPU). Blocks M2+M3.

## Key Interfaces (Application/Abstractions/)

| Interface | Purpose | Lifetime |
|-----------|---------|----------|
| `IDatFileHandler` | Open/Read/Write/Flush/Close DAT files | Scoped |
| `IDatFileLocator` | Auto-detect LOTRO installations | Singleton |
| `IExporter` | Export texts from DAT | Scoped |
| `IPatcher` | Apply translations to DAT | Scoped |
| `ITranslationParser` | Parse translation files | Singleton |
| `IGameProcessDetector` | Check if LOTRO is running | Singleton |
| `IWriteAccessChecker` | Verify write permissions | Singleton |
| `IGameUpdateChecker` | Forum scraping for updates | Singleton |
| `IForumPageFetcher` | HTTP GET release notes | Singleton |
| `IVersionFileStore` | Read/write version to file | Singleton |

Planned (M1): `IDatVersionReader`, `IDatFileProtector`, `IGameLauncher`

## Reference: Russian Project (translate.lotros.ru)

Our project shares DNA: same datexport.dll, same 0x25 marker, same `||` format, same `<--DO_NOT_TOUCH!-->`.
See `docs/RUSSIAN_PROJECT_RESEARCH.md` for full analysis.

Key differences:
- We use `attrib +R` (OS-level) vs their `-disablePatch` flag
- We have Clean Architecture + Result monad + ~550 tests vs their monolith + no tests
- We have proactive update detection (forum+vnum) vs their reactive (file change tracking)
- They patch 7 types (text/font/image/sound/texture/loadscreen/video), we patch text only
- They built their own C++ DAT library (LotroDat), we use datexport.dll
- Polish diacritics work natively in LOTRO (no FontRestorator needed, unlike Cyrillic)

## Roadmap

See `docs/PROJECT_PLAN.md` for full plan with step-by-step execution guide.

```
M1: #1-#21   CLI cleanup (TFM split, MediatR, launch command, update fix) — 4 phases
M2: #22-#43  Database (PostgreSQL, EF Core, LOTRO Companion import, glossary) — 4 phases
M3: #44-#62  Web App (Blazor SSR, translation UI with context)
M4: #63-#75  Desktop App (WPF, LotroPoPolsku.exe, one-click patch+play)
M5: #76-#83  Community & Auth (OpenIddict, roles, review workflow)
```

Critical path: #1 (TFM split) blocks M2+M3. M2 can run in parallel with M1 Faza C/D after #1.
