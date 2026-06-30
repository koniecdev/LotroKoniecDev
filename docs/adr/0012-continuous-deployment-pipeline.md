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

**Amendment (2026-06-30, audit 0001 H9 + H1 — supply-chain hardening of the deployment unit).** The
immutable image is now **scanned, signed and attested** before it can be deployed, and that chain is
**verified at deploy**. In `build-and-push`, each image is scanned by Trivy (a fixable HIGH/CRITICAL
fails the build *before* publish — the H1 image-scanning leg, complementing the already-shipped
NuGetAudit + CodeQL + Dependabot dependency/SAST legs); BuildKit attaches an SLSA provenance + SPDX
SBOM attestation (`provenance: true`, `sbom: true`); the image is signed keyless with **cosign**
(Fulcio/Rekor — no stored key, the job's GitHub OIDC identity); and a signed first-party
**build-provenance attestation** (`actions/attest-build-provenance`) is pushed to GHCR as an OCI
referrer. `deploy-prod` then runs a **fail-closed verify gate** (`gh attestation verify`) over all
four images *before* the migration gate (§4) or any traffic move, so only an image our own CD built,
scanned and attested can reach prod. The attestations are also standalone artifacts (SBOM for CVE
triage, provenance for "which commit/builder produced this"). One consequence: enabling
`provenance`/`sbom` turns each push into an OCI **image index** (the `linux/amd64` runtime manifest +
two `unknown/unknown` attestation manifests); ACA/containerd platform-match the runtime manifest, so
the rollout pull is unchanged.

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

**Amendment (2026-06-29, audit 0001 C2 — CI success is a hard pre-condition of deploy):** the CD
trigger changed from `on: push: main` to `on: workflow_run` of the **CI** workflow (`types:
[completed]`, `branches: [main]`). A new `gate` job runs first and proceeds **only** when CI
concluded `success` on main; a red or cancelled CI skips the whole deploy (the gate records why in
the run summary — which the approver sees before releasing, satisfying the audit's "show CI status
in the approval decision"). The gate also pins the exact commit CI tested
(`github.event.workflow_run.head_sha`); the image build (`checkout` ref + a per-tested-commit build
concurrency key), its immutable `sha-<short>` tag, the ACA rollout, and even the mutable `latest` tag
(moved only when the tested commit is still main's tip, `head_sha == github.sha`) are all keyed to
that commit — so a commit that races ahead of CI can neither be deployed nor make `latest` regress to
an older commit, and no green commit loses its build to another commit's run. Consequently the original §3 phrasing "a push to main builds
artifacts unconditionally" no longer holds: a build now happens **only behind a green CI**. The full
suite (Release build + unit + integration) is thus a gate *before* the deploy candidate exists, not
a parallel race. `workflow_dispatch` and `v*`-tag pushes are deliberately **not** CI-gated (manual /
publish-only paths) — they pass through the gate ungated — but a dispatch rollout still requires the
same human approval, and a tag still only publishes artifacts. Because CI carries `paths-ignore` for
docs, a docs-only push runs no CI and therefore triggers no CD (incidentally closing audit 0001 M2).

### 4. Migrations are an enforced pre-rollout gate (R6 — finally live)

`deploy-prod` pins the migrator job to the same `:sha`, starts it, and polls its execution to
`Succeeded` **before rolling any API**. A non-`Succeeded` execution fails the job, so the apps are
never rolled onto an un-migrated schema — no half-migrated serving. Forward-only (ADR-0008 §6;
snapshot before applying in a real environment).

### 5. Post-deploy smoke is wired into the deploy

`smoke.yml` becomes `workflow_call`-able; `deploy-prod` invokes it after a cold-start-tolerant
readiness wait. A failed smoke marks the release failed and visible (the documented "call the smoke
job at the end of the deploy workflow").

**Amendment (2026-06-30, audit 0001 H7 — health-gated rollout, supersedes the single-revision
rollout below).** The rollout is no longer "`az containerapp update` → 100% of traffic onto the new
revision immediately, smoke afterwards". The three web apps run in **multiple-revision mode**
(`iac/azure-container-apps.tf`: `revision_mode = "Multiple"`, and `lifecycle.ignore_changes` now also
covers `ingress[0].traffic_weight` — Terraform seeds only the initial `latest_revision = true` weight;
the pipeline owns per-deploy traffic, mirroring the §2 image split). Per deploy, `deploy-prod` pins
100% of traffic to the current revision, then creates each app's new revision with `--revision-suffix`
so it lands at **0% traffic**, labels it `cd-candidate`, and waits for the candidate's readiness —
**including the frontend** (previously only auth + tms were polled; the frontend, a Static-SSR app
with no `/health`, is checked for a 2xx/3xx on `/`). The `smoke` job then exercises the candidate
through its private `…---cd-candidate.<env-domain>` label FQDN (valid wildcard cert; the token
round-trip works cross-revision because the OpenIddict signing key is shared via Key Vault and the
issuer is pinned). Only a green candidate smoke runs `promote`, which shifts 100% of traffic onto the
candidate (`ingress traffic set --revision-weight <prev>=0 <candidate>=100`); `smoke-prod` then
re-checks the real production origins (custom domain + cert + routing). On **any** failure the
`rollback` job restores 100% of traffic to the previous revision and deactivates the candidate — safe
in every phase (pre-promotion: traffic never moved; post-promotion: traffic is forced back). The net
effect is the audit's "deploy at 0% → smoke → 100%": **traffic never lands on an unverified revision**,
and there is no auto-rollback gap. `min_replicas` stays `0` (the audit's M3 reconciliation is left
out of scope here): the 0%-traffic candidate is woken from scale-to-zero by the readiness/smoke
requests to its label FQDN (Container Apps' documented direct-revision-access path), so no replica is
paid for between deploys. One accepted consequence of decoupling the traffic shift from the image
roll: during the smoke window the **previous** revision briefly serves on the freshly-migrated schema
(the migration gate still runs first), which the project's forward-only / expand-contract discipline
already assumes (ADR-0008 §6; audit C4).

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
- ~~**Single-revision rollout** (no blue/green traffic split). Fine at this scale; revisit if
  zero-downtime becomes a requirement.~~ **Superseded (2026-06-30, audit 0001 H7):** the rollout is
  now multiple-revision and health-gated — see the §5 amendment. Traffic shifts only onto a
  candidate that passed smoke, with automatic rollback on failure.
- **TF secrets now also live in GitHub** (besides the laptop). Accepted: scoped repo secrets,
  masked, never in git; the alternative (manual local apply forever) is worse.
- **Forward-only migrations** (no automated rollback) — unchanged from ADR-0008 §6.
- **Release availability now depends on Sigstore** (Fulcio/Rekor) + the GHCR attestation store (audit
  0001 H9): signing/attestation run in the build's critical path (not `continue-on-error`) and the
  deploy verify gate fails closed, so a Sigstore/GHCR outage blocks publishing or deploying until it
  clears (re-run later). Accepted for a pre-release — a signed-or-nothing supply chain is the point,
  and the deploy path is already human-gated. A `workflow_dispatch` of an **old** tag built before
  this change carries no attestation and fails the verify gate by design: rebuild from the commit.

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
- **Changed (CI/CD — audit 0001 H9 + H1):** `cd.yml` — `build-and-push` gains a Trivy image-scan gate,
  `provenance`/`sbom` attestations, cosign keyless signing and `actions/attest-build-provenance` (job
  perms `id-token`/`attestations`); `deploy-prod` gains a `gh attestation verify` fail-closed gate (job
  perm `packages: read`). The three new actions are SHA-pinned (Dependabot `github-actions` keeps them
  fresh); no IaC change.
- **Changed (infra):** `iac/vars.tf` (`image_tag`); `iac/azure-container-apps.tf` +
  `iac/migrator-job.tf` (`ignore_changes` on the image ×4, `min_replicas` `0 → 1`).
- **Changed (docs):** `runbook.md` + `azure-supabase-bring-up-plan.md` — domain drift
  `koniec.dev → lotro-translator.pl` and the automated-CD description.
- **Operator one-time setup** (Azure + GitHub; enumerated in the runbook + issue #217): Entra app +
  two federated credentials, RBAC, repo Secrets/Variables, the `production` environment with a
  required reviewer, and the `CD_ENABLED` activation switch (a repo Variable; the Azure-touching jobs
  are skipped until it is `true`, so merging this change is inert until the operator activates).
- **Unchanged:** the four Dockerfiles, the CI gate (`pr-verify`/`ci`), `gitleaks`/`e2e`/`mutation`,
  and the patcher (ADR-0002).

## References

- ADR-0008 — cloud-agnostic deployment & environment strategy (superseded here re: provider / IaC /
  human-driven first deploy)
- `docs/deployment/azure-supabase-bring-up-plan.md`, `runbook.md`, `target-requirements.md`
- `.github/workflows/cd.yml`, `infra.yml`, `smoke.yml`; `iac/`
- issue #217
