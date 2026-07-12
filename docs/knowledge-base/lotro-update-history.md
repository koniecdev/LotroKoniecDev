---
name: LOTRO update version history and release timeline
description: Known LOTRO update versions observed during this project. Includes content summaries for major updates 47 and 48. All observed vnums 112/3 unchanged.
type: reference
originSessionId: fcf33c21-a957-445f-bc57-c8ea7814f1b6
---
# LOTRO Update History (observed)

> **Addendum (2026-07-11):** → **48.8** observed (forum fetch returned "48.8"; vnum 112/3 unchanged,
> 5th cycle; resident translations survived 48.7 → 48.8). Tested on the first real-world AUDIT-SEC
> run — see [live-test-2026-07-11.md](live-test-2026-07-11.md).

## Known versions

```
45.1 → 45.2 → 45.2.1 → 45.3 → 45.4 → 45.4.1
→ 46 → 46.1
→ 47 (MAJOR — Guardian rework, new instances)
→ 47.1 → 47.1.1 → 47.2
→ 48.0 (MAJOR — 2026-04-23, Hatokáli Fells / Rhûn content)
→ 48.7 (point — 2026-06-25, +1,159 fragments / +601 text files)
```

All observed with **vnum 112/3 unchanged** — see [vnum-observations.md](vnum-observations.md).

## Our test timeline

| Date | Game version | What we tested |
|------|--------------|----------------|
| Before 2026-01-27 | Pre-47 (46.x?) | Patcher existed, exported.txt created |
| 2026-03-16 | 47 → 47.1 | Legacy flow (harmful), forum scraper works |
| 2026-03-17 | 47.1 | Simplified flow all paths except SKIP, export baseline |
| 2026-03-22 | 47.1 → 47.2 (chunk) | SKIP path, translations survive DAT-patching update |
| **2026-04-23** | **47.2 → 48.0 (MAJOR)** | **First major update since simplified flow. 4-channel survival verified. datexport.dll READ + WRITE compat with 48.0 schema.** |
| **2026-06-25** | **48.0 → 48.7 (point)** | **Second live update, first via SKIP path. 4-channel survival verified. datexport.dll READ+WRITE compat with 48.7. vnum 112/3 (4th cycle). Forum-fetcher returned "48.7".** |

## Major update 48.0 content (from diff analysis)

See `intel/update-48.0/diff-47.2-vs-48.0.txt` (in project dir, gitignored) — 587 hunks, +4,231 fragments.

**New regions:**
- **The Hatokáli Fells** (crypt, fells proper, regional/OOC/role-playing channels)
- **Rhûn expansion:** Nan Ogol / Fell-vale, Heledhris / Rift of Shornshards, Parth Rhavas / Lothfold

**New housing:**
- Deluxe House (Cave), Deluxe House (Tower)
- Deluxe Kinship House (Island)
- New fences

**UI features:**
- „Edit UI" with CTRL-\ lock/unlock shortcut

**Renames / placeholder resolutions:**
- Tyrant Tharbîl → Spirit of Vengeance
- MGS Central Hub → Ingarûma
- „TBD REGION" placeholder → „The Hatokáli Fells"
- Barad Rill → Barad Rill (King's Gondor)

**Typo/polish fixes:**
- galavanting → gallivanting (Samwise dialog in Rivendell)
- „What read simply" → „What is here simply" (Guardian primer)
- Minor whitespace/punctuation normalization

## Source

Forum release notes page: `https://forums.lotro.com/index.php?forums/release-notes-and-known-issues.7/`
Regex for version detection: `Update\s+(\d+(?:\.\d+)*)\s+Release\s+Notes`

## Archival policy

Full exports (82MB each) archived in `intel/update-XX.Y/` per update. Roughly 1 GB/year for long-term historical diff capability across all future updates.
