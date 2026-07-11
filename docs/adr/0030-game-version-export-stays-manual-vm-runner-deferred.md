# ADR-0030: Game-version export stays manual — VM runner deferred, pipeline instrumented

**Status:** Accepted
**Date:** 2026-07-11
**Decision-makers:** Solo maintainer
**Related:** Patcher (export/launch flow), `docs/specs/0001-game-update-lifecycle-and-translation-invalidation.md` (Out of scope), tickets #443 (TP-11), #85 (M2-18 forum watcher), #384 (TP-07), #52 (Discord notifications), knowledge-base live-test entries

## Context

Spec 0001 built the hard half of the update lifecycle: version-bound import with diff +
invalidation of stale translations. The one fully manual link left is producing a fresh
`exported.txt` after each game update and uploading it. Spec 0001's Out of scope rejected
"automating game updates on a VM to regenerate exports" on cost grounds — a rejection that
implicitly assumed re-provisioning a VM per run. Ticket #443 revisits it with new information:
a persistent, patch-only Windows VM (e.g. `dockurr/windows` on KVM, one durable disk, started
and stopped like a real machine) has a much smaller ongoing cost.

The force that actually matters was named in the #443 discussion: the **staleness window**.
Between an SSG update and the admin's manual export→import, every patcher user actively
re-applies now-obsolete translations onto changed English content — the launcher rewrites
changed subfiles (reverting them to new English), while the distributed `polish.txt` still
carries the old rows until the import diff invalidates them; the CLI auto-download + `patch`
loop re-injects them. Window length = detection latency + the admin's physical access to a
Windows box with LOTRO. Compounding it: a single-maintainer bus factor — admin unavailability
suspends the loop indefinitely.

Facts that constrain the choice today:

- **The pipeline head does not exist yet.** The TMS GameVersion import endpoint (spec 0001
  Contract) is still M2 work-in-progress; an unattended runner would automate the tail of a
  pipeline with a missing head.
- **Cadence and effort are small.** Updates run roughly monthly (48.0 Apr → 48.7 Jun → 48.8 Jul,
  `docs/knowledge-base/`); once at the machine, the ceremony is minutes. The project is
  pre-release with zero production users — the window currently hurts nobody.
- **Three VM prerequisites are unconfirmed:** whether `TurbineLauncher.exe` patches without a
  GPU; whether a silent/patch-only path exists (the Russian project's Elanor uses undocumented
  flags `-disablePatch -nosplash -skiprawdownload` on the same launcher —
  `docs/knowledge-base/russian-project.md` — suggesting more exist); Windows licensing for an
  unattended runner.
- **No cheap host.** The dev Mac has no KVM; Azure nested-virt VM sizes are real money against a
  nearly exhausted credit and the standing FinOps posture (ADR-0020, ADR-0027).
- **Elevation was the other friction.** `DatFileHandler.Open` and `ReadVersion` always passed
  native flag 130 (Read|Write); the write intent is why non-elevated opens of the live DAT failed
  twice empirically — a `launch` on 2026-04-23, an `export` on 2026-06-25
  (`live-test-2026-04-23.md`, `live-test-2026-06-25.md`). #443 Option A (same PR as this ADR)
  switches read paths to flag 2, pending real-Windows confirmation.

## Decision

### 1. The VM/VPS runner is deferred — spec 0001's rejection is upheld, with new reasoning

Not "re-provisioning cost" anymore. Deferred because the pipeline head is missing, three
technical prerequisites are unconfirmed, a KVM host is a standing cost, and the staleness window
has no victims pre-release (YAGNI). The runner design sketched in #443 is preserved there as a
case study, not a backlog item. This includes the research spike: even standing the VM up once
needs a KVM host and hours, and its answers buy nothing actionable until the triggers in §3 fire.

### 2. The manual pipeline is instrumented — three concrete moves

- **Read path de-elevated (#443 Option A, this PR).** `export` and version reads open the DAT
  read-only (native flag 2); `export.bat` stops self-elevating. The ceremony loses its UAC step,
  and any future runner's export task needs no elevation engineering at all.
- **Detection latency bounded (#85 scope amendment).** The M2-18 forum watcher additionally
  notifies the admin **by e-mail** when a new GameVersion is detected (previously: structured log
  only for MVP). Discord webhook stays post-MVP (#52). #85 currently sits in Post-MVP/Backlog per
  its triage comment and the owner signalled on 2026-07-11 the intent to revive it soon; this ADR
  is the scope record, to be mirrored onto the ticket as a comment when it is picked up.
- **Reaction effort scripted (future ticket, after the import endpoint exists).** A one-shot
  "ceremony script" — update LOTRO → `export` → upload to the import endpoint — runnable on any
  Windows box with LOTRO: the admin's gaming PC today, a VM tomorrow. VM-ready by design:
  non-interactive, exit-coded, idempotent. Whether it runs by hand, as a scheduled task, or
  inside a VM is a deployment detail decided when needed.

### 3. Explicit reconsider triggers for the VM

Re-open (new ADR or spec revision) when **any** of:

- (a) real users exist and a staleness window measurably exceeds a few days more than once;
- (b) the import endpoint + ceremony script both exist, making the VM a thin shell around a
  proven script;
- (c) a KVM-capable host becomes available at negligible marginal cost;
- (d) admin availability becomes structurally worse.

Until a trigger fires, no VM work happens — including "just a quick spike".

### 4. The staleness window is an accepted, documented risk

Between an update and the next import, the distributed file may re-apply stale rows onto changed
content. Accepted while pre-release; the e-mail alert plus the scripted ceremony keep the window
as short as the admin can make it, and TMS-side correctness self-heals at first import
(diff invalidation, spec 0001).

## Consequences

### Positive

- Zero new infrastructure, cost, or licensing exposure; FinOps posture intact.
- The decision is reversible on named, checkable conditions instead of vibes.
- #85's scope is sharpened (e-mail alert) while the watcher itself stays unchanged.
- The ceremony script becomes the reusable kernel of *any* future runner — manual, scheduled,
  or VM — so deferring the VM wastes no work.
- Option A lands independent value now: UAC-free export on a real Windows box.

### Negative / Accepted Trade-offs

- The staleness window remains, bounded only by the admin's responsiveness to the e-mail.
- The bus factor is mitigated, not solved: anyone with repo access and a Windows box + LOTRO can
  run the ceremony, but nothing runs unattended.
- The e-mail alert adds a small SMTP need to the TMS side when #85 lands (the AuthSystem already
  ships the pattern to lift).

## Alternatives Considered

### A. Persistent VM/VPS runner now (`dockurr/windows` on a KVM host)

Fully closes the loop and the bus factor. Rejected. The pipeline head (import endpoint) doesn't
exist; GPU-less patching, a silent launcher path, and licensing are all unconfirmed; a KVM host
is a standing cost against a nearly exhausted budget; no users feel the window today.

### B. Research spike only (stand the VM up once, answer the unknowns)

Rejected for now. The spike itself needs the KVM host and non-trivial hours, and its findings are
not actionable until §3's triggers fire; run it as the first step *after* a trigger, not before.

### C. Scheduled task on the admin's gaming PC

Cheapest automation: the box already receives every update because the admin plays. Rejected as a
commitment today — but its kernel is adopted: §2's ceremony script *is* this alternative minus
the scheduler, and adding the scheduled task later is configuration, not code.

### D. Do nothing (pure manual, no instrumentation)

Rejected. Two live tests showed the stored forum version silently staling between rare Windows
sessions (`live-test-2026-07-11.md`); e-mail alert + UAC-free export + a script are near-zero
cost and buy most of the practical value of automation.

## Implementation Notes

- This ADR ships in #443's PR together with: the Option A code change (`DatFileAccess` enum,
  `DatExportNative.OpenFlagsRead`, `IDatFileHandler.Open(string, DatFileAccess)`,
  `DatFileHandler`, `ExportTextsQueryHandler`, `PatchingService`, unit/E2E tests), the
  de-elevated `export.bat`, and `docs/knowledge-base/live-test-2026-07-11.md`.
- `docs/specs/0001-…md` Out of scope gains a dated re-examination pointer to this ADR.
- #85: the e-mail scope is recorded here and mirrored onto the ticket as a comment at pickup;
  implementation lands with that ticket.
- The ceremony-script ticket is cut when the GameVersion import endpoint exists (trigger (b)
  approaches), not now.

## References

- `docs/specs/0001-game-update-lifecycle-and-translation-invalidation.md` — Out of scope + Contract
- Ticket #443 (TP-11) — the revisit request and the preserved VM design sketch
- Tickets #85 (M2-18 forum watcher), #384 (TP-07 lotro-data watcher), #52 (Discord webhook, post-MVP)
- `docs/knowledge-base/live-test-2026-04-23.md`, `live-test-2026-06-25.md`,
  `live-test-2026-07-11.md` — elevation findings + the staleness evidence
- `docs/knowledge-base/russian-project.md` — Elanor's undocumented launcher flags
- ADR-0020, ADR-0027 — the FinOps posture the VM host would violate
