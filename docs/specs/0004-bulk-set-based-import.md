# Spec 0004: Bulk / set-based exported.txt import

- **Status:** Implemented (2026-07-01)
- **Date:** 2026-06-28
- **Author:** ticket-worker
- **Ticket:** #214 (follow-up to #208)
- **Related:** spec 0001 (import lifecycle + the five diff outcomes), spec 0003 / #208 (lifted the
  upload cap — this is its performance follow-up), ADR-0001 (slim handlers), ADR-0007 (read
  projections are not aggregates), ADR-0011 (Npgsql COPY for the import added-rows write path)

## Implementation notes (2026-07-01)

Phase 1 shipped as specified: added rows are written by a native Npgsql binary `COPY` behind
`IBulkTranslationInserter` (`…Persistence/Bulk/`), enlisted in the import transaction; the
source-change / removed / restored outcomes stay per-row. A full-catalog baseline that took ~3 min
now completes in **~1 s** on the integration Postgres (measured at 200k rows).

One design detail beyond the original spec surfaced in review and is worth recording: the write
context has `EnableRetryOnFailure`, so the transaction is driven by the execution strategy
(`IUnitOfWork.ExecuteInTransactionAsync`). A transient fault **at commit** makes the strategy re-run
the whole unit — and with EF's default `SaveChanges(acceptAllChangesOnSuccess: true)` the retry would
find the tracker already accepted and silently drop the tracked diff mutations + the version's
`Processed` flag (COPY re-runs fine; the tracked half would be lost). The unit therefore saves with
`acceptAllChangesOnSuccess: false` and calls `ChangeTracker.AcceptAllChanges()` only **after** the
commit succeeds. This is regression-tested (`ExecuteInTransaction_WhenFirstCommitFailsTransiently…`):
without the deferral the version ends `Unprocessed`; with it the whole unit survives the retry. The
commit-ack-loss case (server committed, ack lost → retry re-COPYs → unique-key violation → 500) stays
a loud, non-corrupting failure the admin re-imports past idempotently.

## Business context

#208 lifted the upload cap so an admin can finally post the whole ~80 MB `exported.txt`. The very
first real upload exposed the next problem: the import itself runs for **~3 minutes** on a
full-catalog baseline. The cost is not transfer or parsing — it is the write path. `ImportExportedTexts.Handler`
does vanilla per-row EF Core: `TranslationRepository.GetAllAsync` materializes every stored
`Translation` as a **tracked** aggregate (with its `FragmentKey`/`TranslationSource` VOs),
`InsertRange` is `AddRange`, the diff outcomes mutate tracked entities one by one, and a single
`SaveChangesAsync` emits batched INSERT/UPDATE for **hundreds of thousands of rows** (~80 MB ≈
~700k fragments). A 3-minute synchronous request is slow and fragile (proxy/LB idle timeouts, no
progress, lost result on navigation).

## Goal

An admin's full-catalog import completes in **seconds, not minutes**, with the import's observable
semantics (spec 0001) byte-for-byte unchanged — so the synchronous request stays viable and an
async job (Oś B) is not needed.

## In scope — Phase 1 (inserts-only via COPY)

This iteration bulks **only the added-rows write**, which is the whole of the measured 3-minute
case (an empty-DB baseline is *all* added rows, every one `Untranslated` with no conditional logic)
— the smallest, lowest-risk change that kills the proven pain (resolved Q1).

- **Added rows** → PostgreSQL `COPY` (Npgsql `BeginBinaryImport` — a **native provider feature**,
  EF Core's Npgsql provider already sits on Npgsql; **no new dependency**, resolved Q2) instead of
  `AddRange` + `SaveChanges`, running on the DbContext's connection **inside the import transaction**
  so atomicity is preserved.
- The diff's **source-change / removed / restored** outcomes stay **per-row** this iteration (an
  incremental game-update import touches a fraction of rows, so they are not the measured pain).
- Keep every import semantic identical to spec 0001: the five outcomes
  (added / source-changed+invalidated / removed / restored / unchanged), the **mass-removal guard**
  (computed *before* applying), **one atomic transaction** (all-or-nothing, idempotent re-upload),
  the version flip to `Processed`, and the **artifact rebuild after commit**.
- The same `ImportSummary` shape (counts + warnings) is still returned.
- An acceptance test asserting a large baseline import finishes in **< 10 s** on the integration
  Postgres (resolved Q4).

## Out of scope

- **Phase 2 — set-based diff *updates* + lightweight diff *read* (deferred, agreed direction).**
  Bulking the source-change/removed/restored updates and replacing the tracked `GetAllAsync` with
  an `AsNoTracking` `(FragmentKey, Status, source)` projection. Phase 1 leaves a known gap: a
  *re-import* still loads all existing rows tracked for the diff (the baseline does not — its
  `GetAllAsync` returns empty), so re-imports stay on the old read cost. Build Phase 2 only if a
  real re-import proves slow. **When built, the source-change transition stays in C#** (lightweight
  projection → in-memory plan → bulk write), *not* SQL `CASE` — keeps `ApplySourceChange`'s
  conditional status/`PreviousSourceText` logic in one place (resolved Q3; recorded in the ADR).
- **Async job / `202 Accepted` + background worker + progress polling (Oś B).** A separate, later
  option gated on whether this perf work alone makes the synchronous request acceptable. It adds
  real infrastructure (job store, worker, polling UI) and is an **ADR-first** architecture change
  (the repo deliberately has no background processing yet). Not this spec.
- **Changing the upload transport** (#208 already did) or the `||` file format (ADR-gated).
- **Changing the diff *rules*** (spec 0001) — only *how* the resulting plan is written.
- **Bulk-optimizing the other write slices** (upsert, approve) — they touch one row; no need.

## Business rules & edge cases

The set-based write must reproduce `Translation`'s state machine exactly
(`Translation.cs:39-107`), per outcome:

- **Added** (incoming key absent): INSERT `Status=Untranslated`, `IntroducedInVersion=target`,
  source from the upload, no Polish, `CreatedAt=UpdatedAt=now`. Uniform → ideal for `COPY`.
- **Unchanged** (key present, source identical, not removed): **no write at all** — `UpdatedAt`
  must not advance (the idempotent re-upload test `ImportExportedTextsTests.cs:71-93` asserts the
  timestamp is frozen).
- **Restored** (key present, source identical, currently removed): set `RemovedInVersion=null`,
  `UpdatedAt=now`; **status (incl. Approved) stands**.
- **Removed** (active stored key absent from the upload): set `RemovedInVersion=target`,
  `UpdatedAt=now`; never hard-delete.
- **Source-changed** (key present, source differs): overwrite source, set
  `LastSourceChangeInVersion=target`, clear `RemovedInVersion`, `UpdatedAt=now` — **and the
  conditional part:** only when the current status is `Draft` or `Approved`, capture
  `PreviousSourceText = old source` and move to `NeedsReview`; from `Untranslated`/`NeedsReview`
  the status and `PreviousSourceText` are left as-is (`ApplySourceChange`, `Translation.cs:66-84`).
  This per-row conditional is the one piece of real logic the set-based path must preserve.
- **Mass-removal guard:** if `removedFraction > MaxRemovedFractionWithoutOverride` (default 0.20)
  and `allowMassRemoval` is false → reject **422, persist nothing, version stays `Unprocessed`**.
  Must be computed before any write (set-based COUNTs over the staged upload vs stored state).
- **Atomicity:** `COPY` and every set-based UPDATE run on the **same connection inside the EF
  transaction**, committed by the existing `IUnitOfWork`, so a guard failure or any error rolls the
  whole import back — exactly today's all-or-nothing behavior.
- **Empty / parse-failed / duplicate-key uploads:** unchanged — rejected before the write path,
  same `Import.*` errors (`ImportExportedTexts.cs:99-107,172-203`).

## Contract

- **Trigger:** `POST /api/v1/game-versions/{id}/import` — unchanged route, auth, limits (#208).
- **Input/Output:** `ImportExportedTexts.Command` / `ImportSummary` — unchanged.
- **Internal change (Phase 1):** the handler delegates the added-rows write to a new
  persistence-side port (e.g. `IBulkTranslationInserter` in `…Persistence`, the Npgsql `COPY` home),
  injected per ADR-0001 (handler stays a slim orchestrator; the COPY lives behind the port). The
  per-row source-change/removed/restored mutations + `SaveChangesAsync` stay as today.
  `TranslationDiffService` (pure) is unchanged — it still builds the `Translation` instances for the
  added set; the port reads their column values to `COPY` instead of `AddRange`.
- **Errors:** unchanged (`Import.*`, mass-removal 422, 404, 401/403).
- **Files touched:** none on disk; `translation."Translations"` written via `COPY` + set-based SQL.

## Acceptance criteria

- [x] A baseline import of a large export (~hundreds of thousands of rows) completes in **< 10 s**
      on the integration Postgres — the same payload that took ~3 min.
- [x] Every existing `ImportExportedTextsTests` integration test stays green with **assertions
      untouched** (idempotent no-op timestamp, the all-five-outcomes case, mass-removal 422 +
      state-intact, truncated/empty/duplicate 422s, auth) — proving semantics are byte-for-byte
      preserved.
- [x] The mass-removal guard still rejects (422, nothing persisted, version `Unprocessed`) when the
      removed fraction exceeds the threshold without the override.
- [x] The import is still one atomic transaction: an induced failure mid-write leaves zero changes
      and the version `Unprocessed`.
- [x] A re-import (idempotent) produces `Added=0`, all `Unchanged`, and advances no `UpdatedAt`.

## Open questions

**Empirical / answered from the code:**

- *Is the whole import already one transaction?* Yes — `ImportExportedTexts.Handler` does a single
  `SaveChangesAsync` (`ImportExportedTexts.cs:162`); the projector rebuild runs *after* commit
  (`:167`). The set-based path keeps this: `COPY` + set-based UPDATEs enlist on the same
  `NpgsqlConnection` the EF transaction owns, so `IUnitOfWork.SaveChangesAsync` still commits the
  unit. **[verify the connection-enlistment detail during implementation]**
- *Schema for `COPY`?* `translation."Translations"` — `Id` (uuid, `ValueGeneratedNever`),
  `FileId`/`GossipId` (owned), `SourceText`/`ArgsOrder`/`ArgsId` (complex), `Status` (text),
  `TranslatedText`, `PreviousSourceText`, `SubmittedById`/`ApprovedById` (uuid FK, null on add),
  `IntroducedInVersion`/`LastSourceChangeInVersion`/`RemovedInVersion`, `CreatedAt`/`UpdatedAt`
  (`TranslationConfiguration.cs`). All known — no open question.

**Business / architecture decisions — RESOLVED (2026-06-28):**

- **Q1 — Scope depth → inserts-only first.** Bulk the added-rows write (`COPY`); leave the
  incremental diff updates per-row. Covers the measured baseline; Phase 2 (Out of scope) optimizes
  the rest only if a re-import proves slow.
- **Q2 — Bulk mechanism → raw Npgsql `COPY`.** It is a native Npgsql provider feature
  (`BeginBinaryImport`), not a third-party bulk library, so there is **no new dependency** — exactly
  the user's bar ("if the PG provider itself has nothing extra, use raw SQL").
- **Q3 — Source-change logic placement → C#** (applies to the deferred Phase 2). Lightweight
  `(key, status, source)` projection → in-memory transition → bulk write; **never** SQL `CASE`.
  Recorded in the new ADR.
- **Q4 — Performance budget → < 10 s** on the integration Postgres for a ~700k-row baseline.

## Assumptions

- The catalog stays a single `Translations` table written whole per import (spec 0001); no
  partitioning/sharding.
- ~700k rows is the right order of magnitude for the full export; the budget is set with headroom.
- PostgreSQL remains the only provider (the prod parity stack and ACA both use Postgres), so a
  Postgres-specific `COPY` path carries no portability cost.
