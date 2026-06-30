# ADR-0018: Staging environment & two-stage promotion (staging auto → prod gated)

**Status:** Accepted
**Date:** 2026-06-30 (amended 2026-07-01 — shared-environment reality)
**Decision-makers:** Solo maintainer
**Related:** audit 0001 §H10 (staging CI / promotion model — was future work), §C5 (isolation);
ADR-0017 (per-env IaC parametrization — made staging *instantiable*), ADR-0012 (CD — single
`production` gate), ADR-0008 (cloud-agnostic deployment + the two-environment intent), ADR-0013 (Key
Vault per-env secrets), ADR-0016 (env_id-parametrized telemetry / the OTel agent)

## Context

ADR-0008 §1 committed to **two environments — staging + production**. ADR-0017 closed the IaC half
(parametrized root, per-env state blob, `env/staging.tfvars`) but deferred the **CI / promotion model**
(§H10). This ADR delivers it: a real **staging** environment fed by CI/CD with a **promotion** boundary
to production.

A hard external constraint, discovered while building it, shapes the topology: the **"Azure for
Students" subscription permits exactly ONE Container Apps Environment in the whole subscription** — not
one *per region*. Both `MaxNumberOfRegionalEnvironmentsInSubExceeded` (a second env in Poland Central)
**and** `MaxNumberOfGlobalEnvironmentsInSubExceeded` (a second env in Germany West Central) were hit, so
a region change does not help. Prod holds the one environment (`lotrotmsenvprod`, Poland Central).
A *separate* staging environment is therefore impossible on this subscription.

Other constraints: prod must stay behavior-identical; secrets stay per-env in Key Vault (ADR-0013); the
staging DB is a separate Neon project (full DB isolation).

## Decision

### 1. Build-once, promote-the-artifact

`cd.yml` builds the four images **once** (`build-and-push`, unchanged), then:

```
deploy-staging   env: staging      AUTO   (no protection rule)         needs: build-and-push
deploy-prod      env: production    GATED  (required reviewer)          needs: deploy-staging
```

The **`production` approval gate IS the staging→prod promotion**: the exact `sha-<short>` that passed
the staging rollout + smoke is what a human promotes. There is no separate prod build.

### 2. Staging shares the production ACA environment (forced by the 1-env cap)

Staging is **not** a separate Container Apps Environment. Its apps
(`lotrotms-{auth-api,tms-api,frontend}-staging`) + `lotrotms-migrator-staging` run **inside**
`lotrotmsenvprod`, referenced by id. What stays **separate and isolated** for staging: the three apps
(own revisions/scaling/ingress), the **separate Neon project** (`lotro_translation` + `lotro_auth`),
`lotrotms-kv-staging` + `lotrotms-aca-staging` identity + its 8 secrets, the custom domains
`*.staging.lotro-translator.pl`, and the Data-Protection keyring (own storage account + env-storage
links named `auth-keys-staging` / `frontend-keys-staging` on the shared env). What is **shared** with
prod: the ACA environment itself, its Log Analytics workspace, and the managed OTel agent → prod's
Application Insights (telemetry co-mingled, distinguishable by app name). The resource group, Key Vault
and identity stay in Poland Central.

This is weaker than a fully separate environment (shared env infra + telemetry sink), but the isolation
that matters for a QA tier — data, secrets, images, domains — is preserved; an app in `staging` cannot
reach prod's database or secrets.

### 3. IaC: a "shared-environment" mode in the flat root (no module, no copy)

New `var.aca_environment_name` / `var.aca_environment_resource_group` (default `""` = create our own).
`local.create_env = var.aca_environment_name == ""`; `local.app_env_id` resolves to either the created
env or a `data.azurerm_container_app_environment` of the shared one. The environment, Log Analytics,
App Insights, the OTel agent and all of `monitoring.tf` are **`count`-guarded on `create_env`**, each
with a `moved {}` block (the ADR-0017 RG-rename pattern) so the **prod plan shows state moves only —
zero infrastructure change**. The apps + migrator + the env-storage links point at `local.app_env_id`;
the env-storage names carry the `env_id` suffix only for the shared case so they never collide with
prod's on the same environment. (ADR-0017's Rank-1 module extraction is still deferred — this is the
minimal conditional that the 1-env cap forced.)

### 4. `infra.yml` mirrors the same promotion; `STAGING_ENABLED` makes it safe

`plan` runs a prod+staging matrix on PRs; on push to main `apply-staging` (auto) → `apply-prod` (gated),
on separate state blobs. A repo variable **`STAGING_ENABLED`** (default false) gates the staging legs
alongside `CD_ENABLED`; while false, staging is skipped and prod deploys/applies **exactly as before**
(`deploy-prod`/`apply-prod` use `always()` + a `(success||skipped)` guard tolerating a skipped staging).
Flipping it to true turns on staging-auto and makes prod a true gated promotion.

### 5. Out-of-band prerequisites (separate from prod, §C5)

A separate `lotrotms-kv-staging` + `lotrotms-aca-staging` (seeded by `scripts/seed-keyvault.sh` with
`KV_*` overrides), a separate **Neon project** (two DBs), a `gh-env-staging` federated credential +
`Contributor` on `rg-lotrotms-staging-polc-001`, the `staging` GitHub environment + env-scoped `vars`,
and the custom-domain bindings (asuid TXT + CNAME/A → the shared env's IP/FQDNs). The staging RG was
pre-created and imported into the staging state.

## Consequences

### Positive

- A real two-environment promotion within a free subscription: every green-CI commit auto-validates on
  a prod-adjacent staging tier, and prod releases are a one-click promotion of an already-verified
  artifact. Verified end-to-end: a green `deploy-staging` left `deploy-prod` `waiting` for approval, and
  a red staging smoke left `deploy-prod` `skipped`.
- Strong isolation where it matters (separate Neon project, KV, identity, apps, domains, state blob).
- Prod is provably untouched at merge time (the `STAGING_ENABLED`-false path) and one source of truth
  for the rollout (`deploy.yml`).

### Negative / Accepted Trade-offs

- **Shared ACA environment + Log Analytics + telemetry** with prod — staging logs/traces co-mingle in
  prod's App Insights (filter by app name). Forced by the 1-env cap, not chosen. Revisit if a second
  subscription (or a paid plan lifting the cap) becomes available — `create_env` makes a true split a
  var-file change.
- The flat-root conditionals (`count` + `moved` across env/LAW/AI/OTel/monitoring) add surgery to the
  prod state addresses; accepted via the proven ADR-0017 `moved {}` pattern (prod plan: 0 change).
- A separate Neon project does not share prod data — intentional.

## Alternatives Considered

- **Separate ACA environment for staging** (the original intent) — *impossible* on this subscription
  (1-env global cap; a region change to Germany West Central also failed). Would need a second
  subscription (real cost) — rejected for now.
- **A separate subscription** for staging — full isolation but real monthly cost; deferred.
- **Local-only staging** (`compose.prod.yaml` parity) — loses the deployed-staging + auto-CD the goal
  requires; rejected.
- **Independent (non-promotion) gates / Neon branch / workspaces / `iac-staging` copy** — rejected as in
  ADR-0017 (weaker isolation / drift / loses the build-once guarantee).

## Implementation Notes

- **New:** `.github/workflows/deploy.yml` (reusable rollout, `environment` input); this ADR.
- **Changed:** `cd.yml` (env-scoped vars/secrets; `deploy-staging` + `deploy-prod` callers);
  `infra.yml` (plan matrix + `apply-staging`→`apply-prod`); `iac/` shared-env mode
  (`var.aca_environment_*`, `local.create_env`/`app_env_id`, `count`+`moved` on
  env/LAW/AI/OTel/monitoring, env-storage suffix, apps/migrator → `local.app_env_id`);
  `iac/observability.tf` OTel azapi patch re-supplies `appLogsConfiguration` (the #246 prod-apply fix);
  `docs/deployment/runbook.md`.
- **Unchanged (deliberately):** `gate` + `build-and-push`; the health-gated rollout steps (verbatim in
  `deploy.yml`); the ADR-0013 KV model; the three `Program.cs` (env-name-agnostic).
- **Supersession:** ADR-0012's single-gate model is generalized to the two-stage promotion; ADR-0008's
  two-env intent is now real end-to-end; ADR-0017 §H10 is closed.

## References

- `docs/audits/0001-infrastructure-audit.md` — §H10, §C5
- ADR-0017 — per-environment IaC parametrization (the foundation this builds on)
- ADR-0012 / ADR-0008 / ADR-0013 / ADR-0016 — CD pipeline / env strategy / KV secrets / OTel agent
