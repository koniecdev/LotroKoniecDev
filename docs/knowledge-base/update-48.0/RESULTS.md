# Update 47.2 → 48.0 — Test Results

**Data:** 2026-04-23
**Operator:** Claude (automated) + user (in-game verification + update trigger)
**Status:** ✅ **ALL TESTS PASSED — simplified flow fully validated across first major update**

## Verdict

**Simplified flow survived first major update bez żadnych modyfikacji.** Wszystkie 4 niezależne kanały dowodowe potwierdzają survival:

1. ✅ **In-game (user manual):** polski działa na 48.0
2. ✅ **Export survival:** 8/8 polish par FileId||GossipId obecne w export-48.0.txt
3. ✅ **Diff stability:** 0 matchów polish entries w 587 hunkach diffa (niezmienione bajtowo)
4. ✅ **Launch log:** patch 8/8 applied → fire-and-forget → launcher applied 48.0 → survival

## Intel summary

### DAT state comparison

| Metric | 47.2 | 48.0 | Delta |
|--------|------|------|-------|
| Size (B) | 1,878,079,216 | 1,892,759,280 | **+14,680,064 (+0.78%)** |
| SHA256 | `AABEC3AA...6992` | `833C22DE...1826` | Changed |
| LastWriteTime | 2026-04-06 20:57:28 | 2026-04-23 12:39:23 | — |
| TotalTextFiles | 277,082 | 278,377 | **+1,295 (+0.47%)** |
| TotalFragments | 788,269 | 792,500 | **+4,231 (+0.54%)** |
| Export output size | 81.9 MB | 82.5 MB | +532 KB |

### Diff summary (export-47.2.txt vs export-48.0.txt)

- Diff file: 700 KB, 587 change hunks
- **828 linii usuniętych** (reworded/edited fragments)
- **5,059 linii dodanych** (new + reworded fragments; net +4,231 = dokładnie tyle ile TotalFragments delta)

### Vnum observations — **hipoteza potwierdzona ostatecznie**

```
Before 48.0:  VnumDatFile=112, VnumGameData=3
After 48.0:   VnumDatFile=112, VnumGameData=3  ← UNCHANGED across major
```

Vnum się **NIE RUSZYŁ** nawet przy major update. Across entire 45.x → 47.x → 48.0 cycle — vnum 112/3 stałe. **Vnum = schema version (binary format), NOT content version.** Vnum-triggered re-patch logic jest definitywnie martwa.

### Content changes w 48.0 (from diff sampling)

- **Nowa kraina: Hatokáli Fells** — wiele nowych lokacji (Crypt, Fells proper, regional channels)
- **Rhûn expansion content:** Nan Ogol / Fell-vale, Heledhris / Rift of Shornshards, Parth Rhavas / Lothfold
- **Nowe housing: Deluxe House (Cave/Tower), Deluxe Kinship House (Island), nowe płoty**
- **UI: „Edit UI" tooltip z CTRL-\ hint (new UI lock feature)**
- **Renames: Tyrant Tharbîl → Spirit of Vengeance, MGS Central Hub → Ingarûma, Barad Rill → Barad Rill (King's Gondor)**
- **Placeholder resolved: „TBD REGION" → „The Hatokáli Fells"**
- **Typo/polish fixes: galavanting → gallivanting, „What read simply" → „What is here simply" (Guardian primer)**

### datexport.dll compatibility

- ✅ **READ path (export) na 48.0 DAT:** 3.5s, 792,500 fragments extracted, zero errors
- ✅ **WRITE path (patch) na 48.0 DAT:** 8/8 applied w 723ms, zero warnings
- Schema wydaje się niezmieniona (lub zachowuje kompatybilność wsteczną)

### Launch log timeline

```
12:35:48 [FAILED] launch polish — CannotOpen (Program Files perms, non-elevated)
12:37:21 [SUCCESS] launch polish (elevated) — 8 applied, 0 skipped, fire-and-forget launcher, 682ms total
~12:37-12:39 — official launcher downloaded & applied 48.0 update
12:39:23 — DAT mtime set (launcher finished write)
```

Version file state post-run:
```
47|112|3|40b613a24dc5c1082697ddde5f542b0b607349a2613d83a88ddf225dc83ed3d6
ForumVersion=47, VnumDat=112, VnumGame=3, Hash=<polish.txt SHA256>
```

## Kluczowe odkrycia / memory worthy

### 1. Pierwszy major update survived — simplified flow assumption holds at scale

Do tej pory testowane tylko chunk-patche (47.1→47.2). 48.0 to pierwszy **major** update odkąd simplified flow istnieje. Survival potwierdzony wszystkimi kanałami. Hash-based trigger + fire-and-forget pattern jest production-ready.

### 2. Vnum definitywnie martwe jako content indicator

Trzeci major update/cycle z rzędu bez zmiany vnum (45.x, 47.x, teraz 48.0). Hipoteza „vnum = schema version" potwierdzona nadwymiarowo. Wszystkie miejsca kodu które polegałyby na vnum change detection można bezpiecznie usunąć (a właściwie już nie istnieją w simplified flow).

### 3. LOTRO wymusza update przed wejściem — impact na test methodology

**User insight:** „nie da się wejść do lotro bez update by robić screeny". Launcher blokuje wejście do gry jeśli nowa wersja dostępna. **Konsekwencja dla przyszłych testów:** pre-update in-game screenshots muszą być wykonane **przed publikacją update** (czyli mamy ~godziny okna między „update dostępny na forum" a „user faktycznie aktualizuje"). Po tym oknie dostępny jest tylko post-update stan. Export-based baseline (co zrobiliśmy tu) nie wymaga in-game access — alternatywa niezależna od tej blokady.

### 4. Program Files (x86) write permissions — dokumentowane ograniczenie

Pierwszy launch (12:35:48) padł z `CannotOpen` bo non-elevated shell. Drugi (12:37:21) zadziałał — user zapewne odpalił jako admin. **Implication:** CLI potrzebuje albo UAC manifestu, albo wyraźnej komunikacji „run as administrator" dla pierwszego użycia. Dla user-facing WPF app (M4) wymaganie elevation musi być wbudowane w installer lub app manifest.

## Infrastructure unchanged — legacy cleanup unblocked

Simplified flow + fire-and-forget + hash-based trigger = validated across:
- 5 live tests (memory pre-2026-04-23) — chunk patches
- 1 live test 2026-04-23 — major update (first of its kind)

**Legacy strategy cleanup (M1-08 TODO) może być zrobiony bez obaw:**
- `LegacyGameLaunchingStrategy` — usuń
- `IDatFileProtector` + `DatFileProtector` — usuń
- `--legacy` flag + `UseLegacyFlow` — usuń
- `IGameLaunchingStrategy` interfejs — zastąp bezpośrednim wstrzyknięciem deps do handlera
- `IForumPageFetcher` + `GameUpdateChecker` — nieużywane w simplified flow, mogą zostać do M3-11 (API-side cron) lub zostać usunięte jeśli nie będą potrzebne lokalnie

## Pliki intel w tym folderze

```
BASELINE.md                            — pre-update snapshot report
RESULTS.md                             — ten plik
client_local_English.47.2.dat          — pre-update DAT backup (1.75GB)
client_local_English.48.0.dat          — post-update DAT backup (1.76GB)
client_local_English.48.0.write-test.dat — WRITE path test result (patched 48.0)
client_local_English.48.0.write-test.dat.backup — auto-backup created by patch command
export-47.2.txt                        — pre-update full export (82MB, 788k fragments)
export-48.0.txt                        — post-update full export (82.5MB, 792k fragments)
diff-47.2-vs-48.0.txt                  — full unified diff (700KB)
polish-pre-48.txt                      — polish.txt snapshot (identical to current)
version-file-pre-48.txt                — "47|112|3" (pre-hash-format)
version-file-post-48.txt               — "47|112|3|<hash>" (post-launch save)
launch-during-update.log               — full Serilog of launch flow
```
