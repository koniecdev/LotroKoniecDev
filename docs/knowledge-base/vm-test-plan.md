---
name: VM test environment plan and friend's 2-year-old LOTRO
description: Plan to use friend's outdated (~2 years) LOTRO installation for VM-based testing with infinite rollback capability
type: project
---

> **Status note (2026-07-11):** this plan was never executed — it remains an unexecuted option for
> a mega-update survival test. Separately, the idea of a VM as an *unattended export runner* for
> the update pipeline was evaluated and **deferred** in
> [`docs/adr/0030-game-version-export-stays-manual-vm-runner-deferred.md`](../adr/0030-game-version-export-stays-manual-vm-runner-deferred.md)
> (reconsider triggers listed there — including the unconfirmed GPU-less patching / silent-launcher
> / licensing prerequisites this plan would also face).

## Test Resource: Friend's 2-Year-Old LOTRO

User has a friend with LOTRO not updated for ~2 years. The friend has been asked not to launch the game yet. This gives us:
- Vnums from ~2 years ago (may differ from current 112/3 — would prove vnums DO change)
- Export diff spanning ~2 years of LOTRO content changes
- Test of mega-update (multiple major + minor patches at once)

**Why:** This is the only way to empirically test a major update that significantly changes `client_local_English.dat` content, including entries we might translate.

## VM Strategy

### What to Copy from Friend's PC

1. **Game directory** (~46 GB full, ~3-4 GB minimum):
   ```
   C:\Program Files (x86)\StandingStoneGames\The Lord of the Rings Online\
   ```
   Minimum for patcher test: `client_local_English.dat` (1.8 GB) + `LotroLauncher.exe` + DLLs

2. **AppData** (launcher logs):
   ```
   C:\Users\{user}\AppData\Local\The Lord of the Rings Online\
   ```

3. **Documents** (optional, plugins/screenshots):
   ```
   C:\Users\{user}\Documents\The Lord of the Rings Online\
   ```

### Registry: NOT NEEDED

Confirmed on user's PC: **zero LOTRO registry entries** exist, yet game works fine. `TurbineRegisterGDF.exe` doesn't exist in modern SSG installs. Our patcher's `DatFileLocator` finds DAT by checking default file path, not registry.

### VM Procedure

```
1. Copy friend's LOTRO directory to external drive
2. Set up Win10 VM
3. Place files at C:\Program Files (x86)\StandingStoneGames\...
4. SNAPSHOT ← infinite rollback point
5. Test A: Run patcher export → get pre-update vnums + text baseline
6. Test B: Run LotroLauncher.exe → observe mega-update
7. Test C: After update → run patcher → check translations
8. Rollback → repeat with variations
```

### Simpler Alternative: Friend Runs Patcher Directly

Our exe is self-contained x86, zero dependencies:
1. Give friend: `LotroKoniecDev.Cli.exe` + `translations/polish.txt`
2. Friend runs: `LotroKoniecDev.Cli.exe export` → sends back exported.txt + logs
3. We get: vnums from 2 years ago + full text baseline
4. Then friend can update and we compare

**How to apply:** Both approaches work. VM gives infinite tries but requires ~46 GB copy. Friend running patcher is simpler but one-shot for pre-update data (export preserves it though).
