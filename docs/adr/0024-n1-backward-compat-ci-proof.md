# ADR-0024: N-1 backward-compatibility proof — the previous release's integration suite against the HEAD schema

**Status:** Accepted
**Date:** 2026-07-05
**Decision-makers:** Solo maintainer
**Related:** ADR-0023 (the N-1 contract this job proves; its "asserted by rule + lint, not proven
by a test" trade-off closes here), ADR-0012 (CD — every `main` commit deploys; §5 shared-schema
window), MIGR-03 / #338 (the text gate this job backs with an executable proof), epic #335,
ticket #340 (MIGR-05), `.github/workflows/pr-verify.yml` (required-check + path-filter lesson),
`tests/LotroKoniecDev.TranslationSystem.API.Tests.Integration`,
`tests/LotroKoniecDev.AuthSystem.API.Tests.Integration`.

## Context

ADR-0023 makes every migration N-1 backward-compatible: the currently-deployed app revision must
keep working against the schema as it stands after the migration. Enforcement today is a rule
plus a text gate (MIGR-03) — nothing *executes* old code against a new schema. The integration
suites can't do it as they stand: each factory boots its own throwaway PostgreSQL and migrates it
itself to the schema of its own commit —
`TranslationSystemApiFactory.InitializeAsync` calls `Database.MigrateAsync()`
(`TranslationSystemApiFactory.cs:184`), and the Auth factory reaches the same call through
`DatabaseSeederExtensions.SeedAuthDatabaseAsync` (`DatabaseSeederExtensions.cs:26`). New code ×
new schema, always; the N-1 window is never exercised (ADR-0023 Context, last bullet).

Facts that constrain the design:

- **There are no `v*` tags.** `git tag` is empty. Releases are `main` commits: `deploy.yml`
  deploys every push to `main` (ADR-0012), so "the previous release" can only be defined in git
  terms, not tag terms.
- **`MigrateAsync` is idempotent via `__EFMigrationsHistory`.** If a database already carries a
  *newer* schema (more history rows than the running assembly knows), the old assembly's
  `MigrateAsync()` finds all of its own migrations recorded and applies nothing. Old test
  fixtures therefore tolerate a pre-migrated HEAD database without modification — the only
  missing piece is getting that HEAD schema *into* the container the old fixture creates.
- **Schema can only change through `src/**/Migrations/`.** The migrator (dev and prod) is the
  sole schema writer (ADR-0008 §6); app code never issues DDL. A PR that adds no migration
  cannot change the schema, so old-code-on-new-schema is trivially green for it.
- **A required check with a path filter deadlocks.** The repo already learned this with
  docs-only PRs (`pr-verify.yml` header): a required status that never reports blocks the merge.
  A path-filtered N-1 job must therefore stay a non-required check.
- **The integration suites already reset state with `TRUNCATE … CASCADE`** (e.g.
  `CoreLoopTests.cs:41`, `CleanerService.cs`), so rows in HEAD-added child tables referencing
  old parents are cascaded away rather than failing the old suite's resets — additive FK
  migrations stay green without touching old tests.
- **Idempotent scripts are generatable offline.** `dotnet ef migrations script --idempotent`
  needs only a build plus the design-time factory's `-- --connection` argument (the same
  mechanism `Dockerfile.migrator` and `scripts/apply-migrations.sh` rely on); it never connects.
  Testcontainers' `PostgreSqlContainer.ExecScriptAsync` (Testcontainers.PostgreSql 4.12.0) can
  apply such a script inside the container via psql.

## Decision

### 1. "Previous release" := `HEAD^1` of the checked-out commit

On a PR run, actions/checkout checks out the merge commit, whose first parent is the tip of
`main` the PR would merge into — under continuous deployment that *is* the running release (every
older revision was already proven by its own PR's run, transitively). On a `workflow_dispatch`
run from `main`, `HEAD^1` is the previous squash commit — the release before the current one.
One expression, correct in both contexts, no tag infrastructure invented for the purpose. If the
repo ever adopts release tags, this definition is the single line to revisit.

### 2. The vehicle is the old checkout's own integration suites, discovered by the old checkout's own convention

The job checks out `HEAD^1` into a git worktree and runs **every** `*.Tests.Integration.csproj`
that `find tests -name '*.Tests.Integration.csproj'` discovers **in that worktree** — the exact
discovery loop `pr-verify.yml`/`ci.yml` gate on. Whatever the previous release proved about
itself is what this job re-proves against the new schema: no curated subset, no silent narrowing;
suites added or removed on `main` change the proof automatically. The `*.E2E.Tests` projects stay
excluded exactly as they are excluded from the PR gate (they build Docker images; disproportionate
here for the same reason). Unit suites touch no database and prove nothing about schema — skipped.

### 3. The seam: `N1_COMPAT_SCHEMA_SCRIPTS_DIR`, applied before the fixture's own migrate

Both API factories gain one opt-in branch, immediately after their PostgreSQL container starts:
when the environment variable `N1_COMPAT_SCHEMA_SCRIPTS_DIR` is set, the factory applies
`<dir>/translation.sql` (TMS) / `<dir>/auth.sql` (Auth) — idempotent EF scripts generated from
HEAD — to the fresh container via `ExecScriptAsync`, then proceeds exactly as before. Its own
`MigrateAsync()` becomes a no-op (all its migrations are already in the applied history) and
stays in place as a safety net: it would loudly fail if HEAD ever *removed* a shipped migration.
When the variable is unset — every normal test run — behavior is byte-identical to today.

The seam refuses to false-green. A configured-but-missing script file throws; the script content
is prefixed with `\set ON_ERROR_STOP on` and a non-zero psql exit throws with stderr; the seam
parses the migration ids the script inserts into `__EFMigrationsHistory`, throws if it finds none
(a generation bug would otherwise silently hand the old fixture an empty database to migrate
old-style), and after applying verifies every parsed id is actually present in the history table.
Each failure mode is a thrown exception — a seam misconfiguration reads as a red job, never as a
quietly-vacuous green one.

Bootstrap and regression are told apart by *which side* lacks the seam: the job fails (exit 2)
if the **HEAD** checkout is missing either the marker in the seam files or the
`ApplyIfConfiguredAsync` call in the factories (the seam is a contract; removing the file *or*
orphaning it as dead code silences the proof — both must be red), and reports-and-passes if the
marker is missing from the **previous release** (genuine pre-seam history; a one-time window
that closes at the first post-seam merge, loudly noted in the run summary).

### 4. Scope the trigger to what can break the invariant: migration-touching PRs, plus manual dispatch

A separate workflow (`n1-compat.yml`), triggered by `pull_request` path-filtered to
`src/**/Migrations/**` plus the proof's own load-bearing files (the workflow, the script, the
seam twins and their factory call sites — so a seam regression reds on the PR that introduces
it, not one migration-PR later), and by `workflow_dispatch`. It is
deliberately **not** a required check (Context: the path-filter deadlock) and deliberately absent
from the push-to-`main` backstop — the heaviest guard in the migration family runs only where a
schema change can actually enter (main is PR-only), keeping its cost at zero for the overwhelming
majority of PRs. `workflow_dispatch` covers assurance runs and re-verification after the fact.

### 5. Schema source: `dotnet ef migrations script --idempotent`, pinned tooling, one runnable script

The HEAD schema ships to the old suite as two idempotent scripts generated with the same
project/startup/context pairs the migrator uses (`ApplicationWriteDbContext` via
`TranslationSystem.Persistence`; `AuthDbContext` via `AuthSystem.Persistence` +
`AuthSystem.API` startup). `dotnet-ef` joins `.config/dotnet-tools.json` (pinned, like
`dotnet-stryker`) instead of the migrator image's floating global install. The whole job lives in
`scripts/n1-compat.sh` — generate, worktree, detect, build, run — so the CI workflow is a thin
caller and the identical proof (including the deliberate-red experiment) runs on a laptop. No
`.ps1` twin: the script's consumer is CI; local runs are maintainer verification of the guard
itself, and the twins exist for developer-workflow gates (`check-ssr-purity`,
`check-migration-safety`), not CI conductors (`apply-migrations.sh` has none).

## Consequences

### Positive

- The invariant the rollout depends on is now *executed*, not just linted: a backward-incompatible
  migration turns a PR job red before the merge, with the old suite's failing tests naming exactly
  what breaks.
- ADR-0023's accepted gap ("asserted by rule + lint, not proven by a test") closes.
- Zero steady-state cost: the job triggers only on migration-touching PRs and adds no step to
  `pr-verify`/`ci`.
- The proof is reproducible locally with the same script CI runs, including the red path
  (add a destructive migration locally, run `scripts/n1-compat.sh HEAD`, watch it fail).
- The seam is inert by default — normal test runs are untouched — and self-guarding: every
  misconfiguration throws.

### Negative / Accepted Trade-offs

- The proof is one release deep (N-1, not N-k) — exactly the window ADR-0023 §2 contracts;
  anything older was proven transitively by earlier runs.
- ~10–15 min of CI on migration PRs (full old build + two integration suites against
  Testcontainers). Accepted: migration PRs are rare and the repo is public (free runners).
- Old-suite flakiness reads as a red N-1 job. Accepted: the suites are the same ones the PR gate
  already trusts; re-run on flake.
- A non-required check can be ignored at merge time. Accepted deliberately (deadlock avoidance);
  the merge gate for migrations remains MIGR-03 + review, with this job as the loud alarm.
- The seam code (~one small class) is twin-copied into both integration test projects — the
  repo's test projects stay self-contained (no shared test library exists to host it). Marked as
  twins; removal of a seam file or a factory call site is red (the script asserts both on every
  run, and the workflow triggers on changes to any of them); subtler drift between the twins is
  caught by review.
- New integration suites with their own DbContext must adopt the seam (a new script file name +
  the same apply call) to be covered — recorded here as the extension point.

## Alternatives Considered

### A. Seam in the fixtures + idempotent HEAD scripts + old suite from a worktree (this ADR)

Chosen. Minimal, explicit, locally reproducible; false-green paths all throw.

### B. Run the previous release's suite unmodified

Rejected. Vacuous: each old fixture migrates its own container to the *old* schema
(`TranslationSystemApiFactory.cs:184`) and the job would green-light every migration ever
written. This is precisely why the seam must exist in the fixture.

### C. Shadow the `postgres:17-alpine` tag with an image whose initdb bakes the HEAD schema

Rejected. Works day-one with zero old-code cooperation (Testcontainers uses a local image when
the tag resolves), but silently redefines an upstream tag for everything on the runner, keys the
whole proof on an implementation detail of image resolution, and offers no honest failure mode —
the exact quiet-vacuity Decision 3 is built to refuse.

### D. A curated N-1 contract test subset

Rejected. The ticket allows it, but curation is a second backlog (every new slice needs a
decision), and the full old integration suites are already fast enough for a rare, path-filtered
job. "No silent narrowing" is cheapest when the scope is *everything the old release gated on*.

### E. Make the job a required check on every PR

Rejected. Required + path-filtered deadlocks non-migration PRs (the docs-only lesson); required
without a path filter taxes every PR with the heaviest job in the family. Non-required +
path-filtered keeps the signal where it matters.

### F. Define "previous release" by `v*` tag

Rejected for now. No tags exist and CD ships every `main` commit; a tag scheme would be invented
solely to serve this job. Decision 1 names the single line to revisit if tags ever arrive.

## Implementation Notes

- `tests/LotroKoniecDev.TranslationSystem.API.Tests.Integration/N1CompatSchemaSeam.cs` +
  `tests/LotroKoniecDev.AuthSystem.API.Tests.Integration/N1CompatSchemaSeam.cs` — twin copies
  (env-var read, `ExecScriptAsync` apply, migration-id parse, history verification); called from
  `TranslationSystemApiFactory.InitializeAsync` / `AuthSystemApiFactory.InitializeAsync` right
  after `StartAsync()`.
- Pure unit tests for the script parser live beside the TMS seam copy (no container needed).
- `scripts/n1-compat.sh` — generate scripts → assert HEAD seam → worktree `HEAD^1` (or the
  argument ref) → bootstrap detect → build + run the old integration suites with
  `N1_COMPAT_SCHEMA_SCRIPTS_DIR` set.
- `.github/workflows/n1-compat.yml` — thin caller; `pull_request` paths
  `src/**/Migrations/**`, `scripts/n1-compat.sh`, itself, the seam twins and the two factory
  files; `workflow_dispatch`.
- `.config/dotnet-tools.json` — pinned `dotnet-ef`.
- ADR-0023 — dated note on the closed trade-off; `CLAUDE.md` migrations rule + `tests/CLAUDE.md`
  gain one-line pointers.

## References

- Ticket #340 (MIGR-05), epic #335 (MIGR-00)
- ADR-0023 — the N-1 contract (Decision 2), the delegated proof (Decision 5)
- ADR-0012 — CD from `main`; §5 shared-schema smoke window (why N-1 is the contract)
- ADR-0008 §6 — the migrator is the sole schema writer
- `.github/workflows/pr-verify.yml` — discovery loop + the required-check/path-filter lesson
- `Dockerfile.migrator`, `scripts/apply-migrations.sh` — project/startup/context pairs and the
  `-- --connection` design-time mechanism
- EF Core docs — idempotent migration scripts; `__EFMigrationsHistory` semantics

## Amendment: the deploy-time leg — prove against the sha actually serving on prod (2026-07-27, #534)

Prod promotion is now **batched** (owner decision 2026-07-27, discussion on CD #180): staging
deploys every push to `main`, prod ships every Nth candidate behind the approval gate, and batch
diffs are deliberately **not** inspected by hand. That breaks the assumption Decision 1 was built
on — under batching, "previous merge" (`HEAD^1`, what the PR-time job proves) and "previous
deploy" (what the box is serving) diverge. An expand and its contract can ride one approved batch
with every per-step proof green while the release still serving breaks on the contract step;
ADR-0023's "across ≥ 2 deploys" is a property of *deploys*, so the gate must run at *promotion*
time.

The prod leg of `deploy.yml` therefore re-runs this proof against the serving release, after the
approval and before GHCR, Neon or any *change* to the box (the gate itself reads the box, over the
ssh transport configured one step earlier):

- `scripts/ci/resolve-prod-baseline.sh` resolves the baseline — the newest `production`
  deployment that ever reached a `success` status (the *history*, not the latest status: GitHub
  re-marks old successes `inactive`, and a latest-status read would false-bootstrap every mature
  promotion). Fail closed — an unresolvable baseline blocks the promotion.
- The `IMAGE_TAG` pinned in the box `.env` is both the fallback when the API cannot answer **and a
  cross-check when it can**. A deployment record carries the sha of the workflow *run*, not of the
  artifact that was rolled, so a manual `image_tag` deploy leaves a `success` record naming a commit
  the box never served — and the next unattended promotion would compute its span from that commit,
  hiding every migration in between. On disagreement the resolver takes the **older** commit (the
  wider span), and an unorderable pair (unrelated histories) fails closed. The cross-check never
  blocks on its own: an unreadable box warns and keeps the API's answer.
- **Bootstrap is the only fail-open verdict, so it is reserved for "nothing is serving":** no
  deployment record at all, or no `IMAGE_TAG` on the box. A window that lists deployments *without*
  a `success` is not that — on a mature environment every superseded candidate lands as `error`, so
  a promotion pause longer than the API window (measured 2026-07-27: 100 records ≈ 25 days) takes
  exactly that shape — and it resolves from the box instead of skipping.
- A span (`<baseline>..<candidate>`) with no `Migrations/` files skips in seconds; a pre-seam
  baseline (§ Bootstrap above) is a loud skip.
- Otherwise `scripts/ci/n1-promotion-gate.sh` runs the existing seam, `scripts/n1-compat.sh
  <baseline>`, and keeps its two failures apart — because they demand opposite fixes. Exit 1 (the
  serving release cannot live on this schema) aborts with the resolution: promote in smaller steps —
  approve a candidate containing only the expand, let it serve, then promote the contract. Exit 2
  (the proof never ran: restore, script generation, worktree, missing seam) aborts as **unjudged**,
  pointing at the infra failure. Collapsing the two would send the approver to split a healthy
  batch, or to the `image_tag` dispatch that skips this gate.
- A manual `image_tag` dispatch override deploys an artifact the checkout cannot reason about;
  the gate steps aside with a warning (the failure mode this closes is the unattended batch).

The PR-time `HEAD^1` job stays exactly as decided above — fast feedback next to the change; the
deploy-time leg is the real guarantee. The staging leg stays ungated: staging deploys every push,
so `HEAD^1` semantics still match it. Tests: `scripts/tests/resolve-prod-baseline.tests.sh` and
`scripts/tests/n1-promotion-gate.tests.sh` (guards job, next to the other bash gates).
