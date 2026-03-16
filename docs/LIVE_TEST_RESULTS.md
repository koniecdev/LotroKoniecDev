# Live Test Results — Game Launch Flow

## Test 1: Legacy Flow (2026-03-16, primary PC)

### Setup
- **Branch:** 82-program-cleanup (strategy pattern refactor)
- **LOTRO:** Update 47→47.1 available
- **Baseline:** version file `47|112|3` (ForumVersion=47, VnumDat=112, VnumGame=3)
- **Command:** `./LotroKoniecDev.Cli.exe launch polish --legacy`
- **Branch selected:** C (forum changed, DAT unchanged → HandleUpdatePath)

### Timeline (from launch_test.log)

```
09:29:26  START
09:29:28  Forum check: 47→47.1 detected                         (2s scrape)
09:29:30  DAT vnum read: VnumDat=112, VnumGame=3                (1.4s OpenDatFileEx2)
09:29:30  Branch C selected → UPDATE PATH
09:29:30  DAT unprotected (attrib -R), launcher started
09:29:40  Launcher initial exit code 0 (UAC restart)             (10s)
09:29:40  UAC-restarted launcher detected instantly
09:29:40  Phase 2: monitoring launcher...
09:30:22  Launcher exited after 41 polls                         (42s update)
09:30:22  Game client detected → KILLED by our code
09:30:22  Vnum AFTER update: still 112/3 — UNCHANGED
09:30:23  Re-patched: 7 applied, 0 skipped
09:30:23  DAT re-protected, second launcher started
09:30:47  Game session ended                                     (81s total)
```

### What the user experienced

1. Launcher start → UAC #1
2. Launcher update (~42s)
3. Game started after update → **killed by our code**
4. Second launcher start → UAC #2
5. Game started again → user plays → exits

**Result: Double UAC, double login. HandleUpdatePath is actively harmful.**

### Key findings

| Finding | Detail |
|---------|--------|
| **Vnums unchanged** | 112/3 before AND after update. `client_local_English.dat` was not touched by 47→47.1 |
| **Translations survived** | Re-patch applied same 7, 0 skipped — nothing was overwritten |
| **Game kill unnecessary** | Killed a working game session with intact translations |
| **Re-patch unnecessary** | Translations were already there, untouched |
| **Forum checker works** | Correctly detected 47→47.1 in 2 seconds |

---

## Test 2: Legacy First-Run on Outdated PC (2026-03-02, second PC)

### Setup
- **Branch:** pre-strategy-pattern (legacy flow was default)
- **LOTRO:** Outdated, major update pending
- **Baseline:** none (first run — no version file)
- **Command:** `./LotroPoPolsku.exe launch polish`
- **Branch selected:** A (first run, no baseline → SaveRepatchAndLaunch)

### Timeline (reconstructed from issue #81)

```
1. First-run path triggered
2. 7 translations patched into client_local_English.dat
3. ProtectedLaunch: DAT set read-only (attrib +R)
4. LotroLauncher.exe started
5. Launcher exited immediately (exit code 0) — UAC restart
6. Patcher read exit code 0 as "session ended"
7. finally block removed read-only protection
8. Patcher exited — no longer running
9. Launcher restarted after UAC elevation (patcher unaware)
10. Official launcher downloaded full update (including -7656.dat files)
11. Translations survived update — no protection was active
12. Later launches from official SSG launcher also did not overwrite translations
```

### What the user experienced

1. `LotroPoPolsku.exe` patched and launched
2. UAC prompt → launcher started
3. Patcher exited (thought session was over)
4. Launcher updated LOTRO in background — translations intact

**Result: ProtectedLaunch failed silently. Protection removed before update even started. Translations survived anyway.**

### Key findings

| Finding | Detail |
|---------|--------|
| **ProtectedLaunch race condition** | `finally` removed read-only before real update started — protection was never active during update |
| **Translations survived unprotected** | Full major update ran with no DAT protection; translations untouched |
| **Exit code 0 is misleading** | Launcher exits with code 0 for UAC restart, not for session end |
| **Subsequent launches safe** | Official SSG launcher also didn't overwrite translations on later runs |

---

## Test 3: Simplified Flow (planned, second PC)

### Plan
- **Command:** `./LotroKoniecDev.Cli.exe launch polish` (no `--legacy`)
- **Expected:** Hash null → patch → fire-and-forget → one UAC → one login → translations survive update

### What to verify
1. Hash detection works (first run = always patches)
2. Fire-and-forget launch works
3. Translations survive update (additional confirmation)
4. Second run: hash matches → skip patch
5. Total time vs Legacy's 81s

---

## Conclusions

### Translations survive LOTRO updates — four independent observations

1. **Test 2 / Issue #81** (2026-03-02, second PC, major update): ProtectedLaunch failed — `finally` removed protection before update started. Translations survived unprotected.
2. **User accident** (undated): Accidentally launched LOTRO from desktop during major update 47. Translations appeared to survive.
3. **Test 1** (2026-03-16, primary PC, minor 47→47.1): Vnums unchanged — `client_local_English.dat` not modified at all.
4. **Post-Test 2 launches**: Official SSG launcher used repeatedly after Test 2; translations persisted across multiple sessions.

**Why:** LOTRO launcher only patches DAT entries that actually changed. Our 0x25 subfiles (translation text) are not touched because SSG doesn't modify the same entries we translate.

### DAT version numbers (vnums) are deterministic

Each LOTRO update produces a specific vnum in the DAT header. All players on the same game version have the same vnum. This makes vnum comparison a more reliable update indicator than forum scraping:
- Forum can be delayed, change format, or go down
- Vnum in DAT is always current and unambiguous
- However, minor updates may NOT change vnum (observed in 47→47.1: vnum stayed 112/3)

### Legacy flow problems

- `IDatFileProtector` (attrib +R) — unnecessary, translations survive without it; also has race condition (Test 2: `finally` removes protection before update starts)
- `HandleUpdatePath` — actively harmful: kills working game, forces double UAC/login
- `WaitForLauncherCompletionAsync` — monitors a problem that doesn't exist
- Exit code 0 detection — unreliable: launcher returns 0 for UAC restart, not just session end
- Unconditional re-patch — wrong: if SSG changed a string we translated, stale translation overwrites fresh English

### Simplified flow is correct direction

```
1. Check translation file hash vs last applied
2. If changed → patch
3. Fire-and-forget launch
4. Done
```

No protection, no monitoring, no killing. Patch only when OUR translations changed.

### Caveat

All four observations involve the same game (LOTRO) with small translation sets (7 entries). A major update that specifically rewrites text subfiles (0x25) could theoretically overwrite translations. The hash-based re-patch mechanism provides a safety net: if the user updates their translation file after such an event, translations will be re-applied.
