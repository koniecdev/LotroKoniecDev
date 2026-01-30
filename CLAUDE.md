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

## Global Build Config

`Directory.Build.props` applies to all projects: .NET 10.0, x86, nullable refs, latest C# lang, code style enforcement.
`Directory.Packages.props` centralizes NuGet versions.
