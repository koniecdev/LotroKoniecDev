# CLI Entry Point

Console application. Depends on Application and Infrastructure layers.

## Key File

`Program.cs` (~370 lines) - Entire CLI logic in a single static class.

## Commands

```
LotroKoniecDev export [dat_file] [output.txt]    Export all texts from DAT
LotroKoniecDev patch <name> [dat_file]            Patch DAT with translations
```

`<name>` resolves to `translations/<name>.txt` unless it contains path separators or `.txt` extension.

## Exit Codes (ExitCodes static class)

- `0` Success
- `1` InvalidArguments
- `2` FileNotFound
- `3` OperationFailed

## Flow

1. Parse args -> determine command
2. Build DI container (`AddApplicationServices()` + `AddInfrastructureServices()`)
3. Resolve DAT path: explicit arg > auto-detect via `IDatFileLocator` > user choice prompt
4. Pre-flight checks (patch only): game running? write access? create backup
5. Execute via DI-resolved `IExporter` or `IPatcher`
6. Print summary, return exit code
7. On patch failure: auto-restore from `.backup`

## Console Output Helpers

- `WriteInfo()` - default color
- `WriteSuccess()` - green
- `WriteWarning()` - yellow with "WARN:" prefix
- `WriteError()` - red with "ERROR:" prefix
- `WriteProgress()` - carriage-return overwrite (progress bar style)
