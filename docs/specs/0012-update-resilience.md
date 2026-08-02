# Spec 0012: Update-resilience — launch sentinel, update-day orchestrator, import echo-guard

- **Status:** Draft — open decisions pending (owner); experiments: **E1/E3/E4 done 2026-08-02**
  (branch A viable — DAT free at the login screen; repair-set NOT required; pre-creds kill clean
  3×), E2 monitor ready for the next SSG update (`scripts/experiments/`, results:
  `docs/knowledge-base/update-49/RESULTS.md` §Experiments — incl. finding E1-F1: DAT mtime is
  volatile, which forces the Tier-0 fingerprint revision below)
- **Date:** 2026-08-02 (seeded from the live-test 48.8→49.1 findings + owner discussion, same day)
- **Author:** Artur Koniec (problem framing + orchestrator/kill concept) — structured against code,
  spec 0001 and the knowledge base by Claude
- **Related:** `docs/knowledge-base/update-49/{BASELINE,RESULTS}.md` (empirics + scenario analysis),
  `docs/knowledge-base/live-test-2026-08-02.md`, `docs/knowledge-base/dat-protection.md`
  (per-SubFile survival model), spec 0001 (game-update lifecycle — this spec extends it),
  ADR-0030 (manual export ceremony), `docs/RUSSIAN_PROJECT_RESEARCH.md` (Legacy = file-state
  sentinel + `-disablePatch`), TP-00 #377 ("launch sentinel (DAT-repair gap)" — promoted here)

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
  degenerate Tier 0 into a force-re-patch-per-launch. Detection is a **content sentinel**
  instead: on launch, read a small sample of known-translated artifact fragments via the
  existing datexport READ path (milliseconds); any sampled fragment reverted to English ⇒
  **force re-patch with a freshly synced artifact even when the translation-file hash
  matches**. Sample choice: spread across distinct SubFiles (collateral reverts are
  per-SubFile, so K sampled SubFiles detect a wipe of any of them; full certainty needs the
  full artifact row-set — sampling breadth is an implementation knob). One-shot guard per
  detection (no repair loops).
- **Anti-masking handshake:** the distribution endpoint / artifact exposes the GameVersion it
  was generated for. The sentinel force-patches only when artifact-version ≥ live forum version;
  otherwise it defers (fallback-to-English semantics preserved — never re-apply pre-update
  Polish over fresh English; a dropped artifact row can NEVER un-write stale Polish, because
  patch does not write absences).
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
  (30+ s no writes + launcher idle) because kill is the one invasive act.
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
- **Artifact metadata:** GameVersion of generation (needed by the Tier-0 handshake) + the
  touched-SubFiles set per version (repair-set).

## Experiments (inputs to final design; all local; Windows box)

| # | Question | Procedure | Decides | Status |
|---|----------|-----------|---------|--------|
| E1 | Does the launcher hold the DAT at the login screen? | Launcher at login screen → elevated RW-open probe (`scripts/experiments/e1-rw-probe.ps1`) | A vs B as the dominant branch | ✅ **OPEN-OK at login screen ⇒ branch A viable & dominant**; in-game control LOCKED (0x80070020) |
| E4 | Is a pre-creds launcher kill clean? | Kill at login screen → relaunch → verify straight-to-login + game boots | Safety of branch B | ✅ **clean, 3× reproduced** — relaunch indistinguishable from a normal start (UAC → DAT check → login); game boots fine |
| E3 | Full-corpus patch duration | Synthetic ~100k+-row polish.txt → patch a DAT COPY → time it | Repair-set: optional vs required | ✅ **800,864 rows = 14.7 s; 21,660 = 5.6 s ⇒ repair-set optional** |
| E2 | Handle behavior across a real update cycle (download vs apply bursts; slow-network hour-long updates) | Next SSG update, or launcher repair/verify mode; observe probe + writes timeline (`scripts/experiments/e2-dat-handle-monitor.ps1`) | Quiesce windows; whether probe-success can occur mid-update | ⏳ monitor ready, awaits next SSG update |

New lock-anatomy facts (2026-08-02, E1/E4 pass): `LotroLauncher.exe` manifests `asInvoker` and
the game dir ACL grants Users only RX, but **the launcher prompts UAC and runs elevated on every
start** — that's how it writes the DAT despite the ACL (and why our elevated orchestrator can
kill it). **E1-F1: the launcher writes the DAT during its startup check on EVERY launch** (and
the client writes again at session start), so DAT mtime is NOT a "someone patched content"
signal — this kills the size+mtime fingerprint and motivates the content-sentinel revision in
Tier 0. Probes must run elevated (a non-elevated run reports an ACL ACCESS-DENIED that masks
the sharing state). The 64-bit client process is `lotroclient64` (`x64\lotroclient64.exe`).

Note on slow updates (owner's point): the RW probe is the **primary** signal, quiesce only a
debounce. If E2 shows the launcher holds the DAT continuously until done, probe alone is
sufficient. If it releases between download/apply bursts, probe-success can occur mid-update —
harmless for patching (convergent loop), decisive only for the kill branch, hence the
conservative quiesce there. An hour-long update just means the watcher idles for an hour
(FileSystemWatcher cost ≈ zero).

## Open decisions (owner — extracted, not invented)

- **Q1 — MVP scope:** Tier 0 only, or Tier 0 + handshake (recommended), or Tier 0+1 together?
- **Q2 — One spec or two:** keep client sentinel/orchestrator and server echo-guard in this one
  spec (recommended — two sides of the same coin) or split?
- **Q3 — `-disablePatch` takeover:** park with an M4 revisit trigger (recommended) or analyze now?
- **Q4 — Kill branch (B):** default-on with one-shot guard, or opt-in setting?
- **Q5 — Placement/priority:** promote from TP-00 #377 into dedicated tickets gated before public
  launch — confirm milestone/labels.

## Acceptance criteria (draft — final after Q1–Q5)

- [ ] After a simulated chunk-wipe (write-test DAT with a reverted SubFile), the next launch
      detects the revert via the content sentinel and restores every artifact row (Tier 0).
- [ ] Sentinel never patches with an artifact older than the live forum version (handshake).
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
