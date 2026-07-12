# ADR-0011: Npgsql COPY for the import added-rows write path

**Status:** Accepted (amended 2026-07-02 — spec 0006/#290 supersedes the buffered pipeline with the streaming two-pass import; the COPY port now takes a stream — see the amendment note at the end)
**Date:** 2026-06-28
**Decision-makers:** Solo maintainer
**Related:** spec 0004 (bulk/set-based import — this decision's spec), spec 0001 (import lifecycle + the five diff outcomes), #208 / spec 0003 (lifted the upload cap — this is its performance follow-up), ADR-0001 (slim SRP handlers), ADR-0007 (a non-repository persistence `Store` port precedent)

## Context

#208 lifted the upload cap so an admin can finally POST the whole ~80 MB `exported.txt`. The first
real upload exposed the next problem: a full-catalog **baseline import runs for ~3 minutes**. The
cost is the write path, not transfer or parsing. `ImportExportedTexts.Handler` does vanilla per-row
EF Core — `TranslationRepository.GetAllAsync` materializes every stored `Translation` as a
**tracked** aggregate (with its `FragmentKey`/`TranslationSource` VOs), `InsertRange` is `AddRange`,
the diff outcomes mutate tracked entities one by one, and a single `SaveChangesAsync` emits batched
INSERT/UPDATE for **hundreds of thousands of rows** (~80 MB ≈ ~700k fragments). EF's change tracker
+ per-row SQL is one to two orders of magnitude slower than a bulk load for this row count.

One structural fact makes the fix easy to scope: a baseline import (empty DB) is **entirely added
rows**, every one `Status=Untranslated` with no conditional logic
(`TranslationDiffService.cs:36-40`, `Translation.CreateUntranslated`). The conditional part of the
domain — `ApplySourceChange`'s status-dependent `PreviousSourceText`/`NeedsReview` transition
(`Translation.cs:66-84`) — only applies to *incremental* imports, which touch a fraction of rows.
So the measured 3-minute pain is exactly the part with **no** business logic to preserve.

The tension this ADR resolves: the repo's write model is aggregates + `IUnitOfWork` + repositories,
with logic in the domain (house rules: "no primitive obsession", "handlers are orchestrators —
business logic lives in domain/application services", "errors are values"). A bulk `COPY` writes raw
rows straight to the table, **bypassing the change tracker** — a deliberate deviation from "every
write goes through the aggregate". We want the speed without smearing domain logic into SQL or
adding a dependency.

## Decision

Write the import's **added rows** with PostgreSQL `COPY`, behind a persistence port, inside the
existing import transaction; leave everything else as it is (Phase 1 of spec 0004).

- **`COPY` via native Npgsql.** Use `NpgsqlConnection.BeginBinaryImport(...)` — a **native feature
  of the Npgsql provider** EF Core already sits on, **not** a third-party bulk library — so there is
  **no new dependency**. It runs on the DbContext's own `DbConnection` enlisted in the import
  transaction, so atomicity is unchanged: a guard failure or any error still rolls the whole import
  back (all-or-nothing, spec 0001).
- **Behind a port, handler stays slim (ADR-0001).** A new persistence-side port
  (e.g. `IBulkTranslationInserter`, implemented in `…Persistence` over the write `DbContext`) owns
  the `COPY`; the handler orchestrates and calls it, exactly as it calls repositories today. This
  mirrors ADR-0007's precedent that not every persistence concern is an `IRepository` —
  a dedicated `Store`/port is the idiom for the non-aggregate-load cases.
- **`TranslationDiffService` is untouched.** It still builds the `Translation` instances for the
  added set (pure, in-memory); the port reads their column values to `COPY` instead of `AddRange`.
  The domain entity remains the single definition of an added row's initial state.
- **The diff's source-change / removed / restored outcomes stay per-row this iteration.** They are
  not the measured pain (a game-update import touches a fraction of rows). Bulking them is Phase 2.
- **Phase 2 direction is fixed now (so it isn't relitigated):** when the diff *updates* are bulked,
  the conditional `ApplySourceChange` transition stays **in C#** — a lightweight `AsNoTracking`
  `(FragmentKey, Status, source)` projection → in-memory plan → bulk write — **never** a SQL
  `CASE WHEN status IN ('Draft','Approved')`. SQL would duplicate the transition and risk silent
  divergence from the entity.
- **Semantics are test-guarded, not re-specified.** The existing `ImportExportedTextsTests`
  integration suite (idempotent no-op timestamp, all-five-outcomes, mass-removal 422 + state-intact,
  truncated/empty/duplicate 422s, auth) must stay green **with assertions untouched**. Because those
  tests assert observable column state, any drift between the `COPY`'d columns and what the entity
  would have written is caught.

## Alternatives considered

- **Keep the per-row EF write path (status quo).** Rejected: ~3 minutes on the baseline and a
  fragile multi-minute synchronous request.
- **A third-party bulk library (EFCore.BulkExtensions, linq2db).** Rejected: adds a dependency for
  something Npgsql does natively; the repo values minimal dependencies; `COPY` is the fastest path
  and the provider already exposes it.
- **Staging table + a single SQL `MERGE`/`CASE` for the *whole* diff.** Rejected for the transition
  logic: it duplicates `ApplySourceChange`'s conditional status/`PreviousSourceText` rules in SQL,
  the exact silent-divergence risk the house rules guard against. (Set-based `ExecuteUpdate` for the
  *unconditional* removed/restored outcomes is fine and is on the table for Phase 2.)
- **Full set-based diff now (insert + update + remove + restore in one pass).** Rejected: YAGNI. The
  measured pain is the all-added baseline; incremental imports touch a fraction of rows. Optimize
  them only when a real re-import proves slow (spec 0004, Phase 2).
- **Async job — `202 Accepted` + background worker + progress polling (Oś B).** Deferred to a
  separate ADR: it introduces background processing the repo deliberately lacks, and after the
  `COPY` speedup the synchronous request may well be fine. Perf first, async only if needed.

## Consequences

**Positive**
- The baseline import goes from ~3 minutes to seconds; the synchronous request stays viable and the
  heavier async-job architecture (Oś B) is not forced.
- **No new dependency** — `COPY` is native Npgsql.
- The handler stays a slim orchestrator (ADR-0001); the `COPY` lives behind a port, consistent with
  ADR-0007's "not everything is a repository" seam.
- Domain logic stays put: the added-row shape is still defined by `Translation.CreateUntranslated`,
  and the Phase 2 transition is pre-committed to C#. No `CASE`-in-SQL business logic.
- Atomicity and all spec-0001 semantics are preserved and pinned by the unchanged integration tests.

**Negative / trade-offs**
- A second write idiom now exists in the import path: a `COPY` that **bypasses the EF change
  tracker**. Bounded to the added-rows write, and intentional — but it means the `COPY` column list
  is a second place that must track the EF mapping. A schema/mapping change to `Translations` must
  update the `COPY` column list too; the integration suite is the guard that catches a mismatch.
- Added rows are inserted outside the change tracker, so the unit cannot rely on EF tracking those
  entities afterwards. The handler doesn't — it computes the `ImportSummary` from the plan and
  returns — so this is a non-issue today, noted for a future maintainer.
- Phase 1 leaves a known gap: a **re-import** still loads existing rows tracked for the diff (the
  baseline does not — its `GetAllAsync` returns empty). Accepted deliberately; closing it is the
  Phase 2 trigger.

**Extraction trigger (futureproofing without speculation).** Build Phase 2 (set-based diff updates
+ the lightweight `AsNoTracking` diff read) **when** a real re-import proves slow — keeping the
transition logic in C# as decided here. Introduce the async job (Oś B) via a separate ADR **if** the
synchronous request proves insufficient even after this speedup. Until each trigger fires:
COPY-the-baseline now, optimize-the-rest later.

**Amendment (2026-07-02, spec 0006 / #290):** the extraction trigger fired early, and on *memory*
rather than re-import latency — the first real 79 MB / 792k-row imports OOM-killed tms-api at the
default 0.25 vCPU / 0.5Gi (prod with an **empty** catalog; incident bridged by the staging-only
sizing bump of PR #300). Spec 0006 supersedes the buffered pipeline with a streaming two-pass
import: Pass 1 streams the upload and a compact hashed catalog projection into a value-row plan
(the tracked `GetAllAsync` read is removed), Pass 2 re-reads the buffered upload and applies the
plan in chunks inside the same transaction — and this ADR's `COPY` port takes a **stream**
(`IAsyncEnumerable<Translation>`) instead of a materialized list. Everything else here stands,
including the pre-commitment that the source-change transition stays in C#.
