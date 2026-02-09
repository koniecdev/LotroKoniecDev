# Application Layer

Business logic orchestration. Depends on Domain only. Defines abstractions that Infrastructure implements.

## Structure

```
Abstractions/
  IDatFileHandler.cs      Open, GetSubfileData, PutSubfileData, Flush, Close
  IDatFileLocator.cs      LocateAll() -> DatFileLocation records (path + source + display name)
  IExporter.cs            ExportAllTexts() -> Result<ExportSummary>
  IPatcher.cs             ApplyTranslations() -> Result<PatchSummary>
  ITranslationParser.cs   ParseFile(), ParseLine()
  IGameProcessDetector.cs IsLotroRunning() -> bool
  IWriteAccessChecker.cs  CanWriteTo() -> bool
Features/
  Export/
    Exporter.cs           Exports all text fragments from DAT. Filters by TextFileMarker (0x25 high byte)
  Patch/
    Patcher.cs            Batch-loads subfiles by FileId, applies translations, handles arg reordering
Parsers/
  TranslationFileParser.cs  Parses || delimited format, handles comments (#), escaping, 1->0 indexed args
Extensions/
  ApplicationDependencyInjection.cs   AddApplicationServices(): Parser=Singleton, Exporter/Patcher=Scoped
```

## DI Registration

```csharp
services.AddApplicationServices();
// Registers: ITranslationParser(Singleton), IExporter(Scoped), IPatcher(Scoped), IGameUpdateChecker(Singleton)
```

## Game Update Detection

`GameUpdateChecker` scrapes LOTRO forum for release notes, compares with stored version.
**Known bug**: Saves forum version immediately on detection (line 56-58), not after user actually updates.
Needs DAT vnum confirmation before saving. See `IDatVersionReader` (planned M1 #14).

## Translation File Format

```
file_id||gossip_id||content||args_order||args_id||approved
```

- Lines starting with `#` are comments
- Empty lines are skipped
- `\r` and `\n` in content are unescaped
- Args arrays are converted from 1-indexed (file) to 0-indexed (internal)
- Results sorted by FileId then GossipId for sequential DAT I/O

## Patching Flow

1. Parse translation file -> sorted `List<Translation>`
2. Open DAT file via `IDatFileHandler`
3. Group translations by FileId
4. For each group: load SubFile, find Fragment by GossipId, replace pieces, save SubFile
5. Flush DAT handle
6. Return `PatchSummary` (applied, skipped, warnings)
