# Knowledge Base — Empirical Findings & Research

This folder is the project's **curated knowledge base**: empirical test results, reverse-engineering
notes, reference research, and strategic decisions accumulated during development. It was originally
captured as private development notes and is committed here so the knowledge is **project-shared** —
available on every clone, to every contributor (and to any AI assistant working in this repo).

> **Nature of these notes:** Each file is a *point-in-time observation* dated at capture. Claims about
> code behavior or `file:line` citations reflect the state when written — **verify against current code
> before treating any of it as live fact.** Empirical findings about *LOTRO/DAT behavior* (chunk
> patching, vnum, translation survival) have been re-confirmed across multiple updates and are
> considered stable. See dates in each file.

---

## Index

### Russian LOTRO Translation Project (translate.lotros.ru)
Detailed analysis: [russian-project.md](russian-project.md)
- Same tech DNA: datexport.dll, TextFileMarker `0x25`, same translation format
- Pipeline: Xavian extracts DAT → SQLite → web platform → translators → review → SQLite patch `.db` → Elanor launcher applies
- Desktop launcher (Elanor, C# WPF) uses `-disablePatch -nosplash -skiprawdownload` flags on TurbineLauncher.exe
- NinjaMark: metadata in DAT subfile `620750000`, format `Ru&{version}&{date}&{subscribed}`, detects if official launcher overwrote translations

### DAT Protection: NOT NEEDED — Translations Survive Updates (incl. MAJOR)
Detailed analysis: [dat-protection.md](dat-protection.md)
- **PROVEN by 6 independent tests** including major update 47.2→48.0 (2026-04-23)
- Launcher uses chunk-based patching — our fragments sit in untouched chunks → byte-for-byte intact
- `attrib +R` protection is unnecessary; legacy `HandleUpdatePath` is actively harmful; vnum-trigger is dead
- Simplified flow (hash-based patch + fire-and-forget) is fully validated

### Live Test — Major Update 2026-04-23 (47.2 → 48.0)
Detailed analysis: [live-test-2026-04-23.md](live-test-2026-04-23.md) · raw intel: [update-48.0/](update-48.0/)
- **First major update since simplified flow existed** — 47.2 → 48.0 (Hatokáli Fells + Rhûn content)
- 4 independent survival channels verified: in-game, export presence, diff stability, launch log
- datexport.dll READ + WRITE paths confirmed compatible with 48.0 DAT schema
- Program Files (x86) perms gotcha: first launch failed non-elevated, retry as admin worked — installer needs UAC manifest

### Live Test — Chunk Patches 2026-03-16..22
Detailed analysis: [live-test-2026-03-16.md](live-test-2026-03-16.md)
- 5 chunk-patch tests, all simplified-flow branches verified
- Legacy flow: harmful (double UAC, kills game) — confirmed bad
- SKIP path: 2026-03-22 confirmed (hash match → launch in 255ms)

### DAT Vnum — definitively schema-version, not content-version
Detailed analysis: [vnum-observations.md](vnum-observations.md)
- Vnum 112/3 unchanged across 45.x → 47.x → **48.0 major** — 3 full cycles, zero movement
- Vnum = DAT binary schema version, NOT content version — any vnum-triggered logic is dead
- Forum version ("48.0") is the reliable content identifier

### DAT Export Diffs
Detailed analysis: [dat-export-diff-2026-03-22.md](dat-export-diff-2026-03-22.md)
- 47.1→47.2: ~5000 lines, skill rewording
- 47.2→48.0: [update-48.0/diff-47.2-vs-48.0.txt](update-48.0/diff-47.2-vs-48.0.txt) — 587 hunks, +4,231 fragments, new region

### LOTRO Update History
Detailed analysis: [lotro-update-history.md](lotro-update-history.md)
- 45.1 → 45.4.1 → 46 → 46.1 → 47 (major) → 47.1 → 47.1.1 → 47.2 → 48.0 (major, 2026-04-23)
- 48.0 content: Hatokáli Fells, Rhûn expansion, Deluxe housing, Edit UI feature

### Game Update Detection & Translation Versioning
Detailed analysis: [update-detection-strategy.md](update-detection-strategy.md)
- Vnum-triggered re-patch is WRONG (would never fire + would be destructive if it did)
- Only valid local trigger: translation file hash change
- Target model superseded by `docs/specs/0001-game-update-lifecycle-and-translation-invalidation.md`
  (forum-only `GameVersion` lifecycle + status-based invalidation); the core insight stands
- Simplified flow is CORRECT as MVP — hash-based trigger naturally evolves into the API model

### VM Test Plan (Friend's 2-Year-Old LOTRO)
Detailed analysis: [vm-test-plan.md](vm-test-plan.md)
- Friend has ~2-year-old LOTRO — potential goldmine for testing a pre-45 version jump
- VM approach: copy his files → snapshot → infinite test attempts
- No registry needed (zero LOTRO registry entries on user's PC)

### Project Strategy Decisions
Detailed analysis: [project-strategy.md](project-strategy.md)
- OSS code (patcher), controlled translations (web platform with review)
- Glossary/style guide critical for consistency (e.g., Tolkien proper nouns)
- Web platform (M3) = for translators; WPF app (M4) = for gamers
- Three presentation layers (CLI + Web + WPF), one set of MediatR handlers

---

## `update-48.0/` — raw intel from the 48.0 major-update test (2026-04-23)

Text evidence behind the 48.0 findings. The multi-GB DAT backups and 82 MB full exports are intentionally
**not** committed (they live in the gitignored `intel/` folder and are regenerable from a LOTRO install);
only the irreplaceable text artifacts are preserved here:

| File | What it is |
|------|-----------|
| [`BASELINE.md`](update-48.0/BASELINE.md) | Pre-update 47.2 snapshot: DAT hash, export stats, the 8 translation pairs |
| [`RESULTS.md`](update-48.0/RESULTS.md) | Full 47.2→48.0 test results, metrics, verdict |
| [`diff-47.2-vs-48.0.txt`](update-48.0/diff-47.2-vs-48.0.txt) | Unified diff of the two full exports (587 hunks) — the only surviving record of exactly what changed |
| [`launch-during-update.log`](update-48.0/launch-during-update.log) | Serilog of the launch flow during the update |
| `polish-pre-48.txt` | `polish.txt` snapshot at test time |
| `version-file-pre-48.txt` / `version-file-post-48.txt` | Version file before/after the run |

---

## Related committed docs (broader context)

- [`../../CLAUDE.md`](../../CLAUDE.md) — project memory: architecture, roadmap digest, house rules (live backlog: `gh issue list`)
- [`../RUSSIAN_PROJECT_RESEARCH.md`](../RUSSIAN_PROJECT_RESEARCH.md) — full Russian-project analysis

Earlier raw launch-flow reconstructions (the pre-knowledge-base `LIVE_TEST_RESULTS.md`) were
distilled into [`live-test-2026-03-16.md`](live-test-2026-03-16.md) and the dated files above.
