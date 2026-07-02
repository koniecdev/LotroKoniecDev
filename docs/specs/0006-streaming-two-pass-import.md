# Spec 0006: Streaming two-pass import — O(batch) memory for the full exported.txt (bulk import Phase 2)

- **Status:** Implemented (2026-07-02, #290 — empirical answers: the ASP.NET-buffered form file is
  seekable, so both passes re-read it directly (the temp-file copy remains as a dead-code
  fallback); chunk size shipped as `Import:ApplyChunkSize` = 5000; the catalog projection is a raw
  Npgsql read because EF's retrying strategy buffers entire result sets (`BufferedDataReader`) —
  an EF-based "stream" re-OOM'd the 792k-row re-import in the incident harness)
- **Date:** 2026-07-02
- **Author:** Claude (interactive session, direction chosen by the owner)
- **Ticket:** #290 (PERF-05 — re-scoped to this spec, 2026-07-02)
- **Related:** spec 0001 (import lifecycle + five outcomes), spec 0004 (bulk import Phase 1 — COPY
  for added rows; this realizes its deferred Phase 2), ADR-0011 (Npgsql COPY write path),
  PR #300 (incident 2026-07-02 + staging-only sizing bridge), incident measurements below

## Business context

The first real full-catalog imports (79 MB, **792,500 rows**) OOM-killed tms-api on both staging
and prod on 2026-07-02 — prod with an **empty catalog**, so the defect is in how the *incoming
file* is handled, not in catalog size. PR #300 (as re-scoped) bridges QA with a **staging-only**
bump to 2 vCPU / 4Gi (~free there thanks to scale-to-zero), while prod deliberately stays at
0.25 vCPU / 0.5Gi: an always-on 2/4Gi prod is ~+$40/mo run-rate for an operation that runs about
once per game update. The import must fit the small container permanently — the export only grows
with every game update, so "a bigger box" is a treadmill, not a fix.

Measured on the real file (incident harness, managed heap):

| Stage | Managed memory |
|---|---|
| after parse (`List<ParsedExportRow>`) | ~530 MB |
| after `MapToIncoming` (per-row `FragmentKey`/`TranslationSource` class VOs) | ~1.1 GB |
| peak at `ComputePlan` (792k `Translation.CreateUntranslated` in `plan.Added`) | ~1.2 GB committed / 932 MB retained |
| re-import adds `GetAllAsync` (792k *tracked* aggregates + snapshots) | conservatively +0.7–0.9 GB |

All stages are hoisted locals of the async `Handle` state machine — **four full materializations
of the file are reachable simultaneously**, against a ~384 MB GC cap (75 % of the 0.5Gi cgroup
limit). `DOTNET_GCHeapHardLimit=0x18000000` reproduces the OOM deterministically mid-parse.

## Goal

An admin imports the full `exported.txt` (today ~79 MB / ~792k rows, headroom to ~2M rows) —
first import **and** re-import — successfully on the default 0.25 vCPU / 0.5Gi container, with
import semantics byte-for-byte identical to spec 0001. The working set must scale with a chunk,
not with the file or the catalog.

## Ubiquitous language

- **Pass 1 (plan):** a full streaming read of the upload that validates every row and computes the
  diff plan against a compact catalog projection — writes nothing.
- **Pass 2 (apply):** a second streaming read of the same buffered upload that realizes the plan
  inside one transaction — COPY for added rows, chunked domain mutations for the rest.
- **Source hash:** a 128-bit hash of a row's source triple (`Text`, `ArgsOrder`, `ArgsId`) used to
  compare incoming vs stored sources without retaining either string in memory.
- **Compact projection:** per stored row, a value struct `(TranslationId, FragmentKey, sourceHash,
  Status, IsRemoved)` streamed untracked from the DB — never a tracked aggregate, never retained
  strings.
- **Chunked apply:** loading affected aggregates in ID batches (~2–5k), mutating via the existing
  domain methods, saving, then clearing the change tracker — bounded memory regardless of how many
  rows changed.

## In scope

- **Streaming parser API.** `ITranslationExportParser` gains a streaming shape
  (`IAsyncEnumerable<ParsedExportRow>`-style); strict-UTF-8 and reject-the-whole-upload semantics
  unchanged. The collected error list is **capped** (e.g. first 100) — today an all-garbage 79 MB
  file can balloon the error list itself; the upload is rejected either way.
- **Pass 1 — plan without materializing the file.** Per streamed row: VO validation
  (`FragmentKey.Create`, `TranslationSource.Create`), duplicate-key detection, source hash →
  a `key → hash` struct map. The catalog side streams the compact projection (new untracked
  repository read; hash computed row-by-row, strings discarded). `TranslationDiffService` moves to
  **value rows** (exactly ticket #290's shape): output is ID/key lists + counters — no aggregates,
  no source strings (`plan.Added` holds keys; source-changed holds `key → id`).
- **Mass-removal guard** evaluated after Pass 1, before any write — semantics unchanged.
- **Pass 2 — apply inside the one existing transaction** (`IUnitOfWork.ExecuteInTransactionAsync`,
  retrying execution strategy owns the boundary, as today):
  - *Added:* re-stream the upload, filter to added keys, `Translation.CreateUntranslated` per row,
    feed straight into the binary COPY in batches — `IBulkTranslationInserter` accepts a stream
    (`IAsyncEnumerable<Translation>`), never a full list.
  - *Source-changed:* while streaming, buffer `(TranslationId, new TranslationSource)` up to the
    chunk size → load those aggregates by ID → `ApplySourceChange` → save → clear tracker → next
    chunk.
  - *Removed / Restored:* chunked by ID (no file data needed) → `MarkRemoved` / `Restore`.
  - `GameVersion.MarkAsProcessed()` commits with the last save, same transaction (spec 0001).
- **Upload re-readability.** Pass 2 re-reads the ASP.NET-buffered form file (multipart files above
  the buffering threshold sit on disk). If re-opening proves unreliable on the container, copy the
  stream once to a temp file on ephemeral storage and stream both passes from it — decided by a
  test during implementation, either way O(1) memory.
- **Repository surface:** remove `ITranslationRepository.GetAllAsync` (#290 AC), add the streamed
  compact-projection read and a chunked `GetByIdsAsync`.
- **The #300 revert is this ticket's DoD:** delete the `tms_api_cpu`/`tms_api_memory` override from
  `iac/env/staging.tfvars` so staging folds back to the 0.25 / 0.5Gi defaults.

## Out of scope

- **Async import (blob + queue/background worker, `202 Accepted` + status).** Spec 0004 parked it
  as "Oś B", ADR-first; it stays parked — see *Alternatives considered*. Revisit trigger: measured
  pass durations approaching the ingress/request budget, uploads far beyond the 256 MB cap, or
  concurrent multi-admin imports becoming real.
- **SQL-side diff or mutations (staging table + `UPDATE … CASE`).** Resolved Q3 of spec 0004
  (2026-06-28) stands: the source-change state machine lives in C# (`ApplySourceChange`) only.
- **A persisted `SourceHash` column.** Streaming the projection and hashing in flight is enough at
  this scale; a denormalized hash column is a micro-optimization with a write-path maintenance
  cost — reconsider only if catalog egress ever dominates.
- **Changing the `||` contract, the upload transport, the diff rules, or other slices** (upsert /
  approve touch one row).
- **Parallelism** (parallel parse/hash or concurrent chunk saves) — the serial pipeline fits the
  budget; complexity not paid for.

## Business rules & edge cases

- **The five outcomes and their writes are byte-for-byte spec 0001 / spec 0004 Phase-2 rules** —
  including the conditional `ApplySourceChange` transition (capture `PreviousSourceText` + flip to
  `NeedsReview` only from `Draft`/`Approved`), `Restore` preserving status incl. `Approved`,
  soft-removal only, and **Unchanged = no write at all** (the idempotent re-upload test's frozen
  `UpdatedAt` must keep passing).
- **Equality via 128-bit source hash.** Hash the triple with length/null framing (null ≠ empty;
  field boundaries encoded) so `("ab","c")` never collides with `("a","bc")`. Default algorithm:
  SHA-256 truncated to 128 bits — zero new dependencies; `XxHash128` (System.IO.Hashing) only if
  profiling shows hashing matters at 0.25 vCPU. Collision odds at 2M rows ≈ 10⁻²⁶ — accepted and
  recorded here; the diff treats hash-equal as source-identical.
- **No stage may materialize the whole file or catalog** — including the worst cases: a baseline
  import (all rows added → streamed COPY) and a pathological all-rows-source-changed re-import
  (bounded by the chunk buffer). Peak expectation at 800k rows: ≤ ~200 MB managed (key/hash maps
  ~50–90 MB each + chunk buffers), verified by the harness AC below.
- **Guard order:** Pass 1 completes (all validation + full counts) before any write; guard
  failures return 422 with nothing persisted and the version still `Unprocessed` — unchanged.
- **Duplicate keys / empty upload / parse errors:** rejected exactly as today (error cap changes
  only how many line errors are *reported*, never whether the upload is rejected).
- **Retry-safety.** The execution strategy may re-run the whole apply unit on a transient commit
  fault: Pass 2 must be re-entrant (re-stream the buffered file, reload chunks). The spec-0004
  `acceptAllChangesOnSuccess: false` discipline extends to **every chunked save** inside the unit;
  tracker state never survives across retries (`ChangeTracker.Clear()` per chunk).
- **Long streamed reads:** the catalog projection query streams ~800k rows on a 0.25 vCPU
  container against Neon — set an explicit, generous `CommandTimeout` on that read; measure and
  record real pass durations in the implementing PR (they are the "Oś B" trigger data).
- **Bootstrap seeder is untouched** (`PolishTranslationSeeder` uses the materializing parse of the
  much smaller `polish.txt`); the materializing parser API stays available for it.

## Contract

- **Trigger / Input / Output / Errors:** unchanged. `POST /api/v1/game-versions/{id}/import`
  (multipart, admin-only, `allowMassRemoval` query), returns the same `ImportSummary`
  (Added / SourceChanged / Invalidated / Removed / Unchanged / Warnings), same ProblemDetails
  (404 / 413 / 422). No `TranslationSystem.Contracts` change; the Frontend is untouched.
- **Files touched:** none beyond today (uploaded temp buffer; artifact rebuild stays the PERF-04
  debounced background job).

## Acceptance criteria

- [ ] Every existing import integration test passes **without touching its assertions** (five
      outcomes, mass-removal guard, atomicity + transient-retry test, idempotent re-upload with
      frozen `UpdatedAt`, COPY column-state assertions).
- [ ] Domain unit tests change only for the new `TranslationDiffService` value-row signature;
      behavior table (added/changed/removed/restored/unchanged × status conditions) fully covered.
- [ ] New integration coverage: a chunk-boundary case (changed rows > 1 chunk) and a
      duplicate-key-in-file rejection through the streaming path.
- [ ] **Memory gate:** the incident harness replayed on the real 79 MB export under
      `DOTNET_GCHeapHardLimit=0x18000000` (384 MB) completes **first import and re-import**
      without OOM; peak managed memory recorded in the PR.
- [ ] Import wall-clock on the integration Postgres stays within spec 0004's < 10 s budget for the
      ~700k baseline; real staging pass durations measured and recorded in the PR.
- [ ] `ITranslationRepository.GetAllAsync` is gone; nothing else regressed (zero warnings).
- [ ] `iac/env/staging.tfvars` sizing override removed (**the #300 bridge revert**); staging
      QA-FE-09 import re-verified green on the small size after deploy.
- [ ] Ticket #290 body updated to this spec; ADR-0011 gets a short amendment note (streamed COPY
      source + two-pass read), spec 0004 a forward pointer to this spec.

## Open questions

**Business:** none — direction (streaming over async-job infra), the staging-only bridge, and
"design doc first" were decided by the owner on 2026-07-02 (this conversation supersedes older
ticket wording per the repo pivot rule).

**Empirical — resolve during implementation, in this order:**

- Is the buffered `IFormFile` stream reliably re-readable (seek/reopen) under Kestrel on the
  container? If not: explicit temp-file copy. `[verify with a test]`
- Chunk size (2k vs 5k) and the projection read's `CommandTimeout` — pick from measurements on the
  integration Postgres, sanity-check on staging. `[measure]`
- Does the synchronous request stay comfortably inside the ACA ingress/request budget at
  0.25 vCPU? Record the numbers; they gate any future "Oś B" ADR. `[measure on staging]`

## Assumptions

- ~800k rows today, ~2M as the design horizon; the 256 MB upload cap (spec 0003) stands.
- Imports are owner/admin-only and serial (spec 0001); no concurrent imports of one version.
- PostgreSQL-only persistence (COPY path already assumes it — ADR-0011).
- A synchronous minutes-scale import request remains acceptable UX for this admin-only, roughly
  monthly operation while it fits transport budgets.

## Alternatives considered (and why not now)

- **Blob storage + queue/background worker (`202 Accepted` + polling), or an ACA Job per import.**
  Genuinely attractive: immune to request timeouts, retryable, keeps an audit copy of every
  upload; the ACA-Job variant could even run the *current* naive pipeline on a big pay-per-second
  container (~pennies per import). Rejected *for now* because it does not fix the defect — the
  worker still needs the streaming rewrite (or permanently masks the O(file) memory with sizing),
  while adding the repo's first background-processing infrastructure (job store, status endpoint,
  polling UI, eventual-consistency UX) to a flow whose synchronous form fits every budget once
  memory is O(batch). Spec 0004 already classified this as "Oś B", an ADR-first change gated on
  evidence; the measured pass durations this spec requires are exactly that evidence, so the
  option stays open and cheap to adopt later — the streaming pipeline would be its worker's core
  either way (nothing built here is throwaway).
- **Set-based diff/mutations in SQL (staging table + joins + `CASE`).** Smallest memory of all and
  the fastest, but it duplicates `ApplySourceChange`'s conditional state machine outside the
  domain — two sources of truth for the one piece of real business logic in the flow; domain unit
  tests stop covering the real path. Explicitly resolved against, spec 0004 Q3 (recorded in the
  ADR); nothing in the incident changes that trade-off.
- **Permanent vertical scale (PR #300 as originally cut).** ~$480/yr for a monthly operation,
  unbounded as the export grows, and a 4Gi ceiling that hides every future memory regression in
  the API. Re-scoped on 2026-07-02 to the staging-only bridge this spec removes.

## Impact on existing docs & backlog (on agreement)

- **#290 (PERF-05):** body re-cut to this spec (streaming two-pass, not just the projection);
  the staging.tfvars revert lands in its DoD.
- **ADR-0011:** amendment note — COPY input becomes a stream; the import reads the upload twice.
- **Spec 0004:** forward pointer "Phase 2 realized by spec 0006".
- **PR #300:** already merged as the bridge; nothing further.
