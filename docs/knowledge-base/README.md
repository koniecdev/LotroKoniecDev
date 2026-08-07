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

### LOTRO Companion Data Model — the `(FileId, GossipId)` join (feeds spec 0008)
Detailed analysis: [lotro-companion-data-model.md](lotro-companion-data-model.md)
- Their published XML stores every translatable game-object field as the literal token `key:<FileId>:<GossipId>` — **deterministic, zero-heuristic join** with our `Translations` identity, proven three ways (their labels ⇔ our export; structural tokens ⇔ live 48.7 export; locale-stable keys)
- Records keyed by DID (`1879xxxxxx`); quests (14,974) + deeds (5,394) ≈ 215k token refs with semantic roles (name/description/objective N/dialog/progress); our TMS layer is named **Catalog** (never "entity")
- `lotro-data` GitHub repo **is** the live dataset (updated within days of each patch); labels not needed — our English comes from our own export
- Caveats: subset coverage, join on keys never text (`${PLAYER}` vs `<--DO_NOT_TOUCH!-->`), version skew tolerated as dangling refs, no LICENSE (attribution = courtesy decision)

### DAT Protection: NOT NEEDED — Translations Survive Updates (incl. MAJOR), per-SubFile
Detailed analysis: [dat-protection.md](dat-protection.md)
- **PROVEN by 9 independent tests** including majors 47.2→48.0 (2026-04-23) and 48.8→49.1 (2026-08-02)
- Launcher uses chunk-based patching — fragments in untouched chunks stay byte-for-byte intact
- **Refined 2026-08-02: survival is per-SubFile, not per-fragment** — an update that modifies a
  SubFile replaces the whole chunk and reverts our fragments inside it (first observed 48.8→49.1:
  1/8 reverted); repair = normal re-patch / the spec-0001 invalidation loop
- `attrib +R` protection is unnecessary; legacy `HandleUpdatePath` is actively harmful; vnum-trigger is dead
- Simplified flow (hash-based patch + fire-and-forget) is fully validated

### Live Test — Major Update 2026-04-23 (47.2 → 48.0)
Detailed analysis: [live-test-2026-04-23.md](live-test-2026-04-23.md) · raw intel: [update-48.0/](update-48.0/)
- **First major update since simplified flow existed** — 47.2 → 48.0 (Hatokáli Fells + Rhûn content)
- 4 independent survival channels verified: in-game, export presence, diff stability, launch log
- datexport.dll READ + WRITE paths confirmed compatible with 48.0 DAT schema
- Program Files (x86) perms gotcha: first launch failed non-elevated, retry as admin worked — installer needs UAC manifest

### Live Test — Update 2026-06-25 (48.0 → 48.7)
Detailed analysis: [live-test-2026-06-25.md](live-test-2026-06-25.md) · committed artifacts: [update-48.7/](update-48.7/) · raw intel: `intel/update-48.7/`
- **Second live update; first via the SKIP-path branch** — translations already resident (hash match) → launch only fires the launcher
- 4 independent survival channels verified again; 8/8 byte-identical across 204 diff hunks
- Translations persisted untouched across the whole 48.x cycle (Apr→Jun, identical DAT SHA256 `833C22DE…1826`) with zero re-patch
- datexport.dll READ + WRITE confirmed on 48.7 DAT schema; vnum 112/3 unchanged (4th cycle); forum-fetcher independently returned "48.7"
- DAT grew by exactly +1 MiB = one allocation block (reinforces chunk-based model)

### Live Test — 2026-07-11 (48.8) — first real-world AUDIT-SEC run
Detailed analysis: [live-test-2026-07-11.md](live-test-2026-07-11.md)
- All 7 AUDIT-SEC PRs (#422-429) validated on real Windows for the first time: 42/42 Infrastructure + 23/23 E2E on a main worktree
- AUDIT-SEC-02 (launcher Authenticode) + AUDIT-SEC-03 (restricted DLL search path) confirmed by a live SKIP launch and a forced live PATCH (8/8, 0 warnings) against the real signed launcher and production DAT
- Live forum fetch returned 48.8 while vnum stayed 112/3 — 5th unchanged cycle
- `launch`'s SKIP path never refreshes the stored ForumVersion — only a standalone `patch` does (code-verified); the manual export→import gap is decided in #443 + ADR-0030

### Live Test — Major Update 2026-08-02 (48.8 → 49.1 "In Good Company")
Detailed analysis: [live-test-2026-08-02.md](live-test-2026-08-02.md) · committed artifacts: [update-49/](update-49/) · raw intel: `intel/update-49/`
- **Third live update-test; first MAJOR since 48.0 and first with the TMS deployed** — U49 released 2026-07-22, client landed on 49.1
- **Survival 7/8 — first real casualty in 9 tests, mechanism fully understood:** SSG added 4 fragments to SubFile 620757435 → launcher replaced the whole chunk → our fragment reverted to its (unchanged) English original; counter-proof: surviving pairs' SubFiles kept identical fragment counts
- Content-wise a full major (+7,192 fragments — more than 48.0) at a DAT growth of exactly +1 MiB (in-place chunk rewrites; second consecutive equal-1-MiB growth)
- datexport.dll READ+WRITE confirmed on 49.1; vnum 112/3 (6th cycle); forum-fetcher returned "49.1"
- Our `launch` deliberately skipped this cycle (LEGAL-08 made repo polish.txt synthetic — hash mismatch would have patched synthetic text into the live game); update ran through the official launcher

### Live Test — Chunk Patches 2026-03-16..22
Detailed analysis: [live-test-2026-03-16.md](live-test-2026-03-16.md)
- 5 chunk-patch tests, all simplified-flow branches verified
- Legacy flow: harmful (double UAC, kills game) — confirmed bad
- SKIP path: 2026-03-22 confirmed (hash match → launch in 255ms)

### datexport.dll — the read-only open flag is bit `0x4`, and `export` needs NO elevation (2026-08-07)
Detailed analysis: [datexport-readonly-open-2026-08-07.md](datexport-readonly-open-2026-08-07.md)
- `OpenFlagsRead = 2` never set the read-only bit, so every "read" open asked the OS for `GENERIC_READ|`**`WRITE`** and failed on a file we cannot write; `= 6` (`0x2|0x4`) is the whole fix
- Proven three ways: x86 disassembly of the open helper (`0x10012bb0`), a flag matrix over `attrib +R` / ACL / writable, and a **non-elevated `export` off the live Program Files DAT** — exit 0, DAT byte-identical
- **Measurement trap:** `icacls /deny "(W)"` also denies `SYNCHRONIZE`, so it blocks reads and is not a Program Files proxy — build one with `/inheritance:r /grant:r "user:(RX)"` and assert both preconditions
- Supersedes the "export from a backup copy" workaround in the 48.7 / 49 baselines; #443 Option A is delivered (#629)

### DAT Vnum — definitively schema-version, not content-version
Detailed analysis: [vnum-observations.md](vnum-observations.md)
- Vnum 112/3 unchanged across 45.x → 47.x → **48.0 major** → 48.7 → 48.8 → **49.1 major** — 6 cycles (two majors), zero movement (latest: [live-test-2026-08-02.md](live-test-2026-08-02.md))
- Vnum = DAT binary schema version, NOT content version — any vnum-triggered logic is dead
- Forum version ("48.0") is the reliable content identifier

### DAT Export Diffs
Detailed analysis: [dat-export-diff-2026-03-22.md](dat-export-diff-2026-03-22.md)
- 47.1→47.2: ~5000 lines, skill rewording
- 47.2→48.0: 587 hunks, +4,231 fragments, new region — stats in [update-48.0/RESULTS.md](update-48.0/RESULTS.md); the raw diff lives in the gitignored `intel/` (repo copy removed 2026-07, LEGAL-08 — verbatim game text)
- 48.8→49.1: 694 hunks, +7,192 fragments — stats in [update-49/RESULTS.md](update-49/RESULTS.md); raw diff only in the gitignored `intel/update-49/` (LEGAL-08)

### LOTRO Update History
Detailed analysis: [lotro-update-history.md](lotro-update-history.md)
- 45.1 → 45.4.1 → 46 → 46.1 → 47 (major) → 47.1 → 47.1.1 → 47.2 → 48.0 (major, 2026-04-23) → 48.7 (2026-06-25) → 48.8 (observed 2026-07-11) → 49 "In Good Company" (major, 2026-07-22) → 49.1 (observed 2026-08-02)
- 48.0 content: Hatokáli Fells, Rhûn expansion, Deluxe housing, Edit UI feature

### Game Update Detection & Translation Versioning
Detailed analysis: [update-detection-strategy.md](update-detection-strategy.md)
- Vnum-triggered re-patch is WRONG (would never fire + would be destructive if it did)
- Only valid local trigger: translation file hash change
- Target model superseded by `docs/specs/0001-game-update-lifecycle-and-translation-invalidation.md`
  (forum-only `GameVersion` lifecycle + status-based invalidation); the core insight stands
- Simplified flow is CORRECT as MVP — hash-based trigger naturally evolves into the API model
- Automation of the manual export→upload ceremony re-examined 2026-07-11: `docs/adr/0030` (stays manual, VM runner deferred, #85 gains an e-mail alert)

### VM Test Plan (Friend's 2-Year-Old LOTRO)
Detailed analysis: [vm-test-plan.md](vm-test-plan.md)
- Friend has ~2-year-old LOTRO — potential goldmine for testing a pre-45 version jump
- VM approach: copy his files → snapshot → infinite test attempts
- No registry needed (zero LOTRO registry entries on user's PC)
- Never executed; the separate VM-as-unattended-export-runner idea was deferred in ADR-0030 (2026-07-11)

### Project Strategy Decisions
Detailed analysis: [project-strategy.md](project-strategy.md)
- OSS code (patcher), controlled translations (web platform with review)
- Glossary/style guide critical for consistency (e.g., Tolkien proper nouns)
- Web platform (M3) = for translators; WPF app (M4) = for gamers
- Three presentation layers (CLI + Web + WPF) over shared handlers — the file's "MediatR" wording predates ADR-0001 (no mediator; in-house handler interfaces)

---

## `update-48.0/` — raw intel from the 48.0 major-update test (2026-04-23)

Text evidence behind the 48.0 findings. The multi-GB DAT backups and 82 MB full exports are intentionally
**not** committed (they live in the gitignored `intel/` folder and are regenerable from a LOTRO install);
only the irreplaceable text artifacts are preserved here:

| File | What it is |
|------|-----------|
| [`BASELINE.md`](update-48.0/BASELINE.md) | Pre-update 47.2 snapshot: DAT hash, export stats, the 8 translation pairs |
| [`RESULTS.md`](update-48.0/RESULTS.md) | Full 47.2→48.0 test results, metrics, verdict |
| `diff-47.2-vs-48.0.txt` | Unified diff of the two full exports (587 hunks) — repo copy removed 2026-07 (LEGAL-08, verbatim game text); survives only in the gitignored `intel/update-48.0/`, stats preserved in `RESULTS.md` |
| [`launch-during-update.log`](update-48.0/launch-during-update.log) | Serilog of the launch flow during the update |
| `polish-pre-48.txt` | `polish.txt` snapshot at test time |
| `version-file-pre-48.txt` / `version-file-post-48.txt` | Version file before/after the run |

---

## `update-48.7/` — committed text artifacts from the 48.0 → 48.7 test (2026-06-25)

Same shape as `update-48.0/`. The 1.76 GB DAT backups + 78 MB full exports live in the gitignored
`intel/update-48.7/`; committed here are the irreplaceable text artifacts:

| File | What it is |
|------|-----------|
| [`BASELINE.md`](update-48.7/BASELINE.md) | Pre-update 48.0 snapshot: DAT hash, export stats, the 8 translation pairs |
| [`RESULTS.md`](update-48.7/RESULTS.md) | Full 48.0→48.7 results, metrics, verdict |
| `diff-48.0-vs-48.7.txt` | Unified diff of the two full exports (131 KB, 204 hunks) — repo copy removed 2026-07 (LEGAL-08, verbatim game text); survives only in the gitignored `intel/update-48.7/`, stats preserved in `RESULTS.md` |
| `polish-pre-48.7.txt` | `polish.txt` snapshot at test time |
| `version-file-pre-48.7.txt` | `48|112|3|<hash>` before the run |

No separate launch log was committed for 48.7 — the SKIP-path launch timeline is captured inside
[`RESULTS.md`](update-48.7/RESULTS.md) ("Launch log timeline"); raw logs live in the gitignored `intel/update-48.7/`.

---

## `update-49/` — committed text artifacts from the 48.8 → 49.1 test (2026-08-02)

Same shape as the folders above (quest text in synthetic LEGAL-08 equivalents). DAT backups
(~1.76 GB each), full exports (82.6/83.1 MB) and the raw diff live in the gitignored
`intel/update-49/`; committed here:

| File | What it is |
|------|-----------|
| [`BASELINE.md`](update-49/BASELINE.md) | Pre-update 48.8 snapshot: DAT hash, export stats, the polish.txt fork after LEGAL-08 (repo file synthetic vs DAT-resident set), 8/8 survival baseline |
| [`RESULTS.md`](update-49/RESULTS.md) | Full 48.8→49.1 results: 7/8 survival, the per-SubFile revert mechanism + counter-proof, diff stats, vnum, WRITE test |

No launch log this cycle — our `launch` was deliberately skipped (see RESULTS §deviation).

---

## Related committed docs (broader context)

- [`../../CLAUDE.md`](../../CLAUDE.md) — project memory: architecture, roadmap digest, house rules (live backlog: `gh issue list`)
- [`../RUSSIAN_PROJECT_RESEARCH.md`](../RUSSIAN_PROJECT_RESEARCH.md) — full raw Russian-project research (dated 2026-02-09; [russian-project.md](russian-project.md) is its distilled digest)

Earlier raw launch-flow reconstructions (the pre-knowledge-base `LIVE_TEST_RESULTS.md`) were
distilled into [`live-test-2026-03-16.md`](live-test-2026-03-16.md) and the dated files above.
