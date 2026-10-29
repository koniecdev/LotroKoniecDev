# Baseline 49.8 — the state before Update 50 "The Wolves of Mordor"

**Captured:** 2026-10-28, 02:39–02:55 (Polish time), before the update reached the patch server
**Operator:** Claude, alone, on the owner's gaming PC (Windows 11, dual boot since 2026-10-21)
**Purpose:** the first **single** major update measured with the E5/E6/E7 tools. E7 (2026-10-10)
measured a cumulative update (47.2 → 49.7, two majors at once) and named this release as the next
real single-major measurement ([../e7-full-corpus-survival-2026-10-10.md](../e7-full-corpus-survival-2026-10-10.md),
"Limits").
**Results:** [RESULTS.md](RESULTS.md) (measured 2026-10-29)

## The install

| Attribute | Value |
|---|---|
| Path | `C:\Program Files (x86)\StandingStoneGames\The Lord of the Rings Online` |
| Installed | 2026-10-21 (Inno Setup `InstallDate`); the last patch run that day applied 2 `client_gamelogic` iterations (77264, 77265) |
| Launcher since then | never started (the English DAT's mtime stayed 2026-10-21 04:44:13, and the launcher writes it on every start, E1-F1) |
| Game version | **49.8** (Steam News: "Update 49.8 Release Notes", 2026-10-14; the CLI's forum fetch returned `49.8`) |

## English DAT (`client_local_English.dat`)

| Attribute | Value |
|---|---|
| Size | 1,910,505,472 B |
| LastWriteTime | 2026-10-21 04:44:13 |
| SHA256 | `0F8C7BB14C0A0ACCABD8A755E26F5A35ED84FF20FE1785354041DFA66518A3F4` |
| Backup | `intel/update-50/client_local_English.pre-u50.dat` (same hash) |
| vnum | 112 / 3 (written by `patch` into `data/last_known_game_version.txt`: `49.8\|112\|3\|…`) |

## Export (`export -d <backup>`, no elevation)

| Metric | Value |
|---|---|
| Text files (`0x25`) | **282,190** |
| Fragments | **806,120** |
| File size | 98,364,856 B (`intel/update-50/export-49.8.txt`) |
| Time | 34.6 s |

The counts equal the 2026-10-21 round-trip scan ([../subfile-round-trip-scan-2026-10-21.md](../subfile-round-trip-scan-2026-10-21.md)),
which read the same install.

## E5 snapshot

311,970 subfiles, 282,190 of them text. Read-only open + `GetSubfileSizes`: **141 ms**.
File: `intel/update-50/e5-pre-u50-49.8-*.csv`.

## The E7 corpus, prepared before the update

- Every one of the 806,120 rows becomes `[PL] ` + its own English, `approved = 1`, digest kept
  (`scripts/experiments/e7-corpus-survival.ps1 -Build`). No English row starts with `[PL]`.
- `patch` on a copy of the backup: **806,120 / 806,120 applied**, 0 skipped, 0 warnings.
- E5 of the marked copy against the clean backup: **iteration moved in 0 subfiles**; size moved in
  280,606 text subfiles. The launcher therefore still sees plain 49.8 (as in E7).
- The patch wrote the ledger and the Tier 0 iteration snapshot next to the corpus
  (`e7pl.txt.ledger`, `e7pl.txt.iterations`). Copies of both are kept, because the update tests
  in [RESULTS.md](RESULTS.md) need the state from before the update.

## The sandbox install: measuring an update with no elevation

The launcher asks for UAC only when it cannot open a file for write. Its binary says so:
`"Failed to open [%1] for write. Error [%2] - restarting launcher as elevated process."`. In
Program Files the user has read-only access (`BUILTIN\Users:(RX)`), so every start asks for
elevation (E4). A full copy of the install in the user's profile (46 GB,
`%USERPROFILE%\lotro-sandbox\LOTRO`, 1.5 min with robocopy) is writable, so a launcher started
from there patches the copy **with no UAC prompt**. Nobody has to click, and the live game is never
touched.

Negative control, 2026-10-28 02:53 (plain start, no update on the server):

- No `consent.exe`, no elevation. `PatchClient.log`: "No file patching necessary", "No data
  patching necessary", **0 iterations**. The launcher logs go to
  `%LOCALAPPDATA%\The Lord of the Rings Online\` (`LotroLauncher.log`, `PatchClient.log`,
  `TTEPatchClient.log`), shared by every install of the user.
- The every-start write (E1-F1) seen file by file: `TTEPatchClient` "commits forward patch data"
  into **all 31 game DAT files in turn, in 0.55 s**. Each one gets a 29-byte `<name>.jrnl` journal, four
  change events on the DAT, then the journal is deleted. Sizes do not move.
- E5 of the copy after the start against the baseline: **0 differences**.
- The launcher sets "the update interval to 4 hours" after a check.
