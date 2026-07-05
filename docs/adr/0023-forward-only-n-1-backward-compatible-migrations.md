# ADR-0023: Migration safety — forward-only, N-1 backward-compatible, expand → backfill → contract

**Status:** Accepted
**Date:** 2026-07-05
**Decision-makers:** Solo maintainer
**Related:** ADR-0012 (CD pipeline — the §4 migration gate, the §5 shared-schema smoke window,
code-only rollback; its §4 wording is amended alongside this ADR), ADR-0008 (§6 pre-deploy
migration job; its §5 "no backup topology" sentence is superseded alongside this ADR), ADR-0014 +
ADR-0018 (Neon prod / staging — the PITR + branching recovery substrate), ADR-0002 (pre-release
"breaking changes are free" — scoped by this ADR to the product level), epic #335 (MIGR-00),
tickets #336–#340 (MIGR-01…05), audit 0001 finding C4, `docs/deployment/runbook.md`, the
`CLAUDE.md` "Migrations" house rule (added with this ADR, ticket #337 / MIGR-02).

## Context

The zero-downtime rollout bakes in a schema contract that no document states. The deploy order is
fixed by `deploy.yml`:

- **The schema commits first.** The step `Run migrations (gate — rollout blocked unless this
  succeeds)` (`deploy.yml:162`) pins the migrator job to the tested `:sha`, starts it, and polls
  the ACA execution to `Succeeded` before any API rolls (ADR-0012 §4).
- **The previous revision then serves on the new schema.** The health-gated rollout (ADR-0012 §5
  amendment) creates each candidate at 0% traffic; throughout the smoke window the **previous**
  app revision serves 100% of production traffic against the freshly-migrated schema.
- **Rollback moves code, never schema.** `Roll back on failure` (`deploy.yml:352`) restores
  traffic to the previous revision and deactivates the candidate. There is no schema-down path
  anywhere in the pipeline.

So in two windows — every deploy's smoke phase, and the end state of any failed deploy after the
gate — the **N-1 revision runs on the N schema by design**. If a migration is destructive or
backward-incompatible, "roll back on failure" does not return to safety; it returns to an outage:
old code against a schema it cannot read, with `replica_retry_limit = 1`
(`iac/migrator-job.tf:8`) and no automated recovery (audit 0001 C4).

The migrator mechanics are execution-safe but semantics-blind: `scripts/apply-migrations.sh` runs
two self-contained EF bundles under `set -e` (line 19), idempotently via each context's
`__EFMigrationsHistory`. The gate proves a migration **ran**; a valid-but-destructive
`DropColumn` passes it without comment.

Meanwhile the contract is written nowhere and guarded nowhere:

- ADR-0012 §4 closes with "Forward-only (ADR-0008 §6; snapshot before applying in a real
  environment)" — but ADR-0008 §6 only defines the pre-deploy bundle job, not any forward-only /
  compatibility discipline, and **nothing takes a snapshot** (`runbook.md:610-613` and
  `target-requirements.md:38` repeat the same aspirational advice).
- `CLAUDE.md` has no migrations rule, so neither a human contributor nor the **autonomous backlog
  loop** is bound to the invariant the rollout depends on.
- Integration tests run new code × new schema (same commit) — the N-1 window is never exercised.

One tension needs resolving explicitly: ADR-0002 makes breaking changes free (pre-release, zero
users). That freedom is **product-level** — contracts, data semantics, no deprecation windows. It
does not extend to the rollout machinery: even at zero users, a backward-incompatible migration
converts an auto-recovering failed deploy into a broken environment needing manual DB surgery.

What actually exists as a recovery substrate: prod and staging each run on **Neon** (ADR-0014,
ADR-0018), which provides history retention (point-in-time restore) and instant branching —
currently undocumented and unverified in this repo.

## Decision

### 1. Migrations are forward-only; `Down()` never runs in shared environments

Recovery from a bad migration is **roll-forward** (a new migration) or **database restore**
(Decision 5) — never `Down()` / `dotnet ef database update <previous>` against staging or prod.
`Down()` methods stay in the codebase for local dev iteration only (rewinding a WIP migration on
a throwaway DB). Reasons: down paths are untested against real data, lossy by construction for
exactly the operations that matter (the inverse of `AddColumn` is `DropColumn`), and the pipeline
has no down executor anyway — `deploy.yml:352` moves traffic, never schema.

### 2. Every migration is N-1 backward-compatible — the running revision must survive it

The invariant each migration must satisfy: the **currently-deployed app revision keeps working
against the schema as it stands after the migration** — every query, command and EF model binding
the N-1 code can issue still succeeds. This is not a new constraint; it is what the rollout
already assumes in the two Context windows. This ADR merely makes it binding. Scope: prod and
staging (the environments the pipeline rolls). Local dev databases are throwaway
(`docker compose down -v`) and exempt.

### 3. Destructive changes ship as expand → backfill → contract, across ≥ 2 deploys

Destructive = anything the N-1 model cannot survive, or that can fail on live data:

- dropping a column or table the previous revision still maps;
- renaming a column or table (data survives; the N-1 model still queries the old name);
- changing a column type;
- adding `NOT NULL` to an existing column (the old code does not write it);
- tightening a constraint, or adding a unique index over existing data (the apply itself can
  fail on duplicates, and the still-serving old code can keep writing violations).

The recipe:

1. **Expand** (deploy 1): add the new shape additively — nullable column, new table — with code
   that writes both shapes and reads either.
2. **Backfill**: populate the new shape (inside the migration for small data sets; an idempotent
   follow-up step for large ones).
3. **Contract** (deploy 2+): once no serving revision touches the old shape, drop or tighten it.
   The contract migration is N-1-safe by construction — revision N-1 at that point is the expand
   release, which already stopped depending on the old shape — and it carries the Decision 4
   acknowledgment marker, since the gate will (correctly) flag its DDL.

### 4. Enforcement is a CI text gate (MIGR-03) with an in-file acknowledgment escape hatch

- `scripts/check-migration-safety.sh` (+ `.ps1` twin; ticket #338) — the schema analog of
  `check-ssr-purity.sh`: a pure text scan over **newly added** migration files
  (`**/Migrations/*.cs`, excluding `*.Designer.cs` and `*ModelSnapshot.cs`) that fails the build
  when an `Up(...)` body contains a destructive operation
  (`DropColumn|DropTable|RenameColumn|RenameTable|DropIndex|AlterColumn` — exact matrix in #338).
  A **text scan, not a DDL-level linter**: it needs no SDK, so it drops into the exact slot
  ssr-purity occupies in both `ci.yml` (line 37 analog) and `pr-verify.yml` (line 98 analog, with
  the `force_build` self-trigger regex at line 63 extended) — before `setup-dotnet`. Accuracy is
  proportional: EF-generated migrations spell these operations literally. Squawk over
  `dotnet ef migrations script` is the recorded upgrade path if the regex proves too coarse.
- **Escape hatch** — defined precisely here so #338 implements without re-deciding: a line
  comment **inside the flagged migration file** containing the token
  `MIGRATION-SAFETY: acknowledged`, followed by free text naming the reason (which
  expand/contract step this is, or why the destruction is safe — e.g. the table never shipped).
  A flagged file carrying the token passes. In-file beats a PR label: it lands in the diff, the
  blame and the squashed history next to the DDL it excuses, and it works identically in local
  runs and the `.ps1` twin with no GitHub API.

### 5. The recovery net is restore, not rollback

- The real safety valve for a logically-bad or data-corrupting migration that already committed
  is **restoring the database**: Neon PITR / branch-restore. MIGR-01 (#336) verifies it, records
  the actual retention windows for the prod and staging projects, and writes the runbook
  "Recover from a bad migration" procedure. Until #336 lands, the net is ambient and unverified —
  the state this ADR stops papering over.
- MIGR-04 (#339, post-mvp) later wires a pre-migration restore point (a Neon branch) into
  `deploy.yml` immediately before the gate step. Ruled here, answering #339's guard question:
  the snapshot leg is **best-effort, never mandatory** — an unconfigured or failing snapshot API
  skips cleanly (visible in the run summary) rather than blocking the deploy, because Neon's
  ambient PITR already covers the window and a hard dependency on an external snapshot API buys
  availability risk before it buys safety. Revisit only if a real incident shows PITR alone was
  insufficient.
- MIGR-05 (#340, post-mvp) is the eventual automated proof: the previous release's suite against
  the HEAD schema.

### 6. The rule binds humans and the autonomous loop via `CLAUDE.md`

A terse **Migrations** house rule lands in `CLAUDE.md` (this ticket) linking this ADR — the same
mechanism that makes every other repo-wide discipline binding on human contributors and the
backlog loop alike. The stale wording is corrected at its sources: ADR-0012 §4 and ADR-0008 §5/§6
receive dated amendment notes pointing here and at the real mechanisms (MIGR-01 procedure,
MIGR-04 snapshot). The runbook's equivalent aspirational lines are rewritten by #336 together
with the recovery procedure, not here.

## Consequences

### Positive

- The invariant the pipeline silently depends on is named, owned, and — via #338 — mechanically
  enforced for humans and the autonomous loop alike.
- The rollback story becomes coherent: restoring traffic is guaranteed to land on working code,
  because the schema beneath it is contractually compatible.
- Destructive changes become deliberate two-step events with an audit trail (the in-file marker)
  instead of accidents that pass a green gate.
- Recovery gains a named, verifiable substrate (Neon PITR + runbook procedure + optional
  pre-migration branch) instead of an aspirational parenthetical.
- Expand → backfill → contract is the industry-standard discipline; it scales past zero users
  unchanged.

### Negative / Accepted Trade-offs

- Destructive cleanups take ≥ 2 deploys — slower schema housekeeping; pre-release, that ceremony
  is occasionally strictly unnecessary (ADR-0002). Mitigated: the escape hatch costs one comment
  line.
- A text-scan gate is approximate. False positives (e.g. dropping a table introduced in the same
  PR) are acknowledged away rather than auto-detected; false negatives (destructive raw
  `migrationBuilder.Sql("…")`) pass silently until the Squawk upgrade path is taken.
- `Down()` methods become permanently dead code for shared environments (still generated, never
  executed there).
- Until MIGR-05 lands, N-1 compatibility is asserted by rule + lint, not proven by a test.
  Accepted: post-mvp, proportional to zero users.
- Nothing restores data automatically — the net is a documented manual procedure. Accepted at
  this scale.

## Alternatives Considered

### A. Forward-only + N-1 contract + expand/backfill/contract + text gate + restore net (this ADR)

Chosen. Matches what the pipeline already does mechanically; adds the missing written contract,
the enforcement, and a verified recovery path.

### B. Down-migrations as the production rollback path

Rejected. `Down()` is untested against real data, destroys data by construction for the exact
operations that motivate rollback, would need its own gated executor in `deploy.yml`, and inverts
the expand/contract model the health-gated rollout already assumes.

### C. No contract — lean on ADR-0002's "breaking changes are free"

Rejected. That freedom is product-level. The shared-schema windows are pipeline-level: without
N-1 compatibility, a failed smoke turns from "traffic restored, incident over" into "previous
revision broken on the new schema" — at any user count.

### D. Squawk (DDL-level lint over `dotnet ef migrations script`) as the gate, now

Rejected for now. More accurate (real DDL), but it needs the .NET SDK and script generation in
CI, so it cannot sit in the cheap pre-`setup-dotnet` slot the ssr-purity pattern occupies.
Disproportionate while EF-generated migrations state the flagged operations literally. Recorded
as the upgrade path in #338.

### E. PR-label escape hatch (`migration-reviewed`) instead of the in-file marker

Rejected. A label is invisible to local runs and the `.ps1` twin, requires the GitHub API in the
scanner, and evaporates from history at squash-merge; the in-file marker rides the diff, the
blame and both scanners for free.

### F. Mandatory pre-migration snapshot on every deploy, now

Rejected. Neon's ambient PITR already covers the window at zero users; a hard deploy dependency
on an external snapshot API adds availability risk before it adds safety. MIGR-04 wires the
snapshot best-effort, post-mvp (Decision 5).

## Implementation Notes

- **New:** this ADR; the `CLAUDE.md` "Migrations" house rule (both ticket #337 / MIGR-02).
- **Amended alongside (same ticket):** ADR-0012 §4 — dated amendment replacing the aspirational
  "(ADR-0008 §6; snapshot before applying in a real environment)" with pointers here and to
  MIGR-01/MIGR-04; ADR-0008 §6 — dated note that the discipline later ADRs cite it for lives
  here; ADR-0008 §5 — the "No HA/backup topology is committed now" sentence superseded by
  ADR-0014/ADR-0018 (Neon) + MIGR-01.
- **Enforcement (ticket #338, not here):** `scripts/check-migration-safety.{sh,ps1}`; wiring into
  `ci.yml` + `pr-verify.yml` mirroring ssr-purity's placement (`ci.yml:37`, `pr-verify.yml:98`,
  `force_build` regex `pr-verify.yml:63`); pass/fail/acknowledged fixtures.
- **Recovery (tickets #336 / #339 / #340, not here):** runbook "Recover from a bad migration" +
  recorded retention windows; best-effort deploy-time snapshot; N-1 CI proof. The same
  aspirational wording in `runbook.md:610-613`, `runbook.md:487-488` and
  `target-requirements.md:38` is superseded in meaning by this ADR and rewritten when #336/#339
  land.
- **Unchanged:** `scripts/apply-migrations.sh`, `Dockerfile.migrator.prod`,
  `iac/migrator-job.tf`, the `deploy.yml` gate and rollback steps — the mechanics already
  implement this model; this ADR writes down the contract they assume. No code change; no build
  impact.

## References

- Epic #335 (MIGR-00) + tickets #336 (MIGR-01), #338 (MIGR-03), #339 (MIGR-04), #340 (MIGR-05)
- ADR-0012 — continuous deployment pipeline (§4 gate, §5 smoke window + code-only rollback)
- ADR-0008 — cloud-agnostic deployment (§6 pre-deploy migration job; §5 backup posture)
- ADR-0014 / ADR-0018 — Neon production / staging projects (PITR + branching substrate)
- ADR-0002 — pre-release, zero users (the product-level freedom this ADR scopes)
- `docs/audits/0001-infrastructure-audit.md` — finding C4 (forward-only on the only live DB,
  no snapshot, no rollback)
- `.github/workflows/deploy.yml` (`:162` migration gate, `:352` traffic-only rollback),
  `scripts/apply-migrations.sh`, `iac/migrator-job.tf`
- `scripts/check-ssr-purity.sh` + `ci.yml:37` / `pr-verify.yml:63,98` — the enforced-gate pattern
  #338 mirrors
- EF Core docs — migrations in production (bundles; down-migrations as a dev-time tool)
- Squawk — <https://github.com/sbdchd/squawk> (the recorded DDL-level upgrade path)
