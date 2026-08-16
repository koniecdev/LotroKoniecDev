# Spec 0012: Update-resilience — launch sentinel, update-day orchestrator, import echo-guard

- **Status:** **Agreed** (owner decisions Q1–Q5 resolved 2026-08-02 — see Decisions; ticket cut
  per Q5 in the dedicated **UR** milestone); experiments: **ALL FOUR done 2026-08-02**
  (E1: DAT free at the login screen; E3: full corpus 14.7 s — repair-set not required; E4:
  pre-creds kill clean 3×; E2: full forced-downgrade update cycle recorded — download leaves
  the DAT free, apply is a ~1 s lock burst, login screen post-update free) **+ E5 done
  2026-08-17** (per-SubFile iteration snapshot = complete wipe signal, 1,277/1,277 coverage —
  Tier-0 detection revised below, #565 redesigned). Results:
  `docs/knowledge-base/update-49/RESULTS.md` §Experiments — incl. findings E1-F1 (DAT mtime
  volatile ⇒ Tier-0 content-sentinel revision below) and the forced-downgrade method (repeatable
  update-cycle simulator; big-major burst shape still worth observing at the next real SSG major)
- **Date:** 2026-08-02 (seeded from the live-test 48.8→49.1 findings + owner discussion, same day)
- **Author:** Artur Koniec (problem framing + orchestrator/kill concept) — structured against code,
  spec 0001 and the knowledge base by Claude
- **Related:** `docs/knowledge-base/update-49/{BASELINE,RESULTS}.md` (empirics + scenario analysis),
  `docs/knowledge-base/live-test-2026-08-02.md`, `docs/knowledge-base/dat-protection.md`
  (per-SubFile survival model), spec 0001 (game-update lifecycle — this spec extends it),
  ADR-0030 (manual export ceremony), **ADR-0045** (the game version reaches clients through our
  API — revises the Tier-0 handshake below), `docs/RUSSIAN_PROJECT_RESEARCH.md` (Legacy =
  file-state sentinel + `-disablePatch`), TP-00 #377 ("launch sentinel (DAT-repair gap)" —
  promoted here)

## Business context — what the 49.1 live test proved

Empirics (2026-08-02, `update-49/RESULTS.md`):

1. **Survival is per-SubFile, not per-fragment.** An SSG update that modifies a SubFile replaces
   the whole chunk; every resident translation inside reverts to English even when its own text
   did not change ("collateral revert"). U49: 1,277/277,420 SubFiles touched → on a
   fully-translated resident corpus **≈12,180 valid Polish fragments (1.52%) would revert per
   major**; 98.5% survives byte-for-byte.
2. **Our patch runs BEFORE the official launcher applies the update** (fire-and-forget), so even
   a fully re-approved TMS cannot protect the first post-update session, and — because the
   SKIP/PATCH trigger is translation-file-hash-only — **a restart does not repair anything**.
   A collateral-reverted row is "Unchanged" for the TMS, so no admin work ever regenerates the
   artifact for it; repair happens only as a side effect of unrelated approves.
3. **The admin's export comes from a patched DAT** ("echo"): resident rows carry our Polish as
   "source". Against a clean-English DB this mass-false-invalidates every translated resident
   row on every import; against today's (poisoned, 8-row) DB it accidentally works but the TMS
   loses the real English source.

## Goal

After any SSG update, every player's game returns to full Polish **automatically, with at most
one visible launcher restart and exactly one login**, without ever masking SSG's text
corrections with stale Polish — and TMS imports stay correct at any corpus scale.

## Design — layered tiers (client) + import hardening (server)

### Tier 0 — launch sentinel (fundament; always on; = the Russians' proven Legacy mechanism)

- ~~DAT fingerprint (size + LastWriteTime)~~ **REVISED per E1-F1 (2026-08-02): the launcher
  writes the DAT on EVERY start** (mtime bumps at launcher check and at client start, with no
  translation loss), so a size+mtime fingerprint would false-positive every session and
  degenerate Tier 0 into a force-re-patch-per-launch. ~~Detection is a **content sentinel**
  instead: on launch, read a small sample of known-translated artifact fragments via the
  existing datexport READ path (milliseconds); any sampled fragment reverted to English ⇒
  force re-patch. Sampling breadth is an implementation knob. One-shot guard per detection.~~
  **REVISED AGAIN per E5 (2026-08-17, #656): detection is a per-SubFile ITERATION SNAPSHOT,
  no content reads and no sampling.** E1-F1 killed only the whole-file fingerprint; E5 measured
  the per-SubFile metadata underneath it and it is exactly the signal we wanted: one
  non-elevated read-only open + `GetSubfileSizes` (~0.2 s) returns size+iteration for every
  SubFile; predicate **`iteration moved ∨ FileId vanished` = chunk replaced since our patch**.
  Proven on a live update (49.1→49.3) and against the 48.8→49.1 ground truth: 1,277/1,277
  touched SubFiles covered, 0 missed, negative control all-zero (`Version` is dead; `size` a
  strict subset of `iteration`; our own patch preserves both, so no self-noise; also closes
  the byte-identical-replacement blind spot a content pair-diff has). The snapshot
  (FileId → Iteration) is persisted after each successful patch and refreshed after a
  successful repair — which makes the repair self-limiting, replacing the one-shot marker
  whose key E1-F1 killed. The diff set doubles as the repair set for free. Coverage is total
  by construction, so the sampling-breadth knob disappears. Full data:
  `docs/knowledge-base/update-49/RESULTS.md` §E5; redesigned ticket: #565.
- **Anti-masking handshake** (revised by **ADR-0045**, 2026-08-06 — the client never reads
  lotro.com): the distribution response carries the GameVersion the artifact was generated for
  **and a server-computed staleness verdict** ("does the TMS know an Unprocessed version newer
  than this artifact?"). The sentinel force-patches only when the server says the artifact is
  current; otherwise it defers (fallback-to-English semantics preserved — never re-apply
  pre-update Polish over fresh English; a dropped artifact row can NEVER un-write stale Polish,
  because patch does not write absences). The client performs no version comparison — version
  ordering stays in the TMS domain, deployable. Unknown verdict, missing artifact version or an
  unreachable TMS ⇒ treat as "do not force-patch". The verdict is valid only for the response
  that carried it and is never cached as authority. **Coverage limit:** the handshake gates the
  *forced* repair only; the routine hash-triggered patch is ungated (ADR-0045 Consequences).
- Offline ⇒ never force-patch; proceed to launch (degrade to English for wiped rows).

### Tier 1 — update-day orchestrator (atomic UX; branches A/B/C)

The elevated launch process stays alive after spawning the official launcher and watches **file
state, not process semantics** (FileSystemWatcher + RW-open probe on the DAT):

- **A — silent in-place patch:** probe succeeds while launcher sits at the login screen (DAT
  released after patch phase) ⇒ patch during credential typing. No kill, no restart, invisible.
- **B — pre-creds launcher kill:** update quiesced but handle still held and lotroclient.exe not
  started ⇒ kill the LAUNCHER (pre-session UI shell — NOT the legacy client-kill), patch,
  relaunch launcher. One login total, reads as a normal update flow.
- **C — player was faster (client running):** touch nothing; Tier 0 repairs next launch.
- **Convergent re-patch loop:** after our patch, keep watching until game start; if the launcher
  writes the DAT again (next chunk burst), re-probe and re-patch. Early patches are harmless by
  construction; the last write wins. Kill (branch B) remains gated on a conservative quiesce
  (30+ s no writes + launcher idle) because kill is the one invasive act. **E2-confirmed:** the
  download phase leaves the DAT free (~11 s window in the forced 48.8→49.1 replay) so a probe
  CAN succeed mid-update — the loop is what makes that harmless; the observed apply burst was
  ~1 s, making the 30 s quiesce very conservative for deltas of this size.
- Rejected detectors: process-lifecycle signals (launcher exit / game start — structurally too
  late, that was legacy's error), and screenshot+LLM login-screen detection (dominated by the
  local file-state probe on cost, latency, offline operation, privacy and determinism).

### Repair-set optimization (E3 verdict 2026-08-02: OPTIONAL, not required)

TMS knows from the import diff exactly which SubFiles a version touched. Expose that set;
the client repairs only artifact rows in touched SubFiles (~14k fragments for U49 instead of
the full corpus). **E3 measured (Release CLI, DAT copy, update-day-shaped run): full corpus
800,864 rows = 14.7 s wall clock end-to-end (~10–11 s net patch); repair-set-sized 21,660 rows
= 5.6 s.** A full-corpus re-patch fits the login window with a wide margin, so the repair-set
drops out of the MVP into an optional later optimization (less I/O on the DAT, marginally
faster repair) — not a correctness or UX requirement.

### Server side — import echo-guard + source hygiene (extends spec 0001)

- **Echo-guard:** on import, an incoming row whose text equals the row's current Polish content
  is an echo of our own patch ⇒ treat as Unchanged (do not touch SourceText, do not invalidate).
- **One-time source repair** for already-poisoned rows (today: 8).
- **Pristine-source direction (optional, Q-gated):** generate a revert file from TMS SourceText
  before export, or keep a pristine DAT copy (the Russians re-download the original DAT for the
  same reason).
- **Artifact metadata:** GameVersion of generation + the staleness verdict derived from it (both
  needed by the Tier-0 handshake — ADR-0045), plus the touched-SubFiles set per version
  (repair-set). The artifact carries no version today, so this is a new column + migration, not
  an exposure of something already stored.
- **Version detection is a prerequisite, not a convenience (ADR-0045 §4):** the verdict is only as
  early as the TMS's knowledge of a new version, so the forum watcher (#85) moves into this
  milestone. Its detections count immediately and are dismissible after the fact; #624 must land
  first, because a wrongly registered version is currently unrecoverable once superseded.

## Experiments (inputs to final design; all local; Windows box)

| # | Question | Procedure | Decides | Status |
|---|----------|-----------|---------|--------|
| E1 | Does the launcher hold the DAT at the login screen? | Launcher at login screen → elevated RW-open probe (`scripts/experiments/e1-rw-probe.ps1`) | A vs B as the dominant branch | ✅ **OPEN-OK at login screen ⇒ branch A viable & dominant**; in-game control LOCKED (0x80070020) |
| E4 | Is a pre-creds launcher kill clean? | Kill at login screen → relaunch → verify straight-to-login + game boots | Safety of branch B | ✅ **clean, 3× reproduced** — relaunch indistinguishable from a normal start (UAC → DAT check → login); game boots fine |
| E3 | Full-corpus patch duration | Synthetic ~100k+-row polish.txt → patch a DAT COPY → time it | Repair-set: optional vs required | ✅ **800,864 rows = 14.7 s; 21,660 = 5.6 s ⇒ repair-set optional** |
| E2 | Handle behavior across a real update cycle (download vs apply bursts; slow-network hour-long updates) | **Forced downgrade** (swap live DAT for the 48.8 backup → launcher replays the real 48.8→49.1 cycle) + probe/writes timeline (`scripts/experiments/e2-dat-handle-monitor.ps1`) | Quiesce windows; whether probe-success can occur mid-update | ✅ **download leaves the DAT free (~11 s window, probe-success mid-update IS possible ⇒ convergent loop required & sufficient); apply = single ~1 s lock burst; post-update login screen free (branch A holds on update day); client holds the DAT for the whole session.** Caveat: ~5 MB delta — big-major burst shape TBD at the next real SSG major |
| E5 | Does an SSG chunk replacement move per-SubFile size/iteration? (2026-08-17, #656) | Snapshot `(FileId,Size,Iteration,Version)` for all ~310k SubFiles via one `GetSubfileSizes` (`scripts/experiments/e5-subfile-metadata-snapshot.ps1`, non-elevated); diff after-patch vs after-update vs plain-launch; 48.8 backup diffed OFFLINE via `-DatPath` | Tier-0 detection: metadata snapshot vs content sentinel | ✅ **iteration+presence = complete signal: 1,277/1,277 ground-truth coverage (899 iteration + 378 removed, 0 missed); negative control 0/0/0 despite E1-F1; measured live on 49.1→49.3 (57 replaced, all caught); `Version` dead, `size` strict subset; 0.2 s warm / 1.4 s cold ⇒ #565 redesigned around it** |

New lock-anatomy facts (2026-08-02, E1/E4 pass): `LotroLauncher.exe` manifests `asInvoker` and
the game dir ACL grants Users only RX, but **the launcher prompts UAC and runs elevated on every
start** — that's how it writes the DAT despite the ACL (and why our elevated orchestrator can
kill it). **E1-F1: the launcher writes the DAT during its startup check on EVERY launch** (and
the client writes again at session start), so DAT mtime is NOT a "someone patched content"
signal — this kills the size+mtime fingerprint and motivates the content-sentinel revision in
Tier 0. Probes must run elevated (a non-elevated run reports an ACL ACCESS-DENIED that masks
the sharing state). The 64-bit client process is `lotroclient64` (`x64\lotroclient64.exe`).

Note on slow updates (owner's point): the RW probe is the **primary** signal, quiesce only a
debounce. **E2 resolved this: the launcher releases the DAT for the whole download phase and
locks only for short apply bursts**, so probe-success mid-update is real — harmless for
patching (convergent loop), decisive only for the kill branch, hence the conservative quiesce
there. An hour-long slow-network update just means a long free-DAT download window and an idle
watcher (FileSystemWatcher cost ≈ zero).

## Decisions — resolved by the owner, 2026-08-02 (#558)

- **Q1 — MVP scope: Tier 0 + Tier 1 together** (sentinel + handshake + update-day
  orchestrator). Decided AFTER the four experiments landed: E1/E2 proved the silent branch A
  window exists on update day, E3 bounded the patch cost at 14.7 s full-corpus, E4 proved the
  branch-B fallback is clean — the orchestrator is de-risked enough to ship in the same cut.
- **Q2 — One spec:** this spec covers both sides (client sentinel/orchestrator + server
  echo-guard/metadata); tickets split per bounded context as always.
- **Q3 — `-disablePatch` takeover: parked** with an explicit revisit trigger at M4 kickoff
  (own launcher GUI = the moment full update-day UX control becomes relevant). Not needed for
  the current design: the official launcher stays the updater and 9+1 live tests prove routine
  launcher starts don't erase translations.
- **Q4 — Branch B default-on** with the one-shot guard per detection and the conservative
  quiesce (30+ s; E2 measured a ~1 s apply burst, so the margin is wide). Branch A stays the
  default path; B fires only when A is impossible.
- **Q5 — Placement: dedicated `UR` milestone**, gated before public launch — the patcher side
  (sentinel, orchestrator, forced-downgrade E2E) and the TMS side (echo-guard, artifact
  metadata/handshake, one-time source repair) ship under one milestone. Repair-set endpoint
  deliberately NOT cut (E3 verdict: optional; YAGNI until a real need appears).
  **Amended 2026-08-07 — the `UR-1x` patcher / `UR-2x` TMS band rule is dropped.** It survived
  exactly one round of additions: ADR-0045 cut four more tickets by next-free-number, and UR-26
  (#627) is a patcher ticket sitting in the TMS band. `UR-nn` is a plain counter — the bounded
  context is already in the ticket title, ordering lives in `Depends on`, and no tooling reads
  the digit. Renumbering was rejected: it would break every `UR-nn` reference in ADR-0045, this
  spec and the ticket bodies to restore a signal nothing consumes.

Ticket cut (2026-08-02, extended 2026-08-06 by ADR-0045). Numbers are allocation order, **not**
execution order — the real chains are:

- **#624** UR-23 superseded version stays retirable → **#85** UR-24 forum watcher
- **#562** UR-22 artifact version + verdict → **#626** UR-25 current-version endpoint + rel →
  **#627** UR-26 patcher drops the forum scrape
- **#562** UR-22 → **#565** UR-10 Tier-0 wipe detection (iteration snapshot per E5) →
  **#566** UR-11 Tier-1 orchestrator → **#567** UR-12 forced-downgrade E2E
- **#563** UR-20 import echo-guard → **#564** UR-21 one-time source repair

First real 49.1 import: **#559** (UR-02) — a manual ops run against a live game install, not a
code ticket. It carries no milestone and stays that way: the backlog loop must never pick it up.

## Acceptance criteria (final per Q1–Q5)

- [ ] After a simulated chunk-wipe (write-test DAT with a reverted SubFile), the next launch
      detects the revert via the ~~content sentinel~~ **iteration-snapshot diff (E5 revision)**
      and restores every artifact row (Tier 0).
- [ ] Sentinel force-patches only when the server's staleness verdict says the artifact is
      current, and declines on stale/unknown/offline (handshake — ADR-0045; the client never
      reads lotro.com and never compares versions itself).
- [ ] Orchestrator branch A: patch lands between launcher-release and Play without killing
      anything (E1 ✅ confirmed the window exists: DAT free at the login screen, full-corpus
      patch 14.7 s).
- [ ] Orchestrator branch B: at most one launcher kill per detection, only pre-client,
      only after conservative quiesce; relaunch reaches login cleanly (E4 ✅ confirmed clean,
      3× reproduced).
- [ ] Echo-guard: importing a patched-DAT export against a translated corpus invalidates ONLY
      rows whose English actually changed (fixture: resident-Polish echo rows + one revert).
- [ ] ~~Repair-set: post-update repair touches only rows in update-touched SubFiles~~ —
      dropped from MVP per E3 (full-corpus re-patch = 14.7 s; repair-set is an optional
      later optimization).

## Assumptions

- Chunk/SubFile replacement model per `dat-protection.md` (9 live tests).
- The official launcher's patcher is resumable/verifying (industry standard; E4/E2 sanity-check).
- Patcher/TMS remain separate bounded contexts — repair-set and artifact-version travel through
  the HTTP contract, never shared code.
