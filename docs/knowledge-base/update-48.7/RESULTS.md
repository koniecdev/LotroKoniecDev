# Update 48.0 → 48.7 — Test Results

**Data:** 2026-06-25
**Operator:** Claude (automated) + user (in-game verification + update trigger)
**Status:** ✅ **ALL TESTS PASSED — simplified flow validated across a second live update (SKIP-path branch)**
**Redaction note:** quest-text content in this folder redacted to synthetic equivalents 2026-07 (LEGAL-08); the empirical result is unaffected.

## Verdict

**Translacje przetrwały update 48.0 → 48.7 bez żadnej interwencji.** Wszystkie 4 niezależne kanały
dowodowe potwierdzają survival:

1. ✅ **In-game (user manual):** „w grze as usual polskie tłumaczenia istnieją" — polski działa na 48.7
2. ✅ **Export survival:** 8/8 polish par FileId||GossipId obecne w `export-48.7.txt`
3. ✅ **Diff stability:** 0 matchów polish entries w 204 hunkach diffa (niezmienione bajtowo)
4. ✅ **Launch log:** SKIP path (hash match) → fire-and-forget → launcher zaaplikował 48.7 → survival

**Nowość względem testu kwietniowego (48.0):** ten przebieg poszedł **SKIP path** (translacje już
rezydowały w DAT od kwietnia, hash polish.txt == stored hash → launch tylko odpalił launcher, bez
re-patchu). Kwiecień szedł PATCH path (8 applied). **Oba branche simplified flow są teraz
zwalidowane przez żywy update.**

## Intel summary

### DAT state comparison

| Metric | 48.0 (baseline) | 48.7 (post-update) | Delta |
|--------|-----------------|--------------------|-------|
| Size (B) | 1,892,759,280 | 1,893,807,856 | **+1,048,576 (+0.055%)** |
| SHA256 | `833C22DE…1826` | `620DA8FD…637A` | Changed |
| LastWriteTime | 2026-06-25 21:52:29 | 2026-06-25 23:06:28 | — |
| TotalTextFiles | 278,377 | 278,978 | **+601 (+0.22%)** |
| TotalFragments | 792,500 | 793,659 | **+1,159 (+0.146%)** |
| Export output size | 82,469,439 B | 82,556,356 B | +86,917 B |

**Obserwacja — DAT urósł o DOKŁADNIE +1 MiB (1,048,576 B).** Treść (export) urosła tylko o ~87 KB,
ale plik DAT urósł o równo jeden blok 1 MiB. Spójne z modelem **chunk-based** (`dat-protection.md`):
launcher dołożył jeden 1 MiB blok alokacji na nowe +601 plików / +1,159 fragmentów; istniejące
chunki (w tym nasze translacje) pozostały nietknięte.

### Diff summary (export-48.0.txt vs export-48.7.txt)

- Diff file: 131,646 B (`diff-48.0-vs-48.7.txt`), **204 change hunks**
- **191 linii usuniętych** (reworded/edited fragments)
- **1,350 linii dodanych** (new + reworded fragments; net +1,159 = dokładnie TotalFragments delta)
- **0 matchów polish par** w liniach +/- diffa → wszystkie 8 translacji bajtowo identyczne między 48.0 a 48.7

### Survival — 8/8 polish pairs (export-48.7.txt)

| # | FileId | GossipId | Content | 48.0 line → 48.7 line |
|---|--------|----------|---------|----------------------|
| 1 | 620871150 | 218649169 | `'Mamy znak…Szare ćmy wznoszą się rzędem` | 255033 → 255044 |
| 2 | 620759036 | 218649169 | `'PL - We cannot let the old warden…` | 19821 → 19821 |
| 3 | 620757435 | 225138404 | `Wejdź do Śródziemia` | 7063 → 7063 |
| 4 | 620757027 | 9795381 | `Kliknij tutaj aby wybrać swój tytuł` | 3798 → 3798 |
| 5 | 620861331 | 228870261 | `Ekwipunek` | 227819 → 227830 |
| 6 | 620757027 | 29271026 | `Podstawowe statystyki` | 3879 → 3879 |
| 7 | 620757027 | 103943794 | `Pokaż wszystkie` | 3681 → 3681 |
| 8 | 620757027 | 85383154 | `Stylizacje` | 3765 → 3765 |

Pary 5 i 1 przesunięte o +11 linii (nowa treść 48.7 wstawiona przed nimi) — sama treść bajtowo identyczna.

### Vnum observations — **4. cykl z rzędu bez zmiany**

```
Before 48.7:  VnumDatFile=112, VnumGameData=3
After 48.7:   VnumDatFile=112, VnumGameData=3  ← UNCHANGED
```

45.x → 47.x → 48.0 → **48.7**: vnum 112/3 stałe przez 4 cykle. Hipoteza „vnum = schema, NOT content"
trzyma się ponad-wymiarowo. Vnum-triggered logic = martwa na zawsze.

### Forum version — niezależne potwierdzenie „48.7"

Preflight forum-fetcher (regex `Update\s+(\d+(?:\.\d+)*)\s+Release\s+Notes` na lotro.com) zwrócił
**ForumVersion = 48.7** podczas WRITE-path testu (SaveBaseline zapisał `48.7|112|3|<hash>`). Potwierdza
że update to faktycznie 48.7. Forum version pozostaje wiarygodnym identyfikatorem treści (vs martwy vnum).

### datexport.dll compatibility (48.7 schema)

- ✅ **READ path (export) na 48.7 DAT:** 2.8s, 793,659 fragments, zero errors
- ✅ **WRITE path (patch) na 48.7 DAT:** 8/8 applied, 0 skipped, 5.1s, zero warnings
  - write-test DAT: `1655B46D…5C5A`, size 1,893,807,856 (size-neutral re-patch), backup auto-created
- Schema niezmieniona (vnum 112/3); pełna kompatybilność wsteczna READ+WRITE.

### Launch log timeline (today)

```
23:05:58 [INF] === SIMPLIFIED LAUNCH START ===
23:05:58 [INF] Current translation hash: 40b613a2…ed3d6
23:05:58 [INF] Stored info: ForumVersion=48, VnumDat=112, VnumGame=3, Hash=40b613a2…ed3d6
23:05:58 [INF] Translation changed? false (match=true)
23:05:58 [INF] >>> SKIP: Translation file unchanged — skipping patch
23:05:59 [INF] Launcher started OK (fire-and-forget, not waiting for exit)
23:05:59 [INF] === SIMPLIFIED LAUNCH END ===
~23:06   official launcher downloaded & applied 48.7
23:06:28 DAT mtime set (launcher finished write, +1 MiB)
```

User uruchomił `lotro.bat polish --skip-sync` → SKIP path (translacje już zaaplikowane) →
fire-and-forget → oficjalny launcher zaaplikował 48.7 (~30 s później) → polski przeżył.

## Kluczowe odkrycia / memory worthy

1. **Drugi żywy update przeżyty — teraz SKIP path.** Kwiecień zwalidował PATCH-path-then-update;
   czerwiec waliduje SKIP-path-then-update (steady state: translacje rezydują, hash match, launch tylko
   odpala launcher). Oba branche simplified flow potwierdzone przez prawdziwy update.
2. **Translacje rezydują przez WIELE updatów bez re-patchu.** Live DAT przed 48.7 był bajtowo
   identyczny ze stanem „48.0 + nasze translacje" z kwietnia (hash `833C22DE…1826`) — translacje
   przetrwały nietknięte od 2026-04-23 do 2026-06-25 (cały cykl 48.x), zero interwencji.
3. **+1 MiB DAT growth = granularność alokacji.** Wzmacnia chunk-based model: nowa treść = nowy blok,
   stare chunki (translacje) nietknięte → byte-for-byte survival.
4. **Vnum 4. cykl bez ruchu; forum-fetcher potwierdza „48.7".** Vnum martwy jako content-indicator;
   forum version wiarygodny.

## Pliki intel w tym folderze

```
BASELINE.md                               — pre-update 48.0 snapshot report
RESULTS.md                                — ten plik
client_local_English.48.0.dat             — pre-update DAT backup (1.76 GB, hash 833c22de…)
client_local_English.48.7.dat             — post-update DAT backup (1.76 GB, hash 620da8fd…)
client_local_English.48.7.write-test.dat  — WRITE path test result (patched 48.7, hash 1655b46d…)
client_local_English.48.7.write-test.dat.backup — auto-backup created by patch command
export-48.0.txt                           — pre-update full export (78.6 MB, 792,500 fragments)
export-48.7.txt                           — post-update full export (78.7 MB, 793,659 fragments)
diff-48.0-vs-48.7.txt                      — full unified diff (131 KB, 204 hunks; repo copy removed 2026-07 — verbatim game text, LEGAL-08; stats preserved above)
polish-pre-48.7.txt                        — polish.txt snapshot (hash 40b613a2…)
version-file-pre-48.7.txt                  — "48|112|3|<hash>" (pre-update, restored after WRITE-test)
```
