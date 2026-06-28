# ADR-0012: Continuous deployment pipeline (automated ACA rollout + GitHub approval gate + IaC in CI)

**Status:** Accepted
**Date:** 2026-06-28
**Decision-makers:** Solo maintainer
**Related:** ADR-0008 (cloud-agnostic deployment & environment strategy — **superseded** here on its
"no IaC / first deploy is human-driven / provider undecided" stance, now that the provider is chosen:
ACA + Supabase, per `docs/deployment/azure-supabase-bring-up-plan.md`), ADR-0005 (DP keyring
persistence), ADR-0006 (dev compose posture), `iac/`, `.github/workflows/{cd,infra,smoke}.yml`,
issue #217.

## Context

After M6 the pipeline does **continuous delivery to GHCR but not deployment**: `cd.yml` builds and
pushes the four images, then stops. The live ACA apps pin `image = ":latest"` with
`revision_mode = "Single"`, so `terraform apply` with an unchanged image string produces **no new
revision** — pushed code never rolls out. The migrator is a **manual-trigger** ACA Job (the R6
"gate API rollout on migration success" rule was documented but never enforced on the live
environment). There are **no GitHub Environments** (no approval gate). CI holds **no Azure
credentials**. The only path to prod is a manual laptop ritual — local `terraform apply` + a manual
`az containerapp update` to force the new image + `az containerapp job start` + a manual smoke — half
of which is a silent no-op unless the operator knows the `:latest`/`Single` gotcha. ADR-0008
deliberately deferred this ("the first deploy is human-driven; no AI-generated IaC operated without
understanding"), which was correct while the provider was undecided. The provider is now chosen and
the infra exists in `iac/`, so the cost to buy down is **operational, not exploratory**.

One constraint shapes the design: prod has **zero users and doubles as the QA/staging environment**
(no separate staging — YAGNI for a pre-release portfolio app). An unconditional auto-deploy on every
merge would redeploy **under QA's feet mid-test**. Deployment must therefore be *continuous in
readiness* but *gated on a human "release now" decision*.

## Decision

### 1. The deployment unit is an immutable per-commit image; the pipeline owns the rolling tag

`cd.yml` builds + pushes `:sha-<short>` (plus `:latest` on main, semver on `v*`). The deploy job
rolls ACA to that immutable `:sha-<short>` via `az containerapp update`. `:latest` stays a
human-pull convenience only — never the thing that defines what runs.

### 2. Terraform owns infra shape; the pipeline owns the running image

Each ACA app + the migrator job carries `lifecycle { ignore_changes = [...container[0].image] }`
(and a `var.image_tag` bootstrap default). So `terraform apply` never reverts a deployed revision to
the bootstrap tag, and `az containerapp update` never fights Terraform. Clean split: **TF =
env/scaling/ingress/secrets/storage; pipeline = which version runs.**

### 3. Every merge to main is an approval-gated deploy candidate

A push to main builds artifacts unconditionally, then `deploy-prod` **pauses at the `production`
GitHub Environment** (required reviewer). Approving releases it. `workflow_dispatch` deploys the tip
of a ref (or an explicit `image_tag`) on demand, behind the same gate. A `v*` tag publishes
artifacts only — it does not deploy. This is the "approve a release manually in GitHub" control that
protects in-flight QA.

### 4. Migrations are an enforced pre-rollout gate (R6 — finally live)

`deploy-prod` pins the migrator job to the same `:sha`, starts it, and polls its execution to
`Succeeded` **before rolling any API**. A non-`Succeeded` execution fails the job, so the apps are
never rolled onto an un-migrated schema — no half-migrated serving. Forward-only (ADR-0008 §6;
snapshot before applying in a real environment).

### 5. Post-deploy smoke is wired into the deploy

`smoke.yml` becomes `workflow_call`-able; `deploy-prod` invokes it after a cold-start-tolerant
readiness wait. A failed smoke marks the release failed and visible (the documented "call the smoke
job at the end of the deploy workflow").

### 6. Infra is also CI-managed, behind the same gate

`infra.yml` runs `fmt -check` + `validate` + `plan` on PRs touching `iac/**` (preview in the run
summary — the infra PR gate), and a **gated `terraform apply`** on main / dispatch. This closes the
"manual local terraform" gap and makes every infra change reviewable.

### 7. Azure auth is OIDC federation — no stored secret

Both workflows authenticate via `azure/login` OIDC (`id-token: write`) against an Entra app with
**two federated credentials** (subjects `…:environment:production` and `…:pull_request`); the
`azurerm` provider + state backend reuse that Azure CLI session (`use_cli`). RBAC: **Contributor on
the prod RG + the tfstate RG**. No long-lived client secret in GitHub.

### 8. Secrets: app secrets stay in ACA; TF inputs move to GitHub

The app-rollout path needs **zero app secrets** — they already live as ACA `secret`s, set by
Terraform. `infra.yml` needs the TF inputs, so the `iac/terraform.tfvars` values move to GitHub repo
**Secrets** (sensitive) / **Variables** (plain) as `TF_VAR_*`. Nothing secret is ever committed
(`*.tfvars` stays gitignored).

## Consequences

### Positive

- A `merge → approve → live` path a recruiter recognizes: immutable artifacts, an enforced migration
  gate, human release control, post-deploy smoke, IaC plan/apply in CI, and keyless cloud auth.
- The `:latest`/`Single` silent-no-op trap is gone; **what's approved is exactly what runs** (by SHA).
- QA on prod is never interrupted involuntarily — releases wait for a human.
- `min_replicas` lifted `0 → 1` (R8) so a release isn't a cold start; smoke stays cold-start tolerant.

### Negative / Accepted Trade-offs

- **Single environment** (prod = staging): a bad release lands on the only environment. Mitigated by
  the migration gate + smoke + zero users + free breaking changes (ADR-0002). A real staging is a
  future ADR if users arrive.
- **Single-revision rollout** (no blue/green traffic split). Fine at this scale; revisit if
  zero-downtime becomes a requirement.
- **TF secrets now also live in GitHub** (besides the laptop). Accepted: scoped repo secrets,
  masked, never in git; the alternative (manual local apply forever) is worse.
- **Forward-only migrations** (no automated rollback) — unchanged from ADR-0008 §6.

## Alternatives Considered

### A. Pipeline rolls by SHA; TF infra-only; gated; OIDC; IaC in CI (this ADR)

Chosen. Targeted, fast, keyless rollout; clean infra/version ownership split; the gate gives release
control without extra ceremony.

### B. `terraform apply` in CI as the *rollout* mechanism (image tag as a TF var)

Rejected as the rollout path: couples every code deploy to a full plan/apply with all app secrets in
the apply — slower and riskier than a targeted `az containerapp update`. TF is still CI-managed for
**infra** (§6); it is just not the per-commit rollout lever.

### C. Auto-deploy on merge with no gate

Rejected: redeploys under QA mid-test — the exact failure mode the maintainer called out.

### D. Tag-only releases (deploy only on `v*`)

Rejected: more ceremony, fewer prod candidates; the approval gate already provides release control
without tags. `v*` still builds immutable artifacts.

### E. Service-principal client secret for Azure auth

Rejected: a long-lived secret in GitHub versus keyless OIDC federation.

## Implementation Notes

- **New:** `.github/workflows/infra.yml`; this ADR.
- **Changed (CI/CD):** `cd.yml` (+`deploy-prod` gated job, +`smoke` reusable-call job);
  `smoke.yml` (+`workflow_call`).
- **Changed (infra):** `iac/vars.tf` (`image_tag`); `iac/azure-container-apps.tf` +
  `iac/migrator-job.tf` (`ignore_changes` on the image ×4, `min_replicas` `0 → 1`).
- **Changed (docs):** `runbook.md` + `azure-supabase-bring-up-plan.md` — domain drift
  `koniec.dev → lotro-translator.pl` and the automated-CD description.
- **Operator one-time setup** (Azure + GitHub; enumerated in the runbook + issue #217): Entra app +
  two federated credentials, RBAC, repo Secrets/Variables, the `production` environment with a
  required reviewer.
- **Unchanged:** the four Dockerfiles, the CI gate (`pr-verify`/`ci`), `gitleaks`/`e2e`/`mutation`,
  and the patcher (ADR-0002).

## References

- ADR-0008 — cloud-agnostic deployment & environment strategy (superseded here re: provider / IaC /
  human-driven first deploy)
- `docs/deployment/azure-supabase-bring-up-plan.md`, `runbook.md`, `target-requirements.md`
- `.github/workflows/cd.yml`, `infra.yml`, `smoke.yml`; `iac/`
- issue #217
