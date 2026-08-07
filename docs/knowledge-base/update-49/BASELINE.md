# Baseline 48.8 — Pre-update snapshot (test: 48.8 → 49 "In Good Company")

**Captured:** 2026-08-02
**Operator:** Claude (automated)
**Context:** Trzecia walidacja survivalu przez żywy update — pierwszy przeskok przez granicę MAJOR
od 48.0 (Update 49 „In Good Company", wydany 2026-07-22). Pierwszy update event odkąd TMS jest
wdrożony (M6) — post-update export napędzi pierwszy prawdziwy przebieg lifecycle'u spec 0001
(rejestracja GameVersion 49 + import + diff/invalidation).
**Redaction note:** quest-text content in this file uses the synthetic equivalents established by
LEGAL-08 (2026-07); verbatim originals live only in the gitignored `intel/update-49/`.

## Live DAT state (pre-update)

| Attribute | Value |
|-----------|-------|
| Path | `C:\Program Files (x86)\StandingStoneGames\The Lord of the Rings Online\client_local_English.dat` |
| Size | 1,893,807,856 B (1.764 GB) |
| LastWriteTime | 2026-07-11 02:47:46 |
| SHA256 | `4E9A8106C16159BC80C7865AE17A740D732B78AA49F96785BE144EF76E5AFD88` |
| Backup | `intel/update-49/client_local_English.48.8.dat` (hash-verified identical) |

**Prowieniencja stanu 48.8:** rozmiar identyczny z post-48.7 (1,893,807,856 B), ale hash ≠ 48.7
(`620DA8FD…637A`). Oczekiwane — 2026-07-11 test AUDIT-SEC wykonał wymuszony live PATCH (8/8,
size-neutral re-patch, stąd LastWrite 02:47:46), a między 48.7 a teraz doszedł drobny patch 48.8:
export pokazuje **+5 plików / +13 fragmentów** względem post-48.7.

## Export stats (z backupu)

| Metric | Value | vs post-48.7 |
|--------|-------|--------------|
| Text files (high byte 0x25) | **278,983** | +5 |
| Total fragments | **793,672** | +13 |
| Export output size | 82,557,600 B | +1,244 B |
| Output path | `intel/update-49/export-48.8.txt` | — |

Export robiony z backupu (live DAT w Program Files odmawia otwarcia non-elevated — `datexport.dll`
otwiera z write/sidecar intentem; znane ograniczenie, patrz RESULTS 48.0 §perms).

> **Superseded 2026-08-07 (#629):** ograniczenie było nasze, nie biblioteki — read-only open siedzi na
> bicie `0x4`. Kolejne exporty rób prosto z live path —
> [../datexport-readonly-open-2026-08-07.md](../datexport-readonly-open-2026-08-07.md).

## Version file (pre-update)

```
data/last_known_game_version.txt (75 B): 48.8|112|3|40b613a24dc5c1082697ddde5f542b0b607349a2613d83a88ddf225dc83ed3d6
```

- ForumVersion: `48.8` · VnumDatFile: `112` · VnumGameData: `3`
- TranslationFileHash: `40b613a2…3ed3d6`
- Snapshot: `intel/update-49/version-file-pre-49.txt`

## Rozwidlenie polish.txt — KRYTYCZNE dla tego cyklu

LEGAL-08 (#481, merged 2026-07-31) zamienił verbatim game text w **repo** na syntetyczne
odpowiedniki. Skutek: working-copy `translations/polish.txt` ≠ zestaw rezydujący w DAT:

| File | SHA256 | Size | Rola |
|------|--------|------|------|
| `translations/polish.txt` (repo, po LEGAL-08) | `F3DD657C…3AFE3` | 2,264 B | syntetyczny — NIE odzwierciedla DAT |
| DAT-resident set (pre-LEGAL-08) | `40B613A2…3ED3D6` | 2,300 B (CRLF) | == stored hash w version file |

Resident set odzyskany z gita (`git show d590088^:translations/polish.txt`) + restytucja CRLF —
hash-proven == stored hash (git blob jest LF, working copy z 2026-07-11 była CRLF). Archiwum w
gitignored `intel/update-49/` (verbatim + stan repo).

**Konsekwencja protokołu:** `launch polish` / `lotro.bat` poszedłby ścieżką PATCH (hash mismatch)
i wstrzyknął syntetyczny tekst do żywej gry → **w tym cyklu NIE używamy naszego launch**. Update
idzie przez oficjalny LotroLauncher bezpośrednio. Obie gałęzie simplified flow są już zwalidowane
żywymi update'ami (PATCH — kwiecień 48.0, SKIP — czerwiec 48.7); ten cykl testuje czysty
resident-survival przez major. **Follow-up po teście:** przywrócić spójność polish.txt ↔ DAT
(np. świeży export z TMS), żeby simplified flow znów miał prawdziwy hash-match.

## Polish translations baseline — survival 8/8 (export-48.8.txt)

Content-level check (para + prefiks polskiej treści obecne w exporcie):

| # | FileId | GossipId | Content (synthetic where quest text) | Export line |
|---|--------|----------|--------------------------------------|-------------|
| 1 | 620871150 | 218649169 | `'Mamy znak, <--DO_NOT_TOUCH!-->! Szare ćmy wznoszą się rzędem…` | 255044 |
| 2 | 620759036 | 218649169 | `'PL - We cannot let the old warden rouse…` (partial tr.) | 19821 |
| 3 | 620757435 | 225138404 | `Wejdź do Śródziemia` | 7063 |
| 4 | 620757027 | 9795381 | `Kliknij tutaj aby wybrać swój tytuł` | 3798 |
| 5 | 620861331 | 228870261 | `Ekwipunek` | 227830 |
| 6 | 620757027 | 29271026 | `Podstawowe statystyki` | 3879 |
| 7 | 620757027 | 103943794 | `Pokaż wszystkie` | 3681 |
| 8 | 620757027 | 85383154 | `Stylizacje` | 3765 |

Pozycje linii **identyczne z post-48.7** — patch 48.8 (+5/+13) nie przesunął naszych fragmentów.

## Phase 1 complete — co jest gotowe

✅ Live DAT backed up (`intel/update-49/`, hash-verified `4E9A8106…5AFD88`)
✅ Full export 48.8 (793,672 fragments)
✅ Version file + oba warianty polish.txt zarchiwizowane (resident hash-proven)
✅ Baseline survival: 8/8 pair-level + content-level w export-48.8.txt

## Phase 2 — USER ACTION REQUIRED

1. **NIE uruchamiaj `lotro.bat` / `launch polish`** (patrz sekcja rozwidlenia polish.txt).
2. Uruchom **oficjalny LotroLauncher** bezpośrednio → pozwól mu ściągnąć Update 49.
3. Wejdź do gry → sprawdź polskie teksty (UI: Ekwipunek / Stylizacje / Pokaż wszystkie +
   miejsca questowe z testu kwietniowego).
4. Wróć i napisz „done" — ruszy Phase 3 (backup 49, export-49.txt, diff, survival, WRITE test,
   RESULTS, rejestracja GameVersion 49 w TMS + import exportu).
