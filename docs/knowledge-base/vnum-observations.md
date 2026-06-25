---
name: DAT Vnum observations — empirical (updated 2026-04-23)
description: Vnum (OpenDatFileEx2 -> vnumDatFile/vnumGameData) behavior across LOTRO updates. Hypothesis "vnum = schema, NOT content" confirmed definitively across 47.x cycle AND 48.0 major update.
type: project
originSessionId: fcf33c21-a957-445f-bc57-c8ea7814f1b6
---
# DAT Vnum Observations

## Measured values across observed updates

| Date | Game version | VnumDatFile | VnumGameData | DAT modified? |
|------|--------------|-------------|--------------|---------------|
| 2026-03-16 | 47 → 47.1 | 112 | 3 | No |
| 2026-03-17 | 47.1 | 112 | 3 | N/A |
| 2026-03-22 | 47.1 → 47.2 | 112 | 3 | Yes (chunk-patched) |
| **2026-04-23** | **47.2 → 48.0 (MAJOR)** | **112** | **3** | **Yes (+14MB, +4,231 fragments)** |
| **2026-06-25** | **48.0 → 48.7 (point)** | **112** | **3** | **Yes (+1 MiB, +1,159 fragments)** |

## Conclusions — definitively confirmed

1. **Vnum is NOT a content version indicator.** Zero correlation with content patches, even MAJOR ones (48.0 added new region Hatokáli Fells, new Rhûn content, new housing).
2. **Vnum is almost certainly DAT binary schema version.** Would only bump if Turbine changed file format structure.
3. **Any logic triggering on vnum change is effectively dead code.** Won't fire in realistic timeframes (4 cycles 45.x/47.x/48.0/48.7 unchanged).
4. **Forum version** (e.g. "48.0" parsed from release notes thread titles) is the reliable content version identifier for API-side filtering (M3).

## Implications for code

- ✅ `IDatVersionReader` still useful for **snapshot into version file** (forensics, debugging)
- ❌ Vnum comparison as re-patch trigger would never fire → dead branch, remove
- ❌ Legacy `ConfirmUpdateInstalled()` logic (compare vnum before/after launcher) therefore moot
- ✅ Simplified flow correctly ignores vnum as trigger, uses translation file hash instead

## Why still read vnum at all

Keep `IDatVersionReader` for:
- Snapshot into version file for human-readable debugging output
- Future-proofing: if Turbine ever bumps schema, we'd want to know immediately (DAT format breakage signal)
- M3 API could aggregate vnum reports from many clients — if a schema bump ever happens, we'd detect it centrally

## Cross-reference

- [live-test-2026-04-23.md](live-test-2026-04-23.md) — major update 48.0 data (first of its kind)
- [live-test-2026-06-25.md](live-test-2026-06-25.md) — update 48.7 (4th cycle, SKIP path)
- [live-test-2026-03-16.md](live-test-2026-03-16.md) — earlier chunk-patch observations
- [update-detection-strategy.md](update-detection-strategy.md) — why hash is the only valid trigger
