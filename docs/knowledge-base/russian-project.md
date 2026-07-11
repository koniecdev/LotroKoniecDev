# Russian LOTRO Translation Project - Deep Analysis

> Distilled digest of the full raw research record in
> [`../RUSSIAN_PROJECT_RESEARCH.md`](../RUSSIAN_PROJECT_RESEARCH.md) (dated 2026-02-09) — start
> here; go to the raw record for timelines, API endpoint reconstructions, and source links.

## Components

| Component | Role | Tech |
|---|---|---|
| translate.lotros.ru | Web platform for collaborative translation | Web app (server-side unknown) |
| Elanor / Nasledie (Legacy) | Desktop launcher, downloads and applies patches | C# WPF, datexport.dll |
| Xavian | DAT extractor → SQLite databases | C# console, datexport.dll, SQLite |
| LotroDat | Standalone C++ library for DAT I/O | C++, git.endevir.ru |
| Mojo | Font converter | C# |
| Jozo | Pre-launcher / self-updater for Elanor | C# WinForms |

## Full Pipeline

```
Game update → Xavian extracts DAT → SQLite DB
  → Upload to translate.lotros.ru
    → Translators translate in browser (register at /auth/register)
      → Reviewers approve (mandatory review, must check Mail.ru baseline)
        → Export → SQLite patch .db files (per content type)
          → Elanor launcher downloads .db → applies to DAT
```

## DAT Files Managed

- client_local_English.dat (ID 0) - PRIMARY target for text
- client_general.dat (ID 1)
- client_sound.dat (ID 2)
- client_surface.dat (ID 3)
- client_highres.dat (ID 4)

## Xavian SQLite Schema

```sql
CREATE TABLE text_data (file_id INTEGER, gossip_id INTEGER, content TEXT, args TEXT, dat_id INTEGER, PRIMARY KEY (file_id, gossip_id));
CREATE TABLE bin_data (file_id INTEGER, data BLOB, dat_id INTEGER);
CREATE TABLE patch_metadata (name TEXT, description TEXT, date TEXT, author TEXT, version TEXT, link TEXT, content_type TEXT);
```

## Patch Content Types

Sound, Image, Font, Text, Video, Texture, Loadscreen, Undef - each independently versioned.

## NinjaMark System

- Stored in DAT subfile ID `620750000`
- Format: `Ru&{version}&{date}&{subscribed}`
- Also stores TurbineLauncher.exe version (via FileVersionInfo.GetVersionInfo)
- Detection: if NinjaMark missing or launcher version mismatch → re-patch all

## Elanor Launcher Flags

```
TurbineLauncher.exe -nosplash -disablePatch -skiprawdownload
```
- `-disablePatch` = DON'T check for/apply game updates (keeps translations intact)
- `-nosplash` = skip splash screen
- `-skiprawdownload` = skip ad/splash downloads

## User Workflow

Normal day: Jozo → updates Elanor → Elanor reads NinjaMark → checks for new patches → applies → launches game with -disablePatch

After game update: User runs official launcher normally → DAT updated → translations wiped → runs Elanor → detects mismatch → re-patches everything

## Web Platform Features

- Open registration
- Translation submission + review/approval workflow
- Mandatory terminology checking (Mail.ru baseline)
- Style guide (informal "you" in dialogs, formal in descriptions)
- Bug tracking
- Guides for translators
- Statistics / progress tracking

## Key Insight: Communication Model

Desktop ↔ Server is INDIRECT and file-based:
- Launcher downloads pre-built SQLite .db files
- No real-time translation API
- Web platform = translation management + patch build pipeline
- Launcher = download + apply + launch with flags

## Sources

- https://github.com/Endevir/Elanor
- http://translate.lotros.ru/
- http://translate.lotros.ru/pages/translate-rules.html
- https://git.endevir.ru/LotRO_Legacy
