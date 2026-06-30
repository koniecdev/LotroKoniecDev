# ADR-0018: Staging environment & two-stage promotion (staging auto → prod gated)

**Status:** Accepted
**Date:** 2026-06-30
**Decision-makers:** Solo maintainer
**Related:** audit 0001 §H10 (staging CI job / promotion model — left as future work), §C5
(staging-on-prod-secrets/state trap); ADR-0017 (per-env IaC parametrization — made staging
*instantiable*; this ADR makes it *delivered*), ADR-0012 (CD pipeline — single `production` gate /
"prod is de-facto staging"), ADR-0008 (cloud-agnostic deployment + the two-environment intent),
ADR-0013 (Key Vault as the per-env secret source), ADR-0016 (env_id-parametrized telemetry)

## Context

ADR-0008 §1 committed to **two environments — staging + production**. ADR-0017 closed the IaC half:
the `iac/` root is parametrized (`env_id`, `public_base_domain`), prod and staging hold **separate
Terraform state blobs** (`backend-config/{prod,staging}.hcl`), and `iac/env/staging.tfvars` exists — but
ADR-0017 §7 explicitly deferred the **CI / promotion model** ("a staging CI job / promotion model
(audit §H10) is a separate change"). ADR-0012 meanwhile shipped a single `production` approval gate and
treated prod as de-facto staging.

This ADR delivers §H10: a real **staging** environment, fed by CI/CD, with a **promotion** boundary
between it and production. Four constraints shape the design:

- **Prod must stay behavior-identical.** The existing `cd.yml` / `infra.yml` prod path (issuer/redirect
  strings, RG, the health-gated rollout, the `prod-mutation` lock) cannot regress, and merging this work
  must not break prod *before* staging infra exists.
- **Full always-on mirror (maintainer call).** Staging runs the same `iac/` root with `min=max=1` like
  prod — truest parity (including warm-revision rollout semantics), accepting ~2× steady ACA spend on the
  "Azure for Students" subscription. No cost-knobs in IaC.
- **A separate Neon project for staging** (full DB isolation) — a deliberate deviation from ADR-0017 §7,
  which wrote "Neon *branch*".
- **Secrets stay per-env in Key Vault (ADR-0013).** A *separate* `lotrotms-kv-staging` vault holds
  freshly-generated staging secrets; no app secret moves into GitHub (§C5).

## Decision

### 1. Build-once, promote-the-artifact

`cd.yml` builds the four images **once** (`build-and-push`, unchanged — signed + attested), then:

```
deploy-staging   env: staging      AUTO   (no protection rule)         needs: build-and-push
deploy-prod      env: production    GATED  (required reviewer)          needs: deploy-staging
```

The **`production` approval gate is the staging→prod promotion control**: the exact `sha-<short>` that
passed the staging rollout + smoke is what a human promotes to prod. There is no separate prod build, so
staging and prod are provably the same artifact.

### 2. GitHub Environments are the configuration seam

The prod-hardcoded workflow `env:` (RG, app names, public URLs, migrator job) becomes **environment-scoped
GitHub `vars`** (same names, per-env values) and the rollout body is extracted into a **reusable
`deploy.yml`** (`workflow_call`, `environment` input) called once per environment. `SMOKE_CLIENT_SECRET`
is an env-scoped secret on staging (repo-level on prod). One job body serves both environments by the
`environment:` it runs under — which also selects the correct Azure **federated credential**
(`gh-env-production` / `gh-env-staging`) so each leg's OIDC login is environment-pinned. All
Azure-mutating steps stay in that one job (the federated credential trusts only that environment's
subject — the ADR-0012 "one job" constraint is preserved per environment).

### 3. `infra.yml` mirrors the same promotion

`plan` runs a **matrix** over prod + staging on PRs (read-only, `pull_request` credential); on push to
main, **`apply-staging` (auto)** → **`apply-prod` (gated, `needs: apply-staging`)**, each on its own
state blob. A botched staging apply can never touch prod state (ADR-0017 §4). `apply-prod` keeps the
`prod-mutation` concurrency lock shared with `cd.yml` deploy-prod; staging uses a `staging-mutation` twin.

### 4. `STAGING_ENABLED` makes the rollout safe and reversible

A repo variable `STAGING_ENABLED` (default **false**) gates the staging legs, alongside the existing
`CD_ENABLED`. While false: staging is skipped and — because `deploy-prod` / `apply-prod` tolerate a
*skipped* staging (`if: always() && … && (…result == 'success' || … == 'skipped')`) — **prod keeps
deploying exactly as today**. This is what lets the workflow PRs merge *before* staging infra is seeded.
Flipping it to `true` (last bring-up step) turns on staging-auto and makes prod a true promotion (prod
blocked when staging smoke is red). Unsetting it later instantly pauses staging.

### 5. Out-of-band staging prerequisites (separate from prod, §C5)

A separate `lotrotms-kv-staging` vault + `lotrotms-aca-staging` identity (seeded by the existing,
already-parametrized `scripts/seed-keyvault.{sh,ps1}` via `KV_*` overrides), a separate **Neon project**
(two databases) for the connection strings, a `gh-env-staging` federated credential + `Contributor` on
`rg-lotrotms-staging-polc-001`, and the `staging` GitHub environment + env-scoped vars — all created
out-of-band (data sources / platform config), never committed.

## Consequences

### Positive

- **A real two-environment promotion:** every green-CI commit auto-validates on a full prod mirror, and
  prod releases are a one-click promotion of an already-staging-verified artifact — the §H10 intent.
- **Blast-radius isolation:** separate Neon project, separate KV, separate Terraform state blob,
  separate ACA stack — a staging mistake cannot reach prod data, secrets, or state (§C5 defused).
- **Prod is provably untouched** at merge time (the `STAGING_ENABLED`-false path is byte-identical to
  today) and the deploy logic is now a single source of truth (`deploy.yml`) instead of duplicated jobs.
- **One artifact, one mental model** — staging and prod differ only by environment-scoped config, never
  by build.

### Negative / Accepted Trade-offs

- **~2× steady ACA spend** on the student subscription (full always-on mirror). Accepted; **revisit
  trigger:** credit pressure → drop staging to scale-to-zero via `env/staging.tfvars` (`min_replicas=0`),
  no code change.
- **A separate Neon project** uses more of the Neon plan than a branch would, and does not share prod's
  data — intentional (isolation > convenience); deviates from ADR-0017 §7's wording, recorded here.
- **More GitHub configuration surface** (env-scoped vars/secrets on two environments, a `STAGING_ENABLED`
  switch) and one layer of reusable-workflow indirection — accepted for DRY + single-source-of-truth.

## Alternatives Considered

### A. Build-once + promote through GitHub Environments (this ADR)

Chosen. One artifact, strongest isolation, smallest prod risk (the `STAGING_ENABLED` path preserves prod
exactly), and DRY via a reusable workflow.

### B. Independent (non-promotion) gates — staging and prod each deploy from main independently

Rejected. Loses the build-once guarantee and the "prod runs exactly what staging ran" property; prod
could deploy a commit staging never saw. The promotion `needs: deploy-staging` is the whole point.

### C. A Neon **branch** for staging (ADR-0017 §7's original wording)

Rejected. Copy-on-write off prod is cheaper and instant, but couples staging to prod compute and data —
the opposite of an isolated mirror. A separate project is the enterprise-correct call here.

### D. Terraform workspaces / a duplicated `iac-staging/`

Rejected (already rejected in ADR-0017 §C/§D for the IaC layer): workspaces give weaker state isolation,
a copy drifts. This ADR keeps ADR-0017's parametrized-root + separate-blob model.

### E. Duplicate the `deploy-prod` job for staging instead of a reusable workflow

Rejected. Two ~150-line near-identical jobs are a maintenance footgun (change one, forget the other —
the repo already tracks this pain for script twins). `workflow_call` is the single source of truth.

## Implementation Notes

- **New:** `.github/workflows/deploy.yml` (reusable rollout, `environment` input); this ADR.
- **Changed:** `.github/workflows/cd.yml` (env-scoped `vars`/`secrets`; `deploy-staging` auto +
  `deploy-prod` gated promotion calling `deploy.yml`); `.github/workflows/infra.yml` (plan matrix +
  `apply-staging` → `apply-prod`); `docs/deployment/runbook.md` (two-stage promotion + staging bring-up +
  the env-scoped vars/secrets contract + DNS records).
- **Unchanged (deliberately):** `cd.yml` `gate` + `build-and-push`; the health-gated rollout steps (they
  move verbatim into `deploy.yml`); the ADR-0013 KV secret model; every `${var.env_id}`-derived resource
  name; the three `Program.cs` (env-name-agnostic, audit G13).
- **Out-of-band (CLI, done at decision time):** staging RG, `gh-env-staging` federated credential,
  `Contributor` on the staging RG, the GitHub `staging` environment, env-scoped `vars` for both
  environments. **Last step:** flip `STAGING_ENABLED=true` after staging KV seed + `terraform apply` +
  custom-domain binding + DNS.
- **Supersession:** ADR-0012's single-gate "prod is de-facto staging" is superseded for env topology by
  the two-stage promotion here; ADR-0008's two-env model is now real end-to-end; ADR-0017 §H10 is closed.

## References

- `docs/audits/0001-infrastructure-audit.md` — §H10 (staging CI/promotion), §C5 (isolation)
- ADR-0017 — per-environment IaC parametrization (the instantiable-staging foundation this builds on)
- ADR-0012 — continuous deployment pipeline (its single `production` gate is generalized here)
- ADR-0008 — cloud-agnostic deployment & the two-environment strategy (delivered in CI/CD here)
- ADR-0013 — Key Vault as the per-environment secret source (the separate staging vault, §C5 boundary)
- ADR-0016 — env_id-parametrized App Insights (the parametrization precedent staging inherits)
