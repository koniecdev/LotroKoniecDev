# Baseline 47.2 — Pre-update snapshot

**Captured:** 2026-04-23
**Operator:** Claude (automated)
**Context:** Pierwszy major update (48.0 just released) od czasu walidacji simplified flow.

## Live DAT state (pre-update)

| Attribute | Value |
|-----------|-------|
| Path | `C:\Program Files (x86)\StandingStoneGames\The Lord of the Rings Online\client_local_English.dat` |
| Size | 1,878,079,216 B (1.749 GB) |
| LastWriteTime | 2026-04-06 20:57:28 (data instalacji 47.2) |
| SHA256 | `AABEC3AAC5C78869CA32747385B329DE91145B2E6620CC5183CDA8DC48916992` |
| Backup | `intel/update-48.0/client_local_English.47.2.dat` |

## Export stats (from backup)

| Metric | Value |
|--------|-------|
| Text files (high byte 0x25) | **277,082** |
| Total fragments | **788,269** |
| Export output size | 81.9 MB |
| Export output lines | 788,278 |
| Export duration | 3.0s |
| Output path | `intel/update-48.0/export-47.2.txt` |

## Version file (pre-update)

```
data/last_known_game_version.txt (8 bytes): 47|112|3
```

- ForumVersion: `47` (niezgodne z aktualnym 47.2 — stary format sprzed M1-10, bez hashu)
- VnumDatFile: `112`
- VnumGameData: `3`
- TranslationFileHash: brak

**Kluczowa obserwacja:** vnum 112/3 = niezmienione od pierwszego zapisu. Pasuje do `vnum-observations.md`.

## Polish translations baseline

**SHA256 polish.txt:** `40B613A24DC5C1082697DDDE5F542B0B607349A2613D83A88DDF225DC83ED3D6` (2300 B)

**Survival status (8/8 visible w live DAT 47.2):**

| # | FileId | GossipId | Content (first 80 chars) |
|---|--------|----------|--------------------------|
| 1 | 620871150 | 218649169 | `'Mamy trop, <--DO_NOT_TOUCH!-->! Szlak czerwonych kwiatów...` |
| 2 | 620759036 | 218649169 | `'PL - We cannot allow the Dourhands to resurrect...` (incomplete tr.) |
| 3 | 620757435 | 225138404 | `Wejdź do Śródziemia` |
| 4 | 620757027 | 9795381 | `Kliknij tutaj aby wybrać swój tytuł` |
| 5 | 620861331 | 228870261 | `Ekwipunek` |
| 6 | 620757027 | 29271026 | `Podstawowe statystyki` |
| 7 | 620757027 | 103943794 | `Pokaż wszystkie` |
| 8 | 620757027 | 85383154 | `Stylizacje` |

**Uwaga:** GossipId 228870261 występuje też w `620757029`, `620757032`, `620757033` (angielski/test) — test polega na specific pair FileId||GossipId. Patch aplikuje się tylko do FileId 620861331 dla tego GossipId.

## Phase 1 complete — what's ready

✅ Live DAT backed up (intel/)
✅ Live DAT hashed (SHA256 above)
✅ Full export of 47.2 (788k fragments)
✅ polish.txt backed up + hashed
✅ version file backed up
✅ Baseline survival confirmed: 8/8 polish translations in export-47.2.txt

## Phase 2 — USER ACTION REQUIRED

Potrzebne od ciebie, zanim uruchomisz update:

1. **In-game screenshots na 47.2** — zrób zdjęcia **każdego** z 8 miejsc gdzie jest polska translacja. Konkretnie:
   - „Wejdź do Śródziemia" — ekran logowania / selekcja postaci
   - „Kliknij tutaj aby wybrać swój tytuł" — UI profilu postaci
   - „Podstawowe statystyki" — ekran statystyk (Character sheet)
   - „Pokaż wszystkie" — filtr/dropdown w UI
   - „Stylizacje" — sekcja Wardrobe/Cosmetic
   - „Ekwipunek" — inventory / slot UI
   - Quest dialog „Mamy trop..." (FileId 620871150) — trzeba znaleźć questline
   - Quest dialog „'PL - We cannot..." (FileId 620759036) — jw.

   Nazwy plików: `ingame-47.2/<shortname>.png` w `intel/update-48.0/ingame-47.2/`

2. **Uruchom oficjalny LOTRO launcher** → pozwól ściągnąć update 48.0 → wejdź chwilę do gry żeby potwierdzić że update zadziałał

3. **Po update — wróć i napisz „done"** — dokończę Phase 3 (export 48.0, diff, survival check, hash reset, simplified flow test, update memory)
