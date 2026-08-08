# Baseline 48.0 — Pre-update snapshot (test: 48.0 → 48.7)

**Captured:** 2026-06-25
**Operator:** Claude (automated)
**Context:** Druga major-update walidacja simplified flow. Live DAT na 48.0; test survivalu translacji przez update 48.0 → 48.7.
**Redaction note:** quest-text content in this folder redacted to synthetic equivalents 2026-07 (LEGAL-08); the empirical result is unaffected.

## Live DAT state (pre-update)

| Attribute | Value |
|-----------|-------|
| Path | `C:\Program Files (x86)\StandingStoneGames\The Lord of the Rings Online\client_local_English.dat` |
| Size | 1,892,759,280 B (1.763 GB) |
| LastWriteTime | 2026-06-25 21:52:29 |
| SHA256 | `833C22DEB4DBF47AD2B622F2E5A276296429A9ABBB4E9333CF5C969B685C1826` |
| Backup | `intel/update-48.7/client_local_English.48.0.dat` (hash-verified identical) |

**Kluczowa obserwacja:** SHA256 `833C22DE…1826` = **dokładnie ten sam hash** co live DAT po update 48.0
z testu 2026-04-23 (patrz `intel/update-48.0/RESULTS.md`). Live DAT jest bajtowo identyczny ze stanem
„48.0 + nasze 8 translacji w nietkniętych chunkach" — translacje przetrwały od kwietnia bez re-patchu.

## Export stats (from backup)

| Metric | Value |
|--------|-------|
| Text files (high byte 0x25) | **278,377** |
| Total fragments | **792,500** |
| Export output size | 78.6 MB (82,469,439 B) |
| Export duration | 2.9s |
| Output path | `intel/update-48.7/export-48.0.txt` |

Liczby (278,377 / 792,500) = identyczne z post-48.0 z kwietnia → zero zmian treści od 48.0.
Export robiony z backupu (live DAT w Program Files odmawia otwarcia non-elevated — `datexport.dll`
otwiera plik z write/sidecar intentem; znane ograniczenie, patrz RESULTS 48.0 §perms).

> **Superseded 2026-08-07 (#629):** ograniczenie było nasze, nie biblioteki — read-only open siedzi na
> bicie `0x4`. Kolejne exporty rób prosto z live path —
> [../datexport-readonly-open-2026-08-07.md](../datexport-readonly-open-2026-08-07.md).

## Version file (pre-update)

```
data/last_known_game_version.txt (73 bytes): 48|112|3|40b613a24dc5c1082697ddde5f542b0b607349a2613d83a88ddf225dc83ed3d6
```

- ForumVersion: `48`
- VnumDatFile: `112`
- VnumGameData: `3`
- TranslationFileHash: `40b613a2…ed3d6`

## Polish translations baseline

**SHA256 polish.txt:** `40B613A24DC5C1082697DDDE5F542B0B607349A2613D83A88DDF225DC83ED3D6` (2300 B)

**= stored hash w version file** → `launch polish` trafi w **SKIP path** (translacje już zaaplikowane,
brak re-patchu, brak zapisu DAT). Translacje są fizycznie obecne w live DAT (8/8 poniżej).

**Survival status (8/8 visible w live DAT 48.0 — confirmed via export-48.0.txt):**

| # | FileId | GossipId | Content (first 60 chars) | Export line |
|---|--------|----------|--------------------------|-------------|
| 1 | 620871150 | 218649169 | `'Mamy znak, <--DO_NOT_TOUCH!-->! Szare ćmy wznoszą się rzędem…` | 255033 |
| 2 | 620759036 | 218649169 | `'PL - We cannot let the old warden rouse…` (partial tr.) | 19821 |
| 3 | 620757435 | 225138404 | `Wejdź do Śródziemia` | 7063 |
| 4 | 620757027 | 9795381 | `Kliknij tutaj aby wybrać swój tytuł` | 3798 |
| 5 | 620861331 | 228870261 | `Ekwipunek` | 227819 |
| 6 | 620757027 | 29271026 | `Podstawowe statystyki` | 3879 |
| 7 | 620757027 | 103943794 | `Pokaż wszystkie` | 3681 |
| 8 | 620757027 | 85383154 | `Stylizacje` | 3765 |

## Phase 1 complete — what's ready

✅ Live DAT backed up (intel/update-48.7/, hash-verified)
✅ Live DAT hashed (SHA256 `833C22DE…1826`)
✅ Full export of 48.0 (792,500 fragments)
✅ polish.txt backed up + hashed (== stored hash → SKIP path)
✅ version file backed up
✅ Baseline survival confirmed: 8/8 polish translations in export-48.0.txt

## Phase 2 — USER ACTION REQUIRED

1. **(opcjonalnie) In-game screenshots na 48.0** zanim ruszysz update — te same 8 miejsc co w teście kwietniowym.
2. **Uruchom `lotro.bat polish --skip-sync`** (self-eleva­cja UAC) → SKIP path → fire-and-forget LotroLauncher.exe.
3. **Pozwól oficjalnemu launcherowi ściągnąć update 48.7** → wejdź do gry → potwierdź że polski nadal działa.
4. **Wróć i napisz „done"** — dokończę Phase 3 (export 48.7, diff, survival check, WRITE-path test, RESULTS + memory).
