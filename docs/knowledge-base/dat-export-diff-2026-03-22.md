---
name: DAT export diff analysis 2026-03-22
description: Diff between post-47.1 export (2026-03-17) and post-47.2 export (2026-03-22) — ~5000 lines changed across 3 accumulated patches (47.1→47.1.1→47.2), translations survived
type: project
---

## Files Compared

- **Old:** `data/exported.txt` — 783,487 lines, modified 2026-03-17 00:12 (post update 47.1)
- **New:** `data/post_update_export.txt` — 788,254 lines, created 2026-03-22 12:27 (post update 47.2)
- **Delta:** +4,767 lines net (+7,086 added, -2,319 removed)

## Diff Spans 3 Accumulated Patches

User did not launch LOTRO between March 17 and March 22. Forum release notes confirm 3 patches in that window:
- **47.1** (installed before March 17 export)
- **47.1.1** (released between March 17-22)
- **47.2** (applied on March 22 during test)

The ~5000 line diff is the sum of 47.1→47.1.1→47.2, not a single patch.

## LOTRO Update History (from forum release notes page)

```
... → 45.1 → 45.2 → 45.2.1 → 45.3 → 45.4 → 45.4.1
   → 46 → 46.1
   → 47 (MAJOR — new instances, Guardian rework)
   → 47.1 → 47.1.1 → 47.2
```

Update 47 was the major content patch (Guardian rework, new instances). The Guardian skill text changes visible in the diff are minor post-rework polish from 47.1.1/47.2, not the rework itself (which was already in the old export).

## Types of Changes Observed

### 1. Our 8 translations (as expected — confirm survival)
Old export had English originals, new has Polish — diff shows them as "changed":
- `Show All Stats` → `Pokaż wszystkie`
- `Cosmetic Outfits` → `Stylizacje`
- `Enter Middle-earth` → `Wejdź do Śródziemia`
- etc.

### 2. SSG text changes (accumulated 47.1.1 + 47.2)
- `Shield Attack Critical Chance` → `Shield Skills Critical Chance`
- `Vitals` → `Player Vitals`
- UI scale descriptions rewritten (more granular)
- Skill descriptions reworded (post-rework polish)

### 3. New entries (~4700+ lines)
- New location: `Mûr Ghala: Pahar Hatokáli`
- Many new UI scale descriptions (granular UI scaling feature added)
- New panels: `Panel Map`, `Fullscreen Map`, `Collapse/Expand Window`
- New skill entries

## Key Finding: Chunk-Based DAT Patching Mechanism

User observed launcher downloading partial files (e.g. `client_local_English-98232.dat`) — NOT the full DAT. Launcher applies only changed chunks to the DAT. Our translated entries sit in untouched chunks → survive.
