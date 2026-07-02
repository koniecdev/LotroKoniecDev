# ADR-0021: Debounced background artifact rebuild — writes schedule, a worker projects

**Status:** Accepted
**Date:** 2026-07-02
**Decision-makers:** Solo maintainer
**Related:** TranslationSystem.API (TranslationFiles/Translations/Import slices), Persistence store, ticket #289 (PERF-04), performance audit 2026-07-02 finding P1-2, ADR-0007 (amended by this ADR), spec 0001

## Context

Spec 0001 distributes the Polish translation file as a precomputed row (`TranslationArtifacts`):
`GET /translation-files/{lang}` streams stored content + hash-ETag and never builds per-request
(ADR-0007). The write side kept that row fresh **inline**: `ApproveTranslation.Handler` (always),
`UpsertTranslation.Handler` (when editing an Approved row) and `ImportExportedTexts.Handler`
(always) awaited `IPrecomputedTranslationFileProjector.RebuildAsync(...)` right after
`SaveChangesAsync`, passing the **request** `CancellationToken`.

Code facts that make that a problem (audit finding P1-2):

- A rebuild is O(N) in the catalog: scan the whole Approved set, serialize a multi-MB `||`
  string, SHA-256 it, and UPDATE a multi-MB row — on every single approve click.
- `PrecomputedTranslationFileProjector` serializes rebuilds behind a process-wide
  `SemaphoreSlim(1,1)`. A reviewer burst of k approves paid k serialized rebuilds — the k-th
  response waited k × rebuild — plus k rounds of MVCC/TOAST/WAL churn on the same row.
- The awaited rebuild ran on the request token: a client disconnect after the domain commit but
  before the rebuild finished cancelled the projection and left the artifact **silently stale**
  until some later write. A rebuild failure after a successful commit also surfaced as a 5xx for
  a write that had, in fact, committed.
- `PrecomputedTranslationFileStore.GetByLanguageAsync` materialized the entire existing row —
  including the previous multi-MB `Content` — only for `Refresh(...)` to overwrite every field.
- ADR-0007 recorded the old contract explicitly: "refreshed in a separate transaction …
  **awaited before the response (so no client-visible staleness)**". This ADR amends that
  consequence.

The artifact is a derived, regenerable projection — never a source of truth — and its consumers
are the CLI/player download (ETag revalidation loop), the Frontend Import/Export page's export
download (M3-07, `ImportExportLoader` — a user-clicked re-serve of the same endpoint) and the
future WPF auto-download. None reads it synchronously after a write: the editor UI reads the read
models, and an export click follows the triggering write by far more than the debounce window.

## Decision

### 1. Write handlers signal; they never await the rebuild

Approve/upsert-of-approved/import call `ITranslationFileRebuildScheduler.Schedule(language)`
**after** `SaveChangesAsync` instead of awaiting `RebuildAsync`. `Schedule` is synchronous and
non-blocking (an unbounded-channel `TryWrite`), so the response returns at commit cost and — with
no await between commit and signal — a client disconnect can no longer strand a committed write
without its rebuild.

### 2. A debounced BackgroundService performs the rebuilds on the host lifetime

`TranslationFileRebuildWorker : BackgroundService` waits for the first dirty signal, sleeps one
`TranslationFileRebuildSettings.DebounceWindow` (default 2 s; config
`TranslationFileRebuild:DebounceWindow`, set short in test environments), drains everything queued
meanwhile, and runs **one** rebuild per distinct language through the existing projector — with
the worker's stopping token, i.e. the application lifetime, never a request token. A reviewer
burst therefore collapses from k × O(N) to ~O(N). A failed rebuild is logged and re-scheduled, so
the artifact converges even across transient DB faults, paced by the debounce window.

### 3. Fixed coalescing window, not a sliding debounce

The window starts at the first signal and is never extended by later ones: a sustained write
stream cannot starve the rebuild, and artifact staleness is bounded by
`DebounceWindow + one rebuild`. Signals arriving during a rebuild simply start the next cycle —
rebuilds read a full snapshot, so a redundant cycle is idempotent (same content ⇒ same ETag).

### 4. The store refresh is a set-based UPDATE

`IPrecomputedTranslationFileStore.GetByLanguageAsync` + entity `Refresh(...)` are replaced by
`TryRefreshAsync(language, content, contentHash, generatedAt)` — a single `ExecuteUpdateAsync`
returning whether a row was hit; the projector inserts (change tracker + save) only when it
returns false, i.e. the first build per language. The previous multi-MB content is never loaded
again, and `PrecomputedTranslationFile` becomes immutable (create-only; EF maps its now get-only
properties explicitly — no model/schema change).

### 5. The single-replica assumption is explicit, and stays

The dirty-signal channel is in-process and the projector's single-flight gate is per-process —
both assume **one API replica**, which the infrastructure already pins
(`iac/azure-container-apps.tf`: `max_replicas = 1`; `iac/vars.tf` validation says as much). The
brief two-revision overlap during a rolling deploy stays safe: each process rebuilds from a full
DB snapshot on its own writes. What is *not* safe is steady-state N > 1 — two replicas' rebuilds
can finish out of order and the last committer may have started from the older snapshot, parking
the artifact stale until the next write. Raising `max_replicas` above 1 therefore requires
re-opening this decision (durable/db-backed scheduling — see Alternatives B) in a new ADR.
Staging additionally runs `min_replicas = 0` (ADR-0018): its scale-to-zero shutdown is exactly the
dropped-pending-signals trade-off recorded below, accepted there for the same regenerable-artifact
reasons.

### 6. Bootstrap keeps its synchronous rebuild

`TranslationsBootstrapExtensions` still awaits the projector inline after the polish seed: it runs
before the app serves traffic, where "respond fast" is meaningless and "artifact ready at first
request" is the point.

### 7. The consistency contract is now convergence, not read-your-write

The distribution endpoint may briefly serve the previous artifact after a commit. Integration
tests assert convergence by polling the download with a timeout instead of downloading once right
after the write; the suite quiesces on the scheduler's pending-signal count before truncating
tables between test classes.

## Consequences

### Positive

- Approve/upsert/import respond at commit cost; the O(N) scan + multi-MB serialize/hash/UPDATE
  leave the request path entirely.
- Reviewer bursts coalesce: k clicks ⇒ one rebuild (was k serialized rebuilds behind the gate),
  cutting MVCC/TOAST/WAL churn on the artifact row by the same factor.
- A disconnect or post-commit failure can no longer leave the artifact silently stale — the
  worker owns the rebuild on the host lifetime and retries failures until it converges.
- A rebuild failure no longer turns a committed write into a 5xx response.
- Each refresh is one UPDATE; the old read-modify-write (fetch multi-MB TOASTed content, then
  overwrite it) is gone.

### Negative / Accepted Trade-offs

- Eventual consistency for the artifact: a download immediately after an approve can be one
  debounce window behind. Acceptable — the CLI's ETag loop re-fetches on its next check, and the
  editor UI never reads the artifact.
- Signals pending at process shutdown are dropped; the artifact stays stale until the next write
  (or bootstrap). Accepted for a regenerable projection on a single always-on replica.
- The in-process queue hard-codes the single-replica topology (previously implicit in the gate);
  scaling out now has a named blocker with a defined re-opening path (§5).
- Tests trade "download right after write" for poll-with-timeout, and the integration suite needs
  a quiesce point between classes — more moving parts in test infrastructure.

## Alternatives Considered

### A. Fire-and-forget `Task.Run(RebuildAsync)` per write

Removes the wait but keeps k rebuilds for k clicks, loses failure observability (unobserved
exceptions), and still needs a lifetime token story. Rejected.

### B. Transactional outbox / DB-backed job queue

Durable across restarts and multi-replica-safe — the correct shape once `max_replicas > 1` or an
event-driven read store appears. Rejected **for now**: infrastructure weight (table, poller,
dedupe) against a regenerable artifact on an infra-pinned single replica; named as the explicit
re-opening path in §5.

### C. Rebuild lazily on `GET` when stale

Reintroduces the per-request build spec 0001 forbids, and moves the O(N) cost onto the CLI
download path's tail latency. Rejected.

### D. Sliding debounce (each signal restarts the window)

Marginally better coalescing under sustained bursts, but a steady write stream can defer the
rebuild indefinitely — unbounded staleness. Rejected for the fixed window (§3).

### E. Quartz/Hangfire scheduler dependency

A framework for one loop. The repo precedent (`OpenIddictPruneService`, PERF-02) is a plain
`BackgroundService`; mirrored here. Rejected.

## Implementation Notes

- New: `TranslationSystem.API/Features/TranslationFiles/{ITranslationFileRebuildScheduler,
  TranslationFileRebuildScheduler,TranslationFileRebuildWorker,TranslationFileRebuildSettings}.cs`
- Changed: `ApproveTranslation`, `UpsertTranslation`, `ImportExportedTexts` (inject the scheduler,
  signal after save), `PrecomputedTranslationFileProjector` (TryRefresh-else-Insert; doc),
  `IPrecomputedTranslationFileStore` + `PrecomputedTranslationFileStore` (`TryRefreshAsync` via
  `ExecuteUpdateAsync`), `PrecomputedTranslationFile` (immutable, `Refresh` deleted),
  `PrecomputedTranslationFileConfiguration` (explicit get-only property mappings; no migration),
  `ApiDependencyInjection` (options + scheduler + hosted worker), `EventIds` (1400/1401)
- Tests: worker/scheduler unit tests; handler unit tests swap the projector fake for a scheduler
  fake and pin the commit-then-signal ordering; integration tests poll the download for
  convergence and reset shared DB state through the factory's fused quiesce-then-truncate
  `ResetDatabaseAsync` (drains `TranslationFileRebuildScheduler.PendingCount` first); E2E fixtures
  set a short debounce via env
- ADR-0007's "awaited before the response" consequence is amended to point here

## References

- Ticket #289 (PERF-04) — performance audit 2026-07-02, finding P1-2
- ADR-0007 — precomputed artifact modelling (projection, store, projector) — amended by this ADR
- Spec 0001 — translation-file distribution (download never builds per-request; unchanged)
- PERF-01 (#286/#294) — the read-side half: hash-only 304 revalidation
- `OpenIddictPruneService` (PERF-02, #297) — the BackgroundService house pattern mirrored here
- `iac/azure-container-apps.tf` / `iac/vars.tf` — `max_replicas = 1` (the single-replica pin)
