# ADR-0007: Read projections are not aggregates — the PrecomputedTranslationFile projection

**Status:** Accepted — amended 2026-07-02 by ADR-0021 (PERF-04): the projection refresh is no
longer awaited before the response; writes schedule a debounced background rebuild, the store
refresh is a set-based `ExecuteUpdate`, and `PrecomputedTranslationFile` is immutable
(`Refresh(...)` deleted).
**Date:** 2026-06-14
**Decision-makers:** Solo maintainer
**Related:** spec 0001 (translation-file distribution), ADR-0002 (TMS pivot + CQRS read/write split), branch `refactor/precomputed-translation-file-projection`, ADR-0021 (amendment)

## Context

Spec 0001 distributes the Polish translation file over HTTP: `GET /translation-files/{lang}`
streams a pre-serialized `||` file plus its content hash as the `ETag`, and the CLI/player caches
it via `If-None-Match`. The file is **not** rebuilt per request — it is precomputed and stored,
and regenerated on every write that changes the distributed (Approved, non-removed) set:
import/version-processing, approve, upsert of an Approved row, and the bootstrap seed.

This precomputed row was first modelled as an **aggregate**:
`TranslationArtifact : AggregateRoot<TranslationArtifactId>` in `TranslationSystem.Domain`,
persisted through `ITranslationArtifactRepository : IRepository<…>`. That is a category error:

- It guards **no business invariant** — the only guards are null/empty argument checks
  (programmer errors, not domain rules), and it is never loaded to *make* a decision: it is only
  ever blind-upserted (load-or-create → overwrite → save).
- It is **derived and regenerable** — explicitly "never a source of truth". Its content is a
  serialization of the `Translation` aggregate set; it can be rebuilt from scratch at any time.
- Its refresh is a **fire-after-commit projection**: every call site does
  `SaveChangesAsync()` (commit the write) **then** `RebuildAsync(...)`, and the rebuilder runs in
  its own scope/transaction. It is not part of any aggregate's consistency boundary — it is the
  output of a projection step, the kind of thing an event-driven system would build in a handler
  reacting to a domain event (this repo deliberately has no domain events — ADR-0002 non-lift —
  so the projection is invoked imperatively).

Two constraints shaped the fix. (1) `IRepository<TAggregateRoot, TId>` is constrained to
`where TAggregateRoot : AggregateRoot<TId>` — repositories are for aggregate roots only, which is
correct and worth preserving. (2) The CQRS read side was *already* correct: the distribution
endpoint reads a `…ReadModel` through `IApplicationReadDbContext`, never the write model. So the
only thing wrong was the **write-side modelling**: a cache dressed as an aggregate, sitting in the
domain core.

## Decision

Model the precomputed file as a **first-class but concrete materialized read projection**,
distinct from both aggregates and the virtual (dual-mapped) read models:

- **`PrecomputedTranslationFile : Entity<PrecomputedTranslationFileId>`** — a plain entity, **not**
  an `AggregateRoot`. It carries identity + mutable state (`Refresh(...)`) for EF tracking, and
  nothing more.
- It lives in a **new `LotroKoniecDev.TranslationSystem.Projections` project**, *out of*
  `Domain`. Because `Domain` does not (and must not) reference `Projections`, the domain core is
  **compile-prevented** from depending on a derived cache — the isolation a separate assembly buys,
  without polluting the domain or the (read-only) `ReadModels` project.
- Persistence goes through a dedicated port, **`IPrecomputedTranslationFileStore`** (`GetByLanguageAsync` +
  `Insert`), **not** `IRepository` — repositories stay aggregate-only. The implementation lives in
  `Persistence`, over the write `DbContext`.
- An explicit projector, **`IPrecomputedTranslationFileProjector` / `PrecomputedTranslationFileProjector`**,
  rebuilds the file from the current Approved set after the triggering write commits (single-flight
  via a process-wide gate, own scope).
- The **read side is unchanged**: `PrecomputedTranslationFileReadModel : IReadOnlyEntity<…>` served
  through `IApplicationReadDbContext`. Write type and read type dual-map to the same physical table
  (the established repo idiom).
- **No generic `MaterializedView<T>` framework.** There is exactly one materialized projection
  today; a generic building block + open-generic store would be premature abstraction.
- The physical table name (`TranslationArtifacts`) is **retained** — this is a code-model rename
  with **no schema change** (the accompanying migration is an empty snapshot re-sync).

## Alternatives considered

- **Keep it as an `AggregateRoot` (status quo).** Rejected: overstates a cache as a domain
  aggregate, dilutes the meaning of "aggregate", and routes a derived view through the aggregate
  repository.
- **Demote to `Entity` but keep it in `Domain`.** Rejected: a derived read cache is not a domain
  concept; `Domain` stays aggregates + value objects only.
- **Put the write type in `ReadModels`.** Rejected: the *write* context would own and mutate a type
  in a project named "ReadModels" — contradictory and misleading.
- **Introduce a generic `MaterializedView<TId>` + `IMaterializedViewStore<,>` building block
  (symmetric to `AggregateRoot`/`IRepository`).** Rejected as premature abstraction: one instance
  exists (Rule of Three), the imminent M3 dashboard counters are a cheap `COUNT … GROUP BY` query —
  not a materialization — and at real scale a materialized-view subsystem is event-driven projectors
  writing to a dedicated read store (e.g. a search index), not an in-process EF generic. The pattern
  is documented here; the building block is deferred until a second real case appears.
- **A Postgres `MATERIALIZED VIEW` (database object).** Not applicable: the content is the
  app-serialized `||` file + a SHA-256 hash produced in C# (`ITranslationFileSerializer`), which is
  not expressible as a SQL `SELECT`. It must be app-maintained.
- **Pure read model + set-based SQL upsert, no write type.** Rejected: the write context must still
  "know" the table to keep it in migrations (otherwise EF would drop it), and a raw upsert is less
  idiomatic than a thin entity + store. The thin entity is the smaller, more consistent deviation.

## Consequences

**Positive**
- `Domain` stays a pure write model (aggregates + value objects); the smell is gone.
- Repositories remain aggregate-only (Evans-correct); projections get their own seam.
- Honest, greppable naming (`PrecomputedTranslationFile`, `…Projector`, `…Store`) that does not
  collide with the Postgres `MATERIALIZED VIEW` term.
- The domain core is compile-isolated from the projection (no `Domain → Projections` reference).
- No schema churn — pure code-model rename, empty migration.

**Negative / trade-offs**
- A second persistence idiom now exists (a `Store` alongside `Repository`). Bounded and intentional;
  the distinction *is* the point.
- The projection is refreshed in a **separate transaction** after the triggering write, awaited
  before the response (so no client-visible staleness), but not atomic with it: a crash between the
  two commits leaves the file stale until the next write. Acceptable for a regenerable artifact and
  consistent with spec 0001.

**Extraction trigger (futureproofing without speculation).** Promote `Projections` to a generic
projection abstraction (and/or a dedicated read store + event-driven projectors) **when** a second
materialized projection appears, or when reads outgrow in-process refresh — via a new ADR. Until
then, folder/project-first, framework-later.
