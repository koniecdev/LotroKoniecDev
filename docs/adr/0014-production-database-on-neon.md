# ADR-0014: Production database on Neon (Postgres 18) — one project, two databases on the direct endpoint, replacing Supabase

**Status:** Accepted
**Date:** 2026-06-29
**Decision-makers:** Solo maintainer
**Related:** ADR-0013 (Key Vault is the single source of truth — this swap is a **rotation of the two
`connection-string-{translation,auth}` secrets** it owns, no Terraform change), ADR-0012 (continuous
deployment — the pre-deploy migrator gate that builds the schema), ADR-0008 +
`docs/deployment/target-requirements.md` (cloud-agnostic deployment — the app is 12-factor and
provider-neutral, §3, so the DB provider is wiring, not code), ADR-0002 (pre-release, zero users —
no data migration; breaking/destructive DB swaps are free), `docs/deployment/neon-migration-plan.md`
(the execution plan), `docs/deployment/azure-supabase-bring-up-plan.md` (superseded for the DB
layer), `docs/deployment/runbook.md`.

## Context

ADR-0013 made Azure Key Vault the single source of truth for the 8 production secrets, two of which
are the database connection strings `connection-string-translation` + `connection-string-auth`. The
app layer reads them from environment variables (12-factor; ADR-0008 §3) and **does not care where
Postgres lives** — ADR-0013 §5 already proved a provider swap is just "change the two
`ConnectionStrings__*`", picked up via the **versionless** Key Vault secret URIs on the next ACA
revision, with no Terraform change.

Production ran on **two free Supabase projects** (one per database) per
`azure-supabase-bring-up-plan.md`. The Supabase free tier costs operational friction with no payoff
for a pre-release app: two separate projects (two dashboards, two `<ref>`-prefixed usernames), and a
**7-day inactivity pause** that needs an external keepalive to dodge cold wakes. Production has
**zero users** — the only row is the admin, auto-seeded on `auth-api` startup from `admin-password`
in Key Vault — so there is nothing to migrate and the swap is free (ADR-0002).

The two EF **write** contexts use disjoint default schemas, each with its own `__EFMigrationsHistory`:

- `ApplicationWriteDbContext` → `translation`
  (`TranslationSystem.Persistence/DatabaseSchemas.cs:5`, `ApplicationWriteDbContext.cs:40`).
- `AuthDbContext` → `authsystem`
  (`AuthSystem.Persistence/DatabaseSchemas.cs:5`, `AuthDbContext.cs:31`).

They could coexist in a single database, but dev and prod-parity compose provision **two** databases
(`lotro_translation` + `lotro_auth`, `scripts/init-postgres.sh`), and the architecture/runbook speak
of "two databases" throughout. (The runbook's Strategy bullet and verify query said the Auth schema
was `auth`; the code says `authsystem` — corrected alongside this ADR, code wins.)

## Decision

### 1. Production Postgres is Neon (managed Postgres 18); Supabase is decommissioned

A single Neon project (`lotro-translator-prod`). Supabase's two projects are **paused, then deleted**
only after the cutover smoke test is green (a fallback window).

### 2. One Neon project, two databases — `lotro_translation` + `lotro_auth`

Not the default `neondb`. This keeps **exact parity** with dev/prod-parity compose (two DBs via
`scripts/init-postgres.sh`) and with the runbook/architecture — zero dev↔prod drift. The cost is one
`CREATE DATABASE`; the shared Neon compute endpoint differs only by `Database=`.

### 3. Direct (non-pooled) endpoint for both the apps and the migrator

`Host=ep-small-water-as8kmmo2.c-4.eu-central-1.aws.neon.tech` (the host **without** `-pooler`),
`Ssl Mode=Require`, `Maximum Pool Size=10`. EF migrations are reliable only on a **direct**
connection — Neon's PgBouncer transaction-mode pooler breaks some DDL / advisory-lock paths — and the
migrator and apps **share the same two secrets**, so they must share the endpoint. With no users the
direct connection cap (~112 on the free 0.25 CU compute) dwarfs 2 apps × pool 10. `Ssl Mode=Require`
only: Neon presents a publicly-trusted certificate, so **no** `Trust Server Certificate`, and Npgsql
10 negotiates SNI (no `options=endpoint=…` hack).

### 4. The cutover is a Key Vault secret rotation + one migrator run — no Terraform change

The two `connection-string-*` secrets (ADR-0013) get a new **version** via `az keyvault secret set`;
ACA's versionless secret URIs pick it up on the next revision (re-roll). The pre-deploy migrator
(ADR-0012) builds both schemas on the empty Neon databases; `auth-api` re-seeds the admin on startup.
**Application code, the IaC, and the migrator image are untouched** — the neutrality contract
(ADR-0008 §3 / ADR-0013 §5) holds.

### 5. No keepalive

Neon free **autosuspends** compute (waking automatically in ~sub-second on the next request) rather
than pausing-until-manual-wake after 7 days like Supabase free. No keepalive workflow is needed —
none ever existed in this repo (the Supabase bring-up's keepalive phase was never implemented).

### 6. Pooling is a documented future step, not now

When real traffic arrives, switch the **apps** to the `-pooler` host (transaction pooling) while the
**migrator** stays on the direct endpoint — which requires splitting the two shared secrets (or an
apps-only pooler secret). Deferred (YAGNI) until users exist.

## Consequences

### Positive

- **One dashboard, one project, one credential set; no 7-day pause and no keepalive** to build or
  operate.
- **Dev / prod-parity unchanged** — still two databases, so compose parity holds with zero drift.
- **The provider swap cost was exactly what ADR-0013 predicted** — rotate two secrets, re-roll. No
  code, IaC, or state change; rollback is re-setting the two secrets to the Supabase values
  (Supabase stays paused until the smoke test passes).
- **Postgres 18** via Npgsql 10.0.1 (PG-18-aware): uuidv7 / virtual generated columns are available
  if ever needed (unused for now).

### Negative / Accepted Trade-offs

- **Direct endpoint, not pooled.** Accepted: pooling buys nothing at zero users and the pooler breaks
  EF DDL; revisit per Decision 6 when traffic arrives.
- **A single Neon compute serves both databases.** Accepted: free tier, no users; it matches the
  prior single-Postgres parity stack.
- **Forward-only after decommission.** Once Supabase is deleted there is no rollback target
  (ADR-0008 §6); the cutover deliberately keeps Supabase paused as a fallback until smoke is green.
- **Free-tier autosuspend cold start** on the first request after idle (~sub-second). Accepted for a
  pre-release app.

## Alternatives Considered

### A. Neon — one project, two databases, direct endpoint (this ADR)

Chosen. Parity with dev/compose, minimal operational change, free, no keepalive.

### B. Neon — one project, a single `neondb`, two schemas only

Rejected. The schemas already isolate the contexts, but a single DB diverges from dev/parity (two
databases) and from the runbook/architecture — dev↔prod drift for no gain. One `CREATE DATABASE`
avoids it.

### C. Neon — pooled (`-pooler`) endpoint for everything

Rejected. The transaction-mode pooler breaks EF migrations (DDL / advisory locks), and the migrator
shares the two secrets with the apps. There is no connection-count pressure at zero users to justify
it.

### D. Stay on Supabase

Rejected. Two projects, a 7-day pause needing a keepalive, `<ref>`-prefixed usernames — operational
friction with no pre-release benefit; the swap is free (ADR-0013 §5, ADR-0002).

## Implementation Notes

- **No code or IaC change.** The cutover is operational: `az keyvault secret set` ×2
  (`connection-string-translation`, `connection-string-auth`, both pointing at the Neon direct host)
  → ACA revision re-roll → migrator job run → smoke test. ADR-0013's versionless Key Vault URIs make
  it a Terraform-free rotation.
- **Connection strings live only in Key Vault** (ADR-0013) — never in a tracked file. Docs use the
  `<NEON_PASSWORD>` placeholder; the gitleaks gate guards this.
- **Schemas (verified in code):** `translation` (`TranslationSystem.Persistence/DatabaseSchemas.cs:5`)
  and `authsystem` (`AuthSystem.Persistence/DatabaseSchemas.cs:5`); each owns its
  `__EFMigrationsHistory`. The Auth migration history table lives in the `authsystem` schema
  (`AuthDbContextDesignTimeFactory.cs:25`).
- **Docs:** this ADR; `docs/deployment/runbook.md` (Supabase→Neon comments; **fix the stale Auth
  schema `auth` → `authsystem`** in the Strategy bullet and the verify query);
  `docs/deployment/azure-supabase-bring-up-plan.md` gets a "superseded for the DB layer" banner (kept
  — ADR-0012 links it); `docs/deployment/neon-migration-plan.md` (the step-by-step execution plan).
- **Optional follow-up (separate concern):** bump compose `postgres:17-alpine` → `18-alpine` for
  prod-parity — mind the PG 18 `PGDATA` / `VOLUME` path move — in `compose.yaml` + `compose.prod.yaml`.

## References

- ADR-0013 — Key Vault is the single source of truth (this swap rotates its two `connection-string-*`
  secrets)
- ADR-0012 — continuous deployment pipeline + the pre-deploy migrator gate
- ADR-0008 + `docs/deployment/target-requirements.md` — cloud-agnostic deployment / 12-factor app
  neutrality (a provider swap is wiring, not code)
- ADR-0002 — pre-release, zero users (no data migration; breaking changes free)
- `docs/deployment/neon-migration-plan.md` — the execution plan
- `docs/deployment/azure-supabase-bring-up-plan.md` — superseded for the database layer
- `docs/deployment/runbook.md` — standing operations (env matrix, migrations, smoke test)
- Neon docs — connection pooling (direct vs `-pooler`) and compute autosuspend; Npgsql 10 Postgres 18
  support
