# LOTRO Polish Patcher - Project Context

## What is this?

CLI tool for injecting Polish translations into LOTRO (Lord of the Rings Online) game DAT files. Exports text from DAT files and patches them with translations.

## Tech Stack

- **Language:** C# 13, .NET 10.0 (`net10.0-windows`, x86)
- **DI:** Microsoft.Extensions.DependencyInjection
- **Testing:** xUnit 2.9.3 + FluentAssertions 8.0.0 + NSubstitute 5.3.0
- **Native interop:** datexport.dll (C++ library for DAT file I/O via P/Invoke)
- **Solution format:** Modern `.slnx`

## Build & Run

```bash
# Build
dotnet build src/LotroKoniecDev

# Run tests
dotnet test

# Run specific test project
dotnet test tests/LotroKoniecDev.Tests.Unit
dotnet test tests/LotroKoniecDev.Tests.Integration

# Run the tool
dotnet run --project src/LotroKoniecDev -- patch polish
dotnet run --project src/LotroKoniecDev -- export
```

Build output: `src/LotroKoniecDev/bin/Debug/net10.0-windows/LotroKoniecDev.exe`

## Architecture

Clean Architecture with 5 layers (dependency flows downward):

```
LotroKoniecDev (CLI / Presentation)
    -> LotroKoniecDev.Application (orchestration, abstractions)
        -> LotroKoniecDev.Domain (models, Result monad, errors)
    -> LotroKoniecDev.Infrastructure (DAT file I/O, system checks)
        -> LotroKoniecDev.Application (implements abstractions)
            -> LotroKoniecDev.Domain
LotroKoniecDev.Primitives (shared constants/enums, no dependencies)
```

## Project Structure

```
src/
  LotroKoniecDev/              CLI entry point (Program.cs)
  LotroKoniecDev.Application/  Business logic, interfaces, parsers
  LotroKoniecDev.Domain/       Core models, Result monad, errors, utilities
  LotroKoniecDev.Infrastructure/ DAT file P/Invoke, LOTRO discovery, system checks
  LotroKoniecDev.Primitives/   Constants (TextFileMarker=0x25), enums (DatFileSource, ErrorType)
tests/
  LotroKoniecDev.Tests.Unit/        Unit tests (~300 assertions)
  LotroKoniecDev.Tests.Integration/ Integration tests (~250 assertions)
translations/                  Translation input files (polish.txt, example_polish.txt)
data/                          Fallback DAT file location (gitignored)
```

## Key Patterns

### Result Monad (Railway-Oriented Programming)
All operations return `Result` or `Result<T>` instead of throwing exceptions. Use `IsSuccess`/`IsFailure`, `Value`, `Error`. Extensions: `Map()`, `Bind()`, `OnSuccess()`, `OnFailure()`, `Match()`.

```csharp
Result<PatchSummary> result = patcher.ApplyTranslations(...);
if (result.IsFailure) { /* handle result.Error */ }
PatchSummary summary = result.Value;
```

### Error Handling
Centralized in `DomainErrors` static class with categories: `DatFile`, `SubFile`, `Fragment`, `Translation`, `Export`, `Backup`, `DatFileLocation`. Each returns typed `Error` instances via factory methods.

### DI Registration
- `services.AddApplicationServices()` - Parser (Singleton), Exporter/Patcher (Scoped)
- `services.AddInfrastructureServices()` - DatFileHandler (Scoped), Locator/Detector/Checker (Singleton)

### Native Interop
`DatExportNative` class wraps datexport.dll via `[DllImport]`. `DatFileHandler` manages IntPtr handles, Marshal memory, and IDisposable cleanup with thread-safe locking.

## CLI Commands

- `export [dat_file] [output.txt]` - Export all texts from DAT to file
- `patch <name> [dat_file]` - Apply translations (name resolves to `translations/<name>.txt`)
- Auto-detects LOTRO installation if no DAT path given
- Exit codes: 0=Success, 1=InvalidArgs, 2=FileNotFound, 3=OperationFailed

**Planned (M1):** `launch` command — protects DAT with `attrib +R`, starts TurbineLauncher, restores after close. Currently implemented as `lotro.bat`.

## BAT Workflow (current)

- `export.bat` - builds + runs export
- `patch.bat <name>` - builds + runs patch (requests admin)
- `lotro.bat [path]` - protects DAT (attrib +R), launches game, restores (attrib -R)

## Game Update Detection

Two-source model:
1. **Forum checker** (proactive): scrapes lotro.com release notes, regex `Update\s+(\d+(?:\.\d+)*)\s+Release\s+Notes`
2. **DAT vnum** (confirmation): `OpenDatFileEx2()` returns `vnumDatFile`/`vnumGameData` — currently discarded in `DatFileHandler.Open()`, needs exposing

**Known bug**: `GameUpdateChecker` saves forum version immediately on detection (not after user actually updates game). DAT vnum needed to confirm update was installed.

## DAT File Protection

`lotro.bat` uses `attrib +R` on `client_local_English.dat` — OS-level read-only that prevents the official LOTRO launcher from overwriting translations. Stronger than the Russian project's `-disablePatch` flag approach. Intentionally also blocks game updates (forces explicit update workflow).

## Code Style

Enforced via `.editorconfig`:
- File-scoped namespaces (`namespace Foo;`)
- `var` when type is apparent, explicit types for built-in types
- Private fields: `_camelCase`
- Types/members: `PascalCase`
- Interfaces: `IPrefix`
- Braces on new lines (Allman style)
- Expression-bodied members when single line

## Translation File Format

```
# Comments start with #
file_id||gossip_id||translated_text||args_order||args_id||approved
620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1
```

Separator in text content: `<--DO_NOT_TOUCH!-->` (marks argument placeholders).

## Key Abstractions (interfaces in Application/Abstractions/)

- `IDatFileHandler` - Open/Read/Write/Flush/Close DAT files
- `IDatFileLocator` - Auto-detect LOTRO installations
- `IExporter` - Export texts from DAT
- `IPatcher` - Apply translations to DAT
- `ITranslationParser` - Parse translation files
- `IGameProcessDetector` - Check if LOTRO is running
- `IWriteAccessChecker` - Verify write permissions
- `IGameUpdateChecker` - Check for game updates via forum scraping
- `IForumPageFetcher` - Fetch LOTRO release notes page
- `IVersionFileStore` - Read/write last known game version to file

**Planned abstractions (M1):**
- `IDatVersionReader` - Read vnumDatFile/vnumGameData from DAT (confirm actual update)
- `IDatFileProtector` - attrib +R/-R on DAT file
- `IGameLauncher` - Start TurbineLauncher.exe with flags

## Global Build Config

`Directory.Build.props` applies to all projects: .NET 10.0, x86, nullable refs, latest C# lang, code style enforcement.
`Directory.Packages.props` centralizes NuGet versions.

**Known issue**: `Directory.Build.props` forces `net10.0-windows` + `x86` globally. Must be changed to per-project for Web App (AnyCPU) to work. This is issue #1 and blocks M2+M3.

## Native Interop Details

`OpenDatFileEx2()` returns version info via out params that are currently discarded:
- `out int vnumDatFile` - DAT file format version
- `out int vnumGameData` - game data version (changes on game updates)

These should be exposed via `IDatVersionReader` for game update confirmation.

## Target Architecture (post M4)

Three presentation layers, zero code duplication:
- **CLI** (`export`, `patch`, `launch`) — power users, CI/CD, automation
- **Blazor SSR Web App** — translation platform for translators (glossary, review, side-by-side EN/PL)
- **WPF Desktop App** (`LotroPoPolsku.exe`) — end-user GUI for gamers (patch + play one click)

All three share the same MediatR handlers via `IMediator.Send()`.

## Reference: Russian LOTRO Translation Project

Our project shares DNA with translate.lotros.ru (same datexport.dll, same 0x25 marker, same format).
See `docs/PROJECT_PLAN.md` for detailed comparison. Key differences:
- We use `attrib +R` (OS-level protection) vs their `-disablePatch` flag
- We detect updates proactively (forum) vs their reactive NinjaMark
- We have Clean Architecture vs their monolith
- We have 3 presentation layers (CLI + Web + WPF) vs their 2 (launcher + web)
