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
  update-cycle simulator; big-major burst shape still worth observing at the next real SSG major).
  **Amended 2026-08-17** — the owner's no-masking **invariant** + **ADR-0047** (per-row source
  guard, #659) and the Tier-1 re-cut after the #566 adversarial review (Q6–Q8; E6 = #660).
  **E6 done 2026-10-10** on a real cumulative 47.2→49.7 update + forced-downgrade replays: a
  `FileSystemWatcher` sees the launcher's writes *during* the hold (gap ≤ 0.94 s, also with no
  polling), but in a multi-DAT update the English DAT was held **108.6 s with no write** before
  its own apply — silence on our DAT is not "update finished". **Owner decisions 2026-10-10
  (Q9, Q10): branch B is cut from MVP, and the re-patch trigger is an E5 snapshot diff, not a
  size change.**
  Results: `docs/knowledge-base/update-49/RESULTS.md` §E6
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
   major**; 98.5% survives byte-for-byte. *Measured 2026-10-10 (E7, #1094):* a fully marked
   corpus over the real cumulative 47.2→49.7 update (two majors) lost **2.97%** of its rows
   (2.57% collateral, 0.30% English changed, 0.11% removed); per-SubFile with no exception —
   `docs/knowledge-base/e7-full-corpus-survival-2026-10-10.md`.
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
  *Amended 2026-08-17 (ADR-0047):* the repair itself writes only through the per-row source guard —
  in a wiped SubFile, a row whose fragment holds the English of its `source_digest` is restored
  (collateral revert), a row holding any other text is `source moved` and skipped (the TMS
  re-translates it). Detection (E5 metadata: *which SubFiles were replaced*) and admission
  (ADR-0047: *which rows may be written*) are complementary layers; the guard is what makes the
  repair safe without a verdict and offline (bullets below), and the snapshot refresh after a
  successful repair is the self-limit — no persisted one-shot marker (its "per DAT-fingerprint"
  key died with E1-F1 and was never replaced).
- **Anti-masking handshake** (revised by **ADR-0045**, 2026-08-06 — the client never reads
  lotro.com): the distribution response carries the GameVersion the artifact was generated for
  **and a server-computed staleness verdict** ("does the TMS know an Unprocessed version newer
  than this artifact?"). ~~The sentinel force-patches only when the server says the artifact is
  current; otherwise it defers~~ (fallback-to-English semantics preserved — never re-apply
  pre-update Polish over fresh English; a dropped artifact row can NEVER un-write stale Polish,
  because patch does not write absences). The client performs no version comparison — version
  ordering stays in the TMS domain, deployable. ~~Unknown verdict, missing artifact version or an
  unreachable TMS ⇒ treat as "do not force-patch".~~ The verdict is valid only for the response
  that carried it and is never cached as authority. ~~**Coverage limit:** the handshake gates the
  *forced* repair only; the routine hash-triggered patch is ungated (ADR-0045 Consequences).~~
  *Amended 2026-08-17 (ADR-0047 §5):* **the verdict no longer gates the repair.** The no-masking
  invariant is enforced per row at write time (artifact `source_digest` + local ledger — the
  patcher writes a row only over the English it was translated from or over its own earlier
  write), which also covers the pre-registration window the verdict never could. The verdict and
  the artifact GameVersion (#562) stay as the preflight/UI signal; the sentinel repairs from the
  local artifact whatever the verdict says.
- ~~Offline ⇒ never force-patch; proceed to launch (degrade to English for wiped rows).~~
  *Amended 2026-08-17 (ADR-0047):* offline ⇒ repair from the local artifact is **allowed** — the
  guard makes it safe row by row; the launch still never blocks on the network (spec 0001 Q5).

### Tier 1 — update-day orchestrator (atomic UX; branches A/B/C)

The elevated launch process stays alive after spawning the official launcher and watches **file
state, not process semantics** (FileSystemWatcher + RW-open probe on the DAT):

- **A — silent in-place patch:** probe succeeds while launcher sits at the login screen (DAT
  released after patch phase) ⇒ patch during credential typing. No kill, no restart, invisible.
- ~~**B — pre-creds launcher kill:** update quiesced but handle still held and `lotroclient64` not
  started ⇒ kill the LAUNCHER (pre-session UI shell — NOT the legacy client-kill), patch,
  relaunch launcher. One login total, reads as a normal update flow.~~ *Cut from MVP 2026-10-10
  (Q9).*
- **C — player was faster (client running):** touch nothing; Tier 0 repairs next launch.
- **Convergent re-patch loop:** after our patch, keep watching until game start; if the launcher
  writes the DAT again (next chunk burst), re-probe and re-patch. Early patches are harmless by
  construction; the last write wins. ~~Kill (branch B) remains gated on a conservative quiesce
  (30+ s no writes) because kill is the one invasive act.~~ (B cut, Q9.) **E2-confirmed:** the
  download phase leaves the DAT free (~11 s window in the forced 48.8→49.1 replay) so a probe
  CAN succeed mid-update — the loop is what makes that harmless; the observed apply burst was
  ~1 s, making the 30 s quiesce very conservative for deltas of this size.
- Rejected detectors: process-lifecycle signals (launcher exit / game start — structurally too
  late, that was legacy's error), and screenshot+LLM login-screen detection (dominated by the
  local file-state probe on cost, latency, offline operation, privacy and determinism).

**Tier 1 rules — amended 2026-08-17** (adversarial review of #566; ADR-0047). The bullets above
stay as the picture; where they are looser than the rules below, the rules win:

1. **Every Tier-1 write goes through the guarded `PatchingService` (ADR-0047).** That is what makes
   the pre-update artifact safe to apply *after* the launcher's burst: rows whose English changed
   are skipped as `source moved`, collateral reverts are repaired. Without the guard the
   post-apply patch masks by construction — #566 depends on #659.
2. **What triggers a (re-)patch:** ~~a **foreign** write burst that **changed the DAT size**
   (the apply signature — E2: 1,893,807,856 → 1,894,856,432 B; the launcher's every-start write
   leaves the size unchanged, E1-F1) followed by a successful probe~~ **a foreign write burst
   after which an E5 snapshot diff (open + `GetSubfileSizes`, ~0.14 s, against the snapshot taken
   after our last patch — the same mechanism as Tier 0, #565) shows a moved iteration or a
   vanished FileId, followed by a successful probe** (*Q10, 2026-10-10*), or a pending patch (hash
   changed / sentinel detection) with a successful probe. The bare "the launcher wrote" event is
   **not** a trigger — read literally it fires on every ordinary launch and degenerates into a
   10–15 s re-patch per start, the very failure that killed the size+mtime fingerprint. Own writes
   are filtered by suppressing the watcher during the patch plus a drain window (a generation
   counter, never mtime — RESULTS.md: mtime volatility is unpredictable); a watcher buffer
   overflow counts as activity, not silence.
   *Why Q10 (E6, #660):* from the watcher's side the two shapes do differ — the every-start write
   is one moment of lastwrite/other events with no size event, an apply is 10–19 moments over
   3+ s with size events. But "size changed" is not a general apply signature: 49.4 (2026-08-22)
   was a real update that left the DAT size unchanged, so a size trigger misses such an apply. The
   every-start write never moved the E5 snapshot (E5, 49.4, E6 run 4); every apply measured so far
   did (E5, 49.4, E6, and E7 with 1,661/1,661 affected SubFiles covered). The watcher stays the
   cheap "someone wrote — check" wake-up; the snapshot decides.
3. **Branch B — cut from MVP (Q9, 2026-10-10).** The text of this rule is kept only as the
   starting point if B ever comes back. ~~**Branch B is bounded per run, not "per detection":**
   **at most one kill per orchestrator run**
   (one launch invocation). Preconditions, all required: an apply burst was observed in this run
   (size changed) · a patch is pending · the probe has failed for 30+ s with no watcher activity ·
   no `lotroclient64`. After the kill: guarded patch, then **relaunch unconditionally** (a failed
   patch logs and still relaunches — never leave the player with a launcher we killed; note the
   sibling `SimplifiedGameLaunchingStrategy` returns `RepatchFailed` *before* launching, do not
   mirror that here). After the kill B is **disarmed for the rest of the run**; A and the loop stay
   armed. "Launcher idle" is dropped — undefined; the list above is exhaustive.~~
4. **B default-on was conditional on E6 (#660)** (historical — answered by the verdict below). Its only safety gate is "no writes for 30 s while
   the handle is held", and no experiment has shown that a watcher sees writes *during* a hold (E2
   polled size/mtime, which moved only at burst end). If E6 says the watcher is blind mid-burst, a
   long apply reads as silence and B kills mid-write — the case E4 never tested; then B ships
   opt-in. Third-party holders (AV, backup, indexer, a second launcher) satisfy B's precondition
   too — the one-kill cap is what keeps that harmless.
   **E6 verdict (2026-10-10, #660): the quiesce is observable, but it is not a safe kill signal.
   Owner decision (Q9): B is cut from MVP.** The watcher is not blind:
   it reported the launcher's writes during the hold in all three apply runs, at most 0.94 s
   apart, also with our probe switched off — so by this rule alone B could be default-on. But the
   quiesce does not mean what B needs. In the real 47.2→49.7 update the launcher patched at least
   17 DATs one after another and held the English DAT for 108.6 s **with no write at all** before
   it reached it, so "30 s of silence while held" was true in the middle of the update. Rule 3
   kept B disarmed there only because the English DAT happened to come at the end. And B was
   never needed: in every observed update (E2, 49.4, E6 runs) the DAT was released the moment
   patching ended, so branch A had its window. If B ever comes back — only on evidence that a
   real update leaves the DAT held after patching ends — it should also require the
   launcher's own "Data patching complete" line in
   `%LOCALAPPDATA%\The Lord of the Rings Online\PatchClient.log` (end of the whole patch; a
   ring-buffer log with an undocumented format — a heuristic, not a contract).
5. **Terminators (not detectors):** game start (`lotroclient64` alive — by process **name**, not the
   spawned PID: the launcher re-execs itself for UAC and `GameLauncher` drops the handle),
   launcher exit with no client, and a wall-clock cap (implementation knob). On any terminator: one
   last guarded patch if pending and the probe succeeds, then exit. Rejecting process signals as
   *detectors* (structurally late) never forbade them as *terminators* — the design already uses
   game start as one.
6. **Play-mid-patch is accepted for MVP:** a client started during our exclusive hold cannot open
   the DAT (E1: the client needs it for the whole session). Today's real artifact patches in
   seconds, not the synthetic 14.7 s; the M4 GUI owns the "patching, please wait" UX; revisit the
   repair-set (5.6 s, E3) if a real update day shows it hurting.
7. Naming: `lotroclient64` (`x64\lotroclient64.exe`) everywhere; `IGameProcessDetector` cannot tell
   the launcher from the client — branch C and the terminators need their own port.

### Repair-set optimization (E3 verdict 2026-08-02: OPTIONAL, not required)

TMS knows from the import diff exactly which SubFiles a version touched. Expose that set;
the client repairs only artifact rows in touched SubFiles (~14k fragments for U49 instead of
the full corpus). **E3 measured (Release CLI, DAT copy, update-day-shaped run): full corpus
800,864 rows = 14.7 s wall clock end-to-end (~10–11 s net patch); repair-set-sized 21,660 rows
= 5.6 s.** A full-corpus re-patch fits the login window with a wide margin, so the repair-set
drops out of the MVP into an optional later optimization (less I/O on the DAT, marginally
faster repair) — not a correctness or UX requirement.

### Server side — import echo-guard + source hygiene (extends spec 0001)

- **Echo-guard (#563 UR-20 — implemented 2026-08-17):** on import, an incoming row whose text
  equals the row's current Polish content is an echo of our own patch ⇒ treat as Unchanged (do not
  touch SourceText, do not invalidate). As built: the diff compares the incoming source hash against
  a second per-row hash, the **echo hash** = `(TranslatedText, Source.ArgsOrder, Source.ArgsId)` —
  the exact triple the artifact carries and a patched DAT exports back (the patcher writes the text
  verbatim, never changes the argument count, and the exporter re-emits identity args from that
  count). The source check runs first, so an already-poisoned row (source == Polish, see #564) is
  plain Unchanged, never an echo. An echo on a soft-removed row follows the identical-source re-add
  rule (restore). A different Polish text — e.g. an older Polish still resident after a re-edit
  (approve P1 → admin patches → translator re-edits to P2 → the next export echoes P1) — is
  indistinguishable from a real change and re-creates a poisoned source of the kind #564 repairs;
  catching it needs the row's Polish history (TP-15 / #50, post-MVP). Echoes are counted in
  `ImportSummary.Echoed` (a subset of `Unchanged`; the import page shows it as "W tym echo patcha")
  and in the import-passes log line — observability only, no warning: with today's manual ceremony
  every export comes from a patched DAT, so echoes are the norm, not an anomaly. **Interaction
  with the invariant (ADR-0047 Context):** an echo also hides a row whose *new* English our patch
  overwrote in the pre-import window, so until #659 lands the post-update ceremony is export →
  import first, patch afterwards.
- **One-time source repair** for already-poisoned rows (today: 8).
- **Pristine-source direction (optional, Q-gated):** generate a revert file from TMS SourceText
  before export, or keep a pristine DAT copy (the Russians re-download the original DAT for the
  same reason).
- **Artifact metadata:** GameVersion of generation + the staleness verdict derived from it (both
  needed by the Tier-0 handshake — ADR-0045), plus the touched-SubFiles set per version
  (repair-set). The artifact carries no version today, so this is a new column + migration, not
  an exposure of something already stored.
- **Version detection feeds the update-check channel, no longer the repair (ADR-0045 §4 as
  superseded by ADR-0047):** the verdict is only as early as the TMS's knowledge of a new version,
  so the forum watcher (#85) stays in this milestone for the preflight/UI signal and for the TMS
  learning versions early — but the client repair no longer waits for it (admission is per row,
  ADR-0047). Its detections count immediately and are dismissible after the fact; #624 must land
  first, because a wrongly registered version is currently unrecoverable once superseded.

## Experiments (inputs to final design; all local; Windows box)

| # | Question | Procedure | Decides | Status |
|---|----------|-----------|---------|--------|
| E1 | Does the launcher hold the DAT at the login screen? | Launcher at login screen → elevated RW-open probe (`scripts/experiments/e1-rw-probe.ps1`) | A vs B as the dominant branch | ✅ **OPEN-OK at login screen ⇒ branch A viable & dominant**; in-game control LOCKED (0x80070020) |
| E4 | Is a pre-creds launcher kill clean? | Kill at login screen → relaunch → verify straight-to-login + game boots | Safety of branch B | ✅ **clean, 3× reproduced** — relaunch indistinguishable from a normal start (UAC → DAT check → login); game boots fine |
| E3 | Full-corpus patch duration | Synthetic ~100k+-row polish.txt → patch a DAT COPY → time it | Repair-set: optional vs required | ✅ **800,864 rows = 14.7 s; 21,660 = 5.6 s ⇒ repair-set optional** |
| E2 | Handle behavior across a real update cycle (download vs apply bursts; slow-network hour-long updates) | **Forced downgrade** (swap live DAT for the 48.8 backup → launcher replays the real 48.8→49.1 cycle) + probe/writes timeline (`scripts/experiments/e2-dat-handle-monitor.ps1`) | Quiesce windows; whether probe-success can occur mid-update | ✅ **download leaves the DAT free (~11 s window, probe-success mid-update IS possible ⇒ convergent loop required & sufficient); apply = single ~1 s lock burst; post-update login screen free (branch A holds on update day); client holds the DAT for the whole session.** Caveat: ~5 MB delta — big-major burst shape TBD at the next real SSG major |
| E5 | Does an SSG chunk replacement move per-SubFile size/iteration? (2026-08-17, #656) | Snapshot `(FileId,Size,Iteration,Version)` for all ~310k SubFiles via one `GetSubfileSizes` (`scripts/experiments/e5-subfile-metadata-snapshot.ps1`, non-elevated); diff after-patch vs after-update vs plain-launch; 48.8 backup diffed OFFLINE via `-DatPath` | Tier-0 detection: metadata snapshot vs content sentinel | ✅ **iteration+presence = complete signal: 1,277/1,277 ground-truth coverage (899 iteration + 378 removed, 0 missed); negative control 0/0/0 despite E1-F1; measured live on 49.1→49.3 (57 replaced, all caught); `Version` dead, `size` strict subset; 0.2 s warm / 1.4 s cold ⇒ #565 redesigned around it** |
| E6 | Does a `FileSystemWatcher` see launcher writes while the DAT is held? (2026-10-10, #660) | Real cumulative update 47.2→49.7 on a box idle since 2026-04-18 + two forced-downgrade replays (one with no polling) + a plain start, under `scripts/experiments/e6-fsw-observability.ps1` (E2 monitor + size/lastwrite/other watchers) | Branch-B default (Tier 1 rule 4) | ✅ **watcher sees writes DURING the hold (≤ 0.94 s gaps, 0 overflows, no polling needed); every-start write = 1 moment, no size event; BUT the English DAT was held 108.6 s with no write mid-update (≥ 17 DATs patched in sequence, English at the end) ⇒ silence ≠ done ⇒ B cut from MVP (Q9).** Self-test: a generic held-handle writer is invisible to the watcher until close — this is the launcher's write pattern, not a watcher guarantee |

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
  *Superseded 2026-10-10 by Q9: branch B is cut from MVP.*
  *Amended 2026-08-17:* "per detection" was undefined and re-armed on the same file state after a
  relaunch (its original key, "marker per DAT-fingerprint", died with E1-F1). Now: **at most one
  kill per orchestrator run**, with the exhaustive precondition list of Tier 1 rule 3, and
  **default-on only if E6 (#660) shows the quiesce is observable** — otherwise opt-in.
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

## Decisions — 2026-08-17 (after the #566 adversarial review; owner's invariant)

- **Q6 — Masking is not a trade-off, it is an invariant violation.** Owner, verbatim in intent:
  *if SSG changed a row's English in N+1 and the newest approved translation is for N, the player
  sees English — whatever path writes the DAT.* Every write path (routine hash-patch, `patch`,
  Tier 0, Tier 1 A/B/loop) is enforced **per row at write time in the patcher** — artifact
  `source_digest` + local ledger, **ADR-0047**, ticket **#659** (UR-27), which now blocks #565 and
  #566. ADR-0045's "accepted for now" routine-path masking is superseded; the verdict no longer
  gates the repair. Decided by Claude under the owner's stated rule and mandate ("podejmiesz
  decyzję za mnie") — the rule is the owner's, the mechanism follows from it.
- **Q7 — Branch B: measure the gate before shipping the kill.** E6 (#660): does a
  `FileSystemWatcher` see the launcher's writes while the DAT is held, or only at burst end? Until
  the verdict, B is specified with the per-run cap and the exhaustive preconditions (Tier 1 rule
  3) and its default-on is conditional (rule 4). *Outcome 2026-10-10:* E6 ran — the watcher sees
  the writes, but silence on our DAT is not "update finished"; the owner cut B from MVP (Q9).
- **Q8 — Play-mid-patch accepted for MVP** (Tier 1 rule 6); repair-set stays optional.

## Decisions — 2026-10-10 (after E6 #660 and E7 #1094)

- **Q9 — Branch B is cut from MVP.** E6 shows the quiesce is observable but is not a safe kill
  signal: in a multi-DAT update the launcher held our DAT for 108.6 s with no write while it
  patched other files. And no observed update (E2, 49.4, E6, E7) ever left the DAT held after
  patching ended, so branch A always had its window. B would only be future-proofing for a
  launcher behavior nobody has seen. Owner, in intent: *we react to the launcher when it changes,
  we do not build for how it might behave one day* — SSG shows no sign of changing it. A rare
  case of that kind is still covered: the player sees English until the next game start, and
  Tier 0 (#565) repairs it then. Rule 3 stays, struck through, and rule 4 keeps the E6 verdict;
  together they are the starting point if evidence ever brings B back.
- **Q10 — The Tier 1 re-patch trigger is an E5 snapshot diff, not a DAT size change** (rule 2).
  The size signature misses a real apply that keeps the DAT size (49.4); the snapshot is exact
  in every measured update, costs ~0.14 s, and is the mechanism Tier 0 (#565) already needs.

Ticket cut (2026-08-02, extended 2026-08-06 by ADR-0045). Numbers are allocation order, **not**
execution order — the real chains are:

- **#624** UR-23 superseded version stays retirable → **#85** UR-24 forum watcher
- **#562** UR-22 artifact version + verdict → **#626** UR-25 current-version endpoint + rel →
  **#627** UR-26 patcher drops the forum scrape
- **#562** UR-22 → **#565** UR-10 Tier-0 wipe detection (iteration snapshot per E5) →
  **#566** UR-11 Tier-1 orchestrator → **#567** UR-12 forced-downgrade E2E
- **#563** UR-20 import echo-guard → **#564** UR-21 one-time source repair
- *(added 2026-08-17)* **#563** UR-20 (`SourceHash`) → **#659** UR-27 per-row source guard
  (**#564** UR-21 lands before the first guarded artifact reaches production — ordering, not a
  code dependency; it is owner-run)
  (ADR-0047) → **#565** UR-10 and **#566** UR-11; **#660** UR-28 (E6, owner-run) → the branch-B
  default of **#566**

First real 49.1 import: **#559** (UR-02) — a manual ops run against a live game install, not a
code ticket. It carries no milestone and stays that way: the backlog loop must never pick it up.

## Acceptance criteria (final per Q1–Q5)

- [ ] After a simulated chunk-wipe (write-test DAT with a reverted SubFile), the next launch
      detects the revert via the ~~content sentinel~~ **iteration-snapshot diff (E5 revision)**
      and restores every artifact row (Tier 0).
- [ ] ~~Sentinel force-patches only when the server's staleness verdict says the artifact is
      current, and declines on stale/unknown/offline (handshake — ADR-0045; the client never
      reads lotro.com and never compares versions itself).~~ *Amended 2026-08-17 (ADR-0047):*
      the client still never reads lotro.com and never compares versions; the repair is gated per
      row by the source guard, not by the verdict, and runs offline.
- [ ] **Invariant (ADR-0047, #659):** after the forced-downgrade replay applies 48.8→49.1, a
      patch with the pre-update artifact leaves every row whose English changed in English and
      repairs every collateral-reverted row — on every write path, offline, no version knowledge;
      the `||` golden fixtures on both sides carry `source_digest` and round-trip; a six-column
      translation file patches nothing and says why.
      *E7 evidence (2026-10-10, #1094), CLI `patch` path only:* on a forced-downgrade replay of
      the real 47.2→49.7 update, a guarded re-patch with the pre-update file repaired all 20,228
      collateral reverts, skipped all 2,347 changed rows as `source moved` and wrote 0 Polish over
      changed English. The Tier 0/1 write paths are not built yet, so this stays open.
- [ ] Orchestrator branch A: patch lands between launcher-release and Play without killing
      anything (E1 ✅ confirmed the window exists: DAT free at the login screen, full-corpus
      patch 14.7 s).
- [ ] ~~Orchestrator branch B: at most one launcher kill **per run**, only pre-client, only after
      an observed apply burst + a pending patch + 30 s of no observed writes; relaunch is
      unconditional and reaches login cleanly (E4 ✅ confirmed clean, 3× reproduced — at the login
      screen; a mid-apply kill is E6's question, #660).~~ *Cut from MVP 2026-10-10 (Q9).*
- [ ] Orchestrator loop: the launcher's every-start write (E5 snapshot unchanged) triggers **no**
      re-patch; an apply (E5 snapshot moved — also when the DAT size stays the same, as in 49.4)
      after our patch triggers exactly one guarded re-patch; own writes never re-trigger (Q10).
- [ ] Orchestrator terminates on game start, on launcher exit without a client, and on the
      wall-clock cap — a player who closes the launcher without playing leaves no elevated process
      behind.
- [x] Echo-guard: importing a patched-DAT export against a translated corpus invalidates ONLY
      rows whose English actually changed (fixture: resident-Polish echo rows + one revert) —
      **#563 done 2026-08-17**: `TranslationDiffServiceTests` (echo matrix incl. the U49 shape),
      `ImportExportedTextsTests.Import_ExportFromPatchedDat_…` (echo rows + collateral revert +
      one real English change, end-to-end through the artifact).
- [ ] ~~Repair-set: post-update repair touches only rows in update-touched SubFiles~~ —
      dropped from MVP per E3 (full-corpus re-patch = 14.7 s; repair-set is an optional
      later optimization).

## Assumptions

- Chunk/SubFile replacement model per `dat-protection.md` (9 live tests).
- The official launcher's patcher is resumable/verifying (industry standard; E4/E2 sanity-check —
  both from a coherent state: E4 killed at the login screen, E2 resumed from an older intact DAT;
  a kill mid-apply is unverified, hence Tier 1 rule 4 / E6).
- Patcher/TMS remain separate bounded contexts — repair-set and artifact-version travel through
  the HTTP contract, never shared code.
