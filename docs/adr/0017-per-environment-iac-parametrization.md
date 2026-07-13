# ADR-0017: Per-environment IaC parametrization (Rank 2 — parametrized flat root + per-env state key)

**Status:** **Obsolete by platform** (ADR-0034, 2026-07-13 / #492 — there is no IaC left to parametrize: the Terraform root `iac/` is removed and an environment is now one Hetzner box + its `/opt/lotro/.env`. The per-environment *split* survives in a different instrument — GitHub environments carrying `HETZNER_HOST` + that box's secrets, ADR-0018.) Previously: Accepted
**Date:** 2026-06-30
**Decision-makers:** Solo maintainer
**Related:** IaC (`iac/`); audit 0001 §H2 (RG + URL literals), §H5 (flat root + monolithic state
key), §H11 (`prevent_destroy`), §C5 (staging-blocker), §6 (approach ranking + staging blueprint),
§M4 (ADR conflict); ADR-0008 (env strategy — staging + production), ADR-0012 (CD — "prod is
de-facto staging" interim), ADR-0013 (Key Vault per-env secrets), ADR-0016 (env_id-parametrized
telemetry — the precedent this generalizes)

## Context

Audit 0001 (§6, §H2/H5/H11) found the Terraform is **single-environment** despite ADR-0008 §1
committing to "two environments — staging + production". The gaps, verified against the tree:

- **RG name hardcoded.** `resource-group.tf:1,3` — both the resource symbol
  `azurerm_resource_group.rg-lotrotms-prod-polc-001` and its `name` are the literal
  `"rg-lotrotms-prod-polc-001"`; the symbol is referenced **28× across 9 `.tf` files**.
- **10 URL literals.** `azure-container-apps.tf` bakes `lotro-translator.pl` in 10 places
  (`:125/141/145/149/321/325/333/438/442/450`): OpenIddict `Issuer`, web-client redirect /
  post-logout, both APIs' CORS, tms `Auth__Issuer`/`Auth__Authority`, frontend
  `AuthSystem__Authority`/`__BaseUrl`, `TranslationSystem__BaseUrl`. Two carry a **trailing slash**
  (`:442`, `:450`) — byte-identity matters (the OIDC `iss`, audit G15).
- **Monolithic state key.** `setup.tf:25` `key = "prod.terraform.tfstate"` — a backend block cannot
  take a variable, so a second env cannot get its own state file without intervention.
- **No destroy guard** on the two stateful, TF-owned resources: the Data Protection keyring
  `azurerm_storage_account.keys` (`storage.tf`) and `azurerm_log_analytics_workspace.law`
  (`azure-law.tf`).

The floor is already high: storage/LAW/App-Insights/app names are **already** `${var.env_id}`-derived
(`lotrotmskeys${var.env_id}`, `lotrotmslaw${var.env_id}`, `lotrotms-*-${var.env_id}`),
`monitoring.tf:53` even comments the `prod -> "lotrotmsprod" / staging -> "lotrotmsstag"` shape, and
the apps are **env-name-agnostic** (audit G13 — guards key off `IsDevelopment()/IsTesting()`, not
`=="Production"`). So the residual gap is narrow: RG name, the 10 URLs, the state key, destroy guards.

Two hard constraints shape the change:

- **Prod must stay byte-for-byte untouched.** The issuer/redirect/CORS strings feed the token `iss`
  and OIDC redirects; the RG name and state blob must not change. The only acceptable prod `plan`
  delta is a **state-address move** plus state-only `prevent_destroy` metas — zero infrastructure
  change.
- **`prevent_destroy` and the RG rename interact** (audit §6 "Sekwencja"): the symbol rename must go
  through `moved {}` / `state mv`, **never** destroy + recreate.

This also settles audit §M4: ADR-0008 §1 says "staging + production"; ADR-0012 took the interim
"prod is de-facto staging (YAGNI)". This ADR resolves the contradiction in favor of 0008's two-env
model and makes staging genuinely instantiable.

## Decision

### 1. Rank 2, not Rank 1 — parametrize the flat root; do not extract a module yet

A single root module stays; each environment is selected by a **var-file** plus a **backend state
key**. The audit's Rank 1 (`modules/environment/`, root = `module "prod"`/`module "staging"`) is
**deferred**: at two environments of identical sizing a module abstraction is YAGNI
(`CLAUDE.md` — right-size, no abstraction without a present need), and it would force a `moved {}` for
**every** resource (all addresses become `module.X.*`) — far larger surgery on the live prod state —
for a DRY win that only pays off at 3+ envs or diverging shapes. Rank 2 is a **strict subset** of
Rank 1, so wrapping the parametrized root in `modules/environment/` later is a clean follow-up at no
rework cost. **Revisit trigger:** a third environment, or env shapes that genuinely diverge.

### 2. One variable drives every public origin

New `var.public_base_domain` (default `"lotro-translator.pl"`) + a new `iac/locals.tf` deriving
`apex_origin = "https://${var.public_base_domain}"`, `auth_origin = "https://auth.…"`,
`tms_origin = "https://tms.…"`, `callback_url = "${local.apex_origin}/callback"`. The 10 literals
become local references; the two trailing-slash values stay `"${local.auth_origin}/"` /
`"${local.tms_origin}/"`. For prod the default reproduces **every former string byte-for-byte**.
Staging sets `public_base_domain = "staging.lotro-translator.pl"`, yielding
`auth.staging…/tms.staging…/staging…` — exactly the origins audit §6 prescribes for staging.

### 3. RG name derives from `env_id`, renamed via `moved {}`

Symbol `rg-lotrotms-prod-polc-001` → `main`; `name = "rg-lotrotms-${var.env_id}-polc-001"` (which
evaluates **identically** for `env_id = "prod"`). A single `moved {}` block migrates the state
address — no destroy/recreate. All 28 references repoint to `azurerm_resource_group.main`.

### 4. Per-environment state key via a partial backend

Drop the inline `key` from the `azurerm` backend (`setup.tf`); supply it at init via
`-backend-config=backend-config/<env>.hcl` (`prod.hcl` → `prod.terraform.tfstate`, `staging.hcl` →
`staging.terraform.tfstate`). Same storage account + container, **separate blobs** — a botched
staging apply can never corrupt prod state (the core §C5/§H5 safety property). `infra.yml`'s two
`terraform init` steps pass `prod.hcl` (with `-reconfigure`, idempotent on the fresh CI runner).

### 5. `prevent_destroy` on the stateful resources

`lifecycle { prevent_destroy = true }` on `azurerm_storage_account.keys` (losing the DP keyring logs
everyone out) and `azurerm_log_analytics_workspace.law` (log history). A meta-argument — state-only,
**no plan diff** — that blocks a stray rename / `-target` slip during multi-env work (audit §H11).

### 6. Prod runs on defaults; only staging carries a tfvars

`vars.tf` defaults are already prod-correct (`env_id=prod`, `public_base_domain=lotro-translator.pl`,
`key_vault_name=lotrotms-kv-prod`, `aca_identity_name=lotrotms-aca-prod`), so prod apply needs **no**
var-file and CI keeps passing only the required secret-free `TF_VAR_*` inputs. Only
`iac/env/staging.tfvars` overrides `env_id`/`public_base_domain`/`key_vault_name`/`aca_identity_name`.
Nothing is named `*.auto.tfvars`, so a prod apply can never load staging values.

### 7. Out-of-band staging prerequisites are named, not Terraform-managed here

Before the first staging apply, seed out-of-band (they are **data sources**, ADR-0013): a separate
`lotrotms-kv-staging` Key Vault with **freshly generated** secrets (never the prod vault — §C5) and a
`lotrotms-aca-staging` identity, plus a Neon `staging` branch (audit §H13). A staging CI job /
promotion model (audit §H10) is a **separate** change; this ADR only makes staging instantiable —
today via a manual `terraform apply -var-file=env/staging.tfvars`.

## Consequences

### Positive

- **Staging is instantiable with zero app/code change** (app already env-name-agnostic, G13) — only a
  var-file, a backend key, and the out-of-band KV/identity/Neon-branch from §7.
- **Prod is provably untouched:** identical issuer/redirect/CORS, identical RG name, same state blob.
  The only prod `plan` delta is the RG state-address move + two `prevent_destroy` metas — no
  infrastructure change.
- **Single source of truth** for the public domain (one var → 10 URLs) and the env name
  (`env_id` → RG/storage/LAW/AI/apps).
- **Real blast-radius isolation:** separate state blobs + destroy guards close §H2/§H5/§H11 and
  defuse the §C5 "staging on prod secrets/state" trap (with the separate KV from ADR-0013 seeding).

### Negative / Accepted Trade-offs

- The flat root is kept; the `modules/` DRY win is deferred (Decision §1) — accepted YAGNI, with a
  documented revisit trigger.
- `terraform init` now needs `-backend-config=backend-config/<env>.hcl`; a local dev with a
  pre-existing `.terraform` needs `-reconfigure` once. Documented in the runbook.
- Per-env required inputs (`subscription_id`, `smtp_sender_email`, `admin_*`) still arrive as
  `TF_VAR_*`, not tfvars, so no env-specific value or secret is committed — staging wiring of those is
  manual until the promotion model (§H10) lands.
- The subdomain scheme is fixed to `auth.<domain>` / `tms.<domain>` / `<domain>`; a differently-shaped
  env would need a small `locals.tf` tweak. Both prod and the §6 staging shape fit it.

## Alternatives Considered

### A. Rank 2 — parametrized flat root + per-env state key + tfvars (this ADR)

Chosen. Strongest state isolation (separate blobs) for the lowest-risk change on the live prod state
(one `moved {}`), fully parametrizes the residual gap, and is a strict subset of Rank 1 so nothing is
foreclosed.

### B. Rank 1 — extract `modules/environment/`, root = `module "prod"` / `module "staging"`

Rejected for now. Cleanest DRY structure and the audit's nominal #1, but requires a `moved {}` for
**every** resource (all addresses move under `module.X.*`) — far larger surgery on live prod state —
for a benefit that pays off only at 3+ envs / diverging shapes; it also trends toward one shared
root+state unless split further, which is **weaker** isolation than Rank 2's separate blobs. Strict
superset of this work, deferrable at no rework cost.

### C. Terraform workspaces (`terraform.workspace`-keyed state)

Rejected. Co-located state is weaker isolation, carries the classic "applied to the wrong workspace"
footgun, and still needs all the same parametrization — lower safety for the same effort (audit §6
Rank 3).

### D. Copy `iac/` to `iac-staging/`

Rejected. Immediate drift; every change made twice by hand (audit §6 Rank 4).

### E. Keep "prod is de-facto staging" (ADR-0012 interim — no second env)

Rejected. The right pre-cloud call, but it is exactly the §C5 risk once a second person (QA) touches
the system — rollout/migration/smoke happen first-and-only on live prod. ADR-0008's two-env intent
supersedes it; this ADR makes the second env real.

## Implementation Notes

- **New:** `iac/locals.tf` (public-origin derivation); `iac/backend-config/prod.hcl` +
  `backend-config/staging.hcl`; `iac/env/staging.tfvars`; this ADR.
- **Changed:** `iac/vars.tf` (+ `public_base_domain`); `iac/resource-group.tf` (symbol → `main`,
  `env_id`-derived `name`, `moved {}`); `iac/azure-container-apps.tf` (10 literals → locals);
  `iac/storage.tf` + `iac/azure-law.tf` (`prevent_destroy`); `iac/setup.tf` (partial backend — drop
  inline `key`); the RG-symbol reference in `azure_container_app_env.tf`, `keyvault.tf`,
  `migrator-job.tf`, `observability.tf`, `monitoring.tf` (→ `.main`); `.github/workflows/infra.yml`
  (both `terraform init` → `-reconfigure -backend-config=backend-config/prod.hcl`);
  `docs/deployment/runbook.md` (per-env init + staging bring-up).
- **Unchanged (deliberately):** the three `Program.cs` (env-name-agnostic, G13); every existing
  `${var.env_id}`-derived name (storage/LAW/AI/app); the KV data-source wiring (ADR-0013) — staging
  just points `key_vault_name` at a separate vault.
- **Supersession (audit §M4):** ADR-0012's "prod is de-facto staging" is now an **interim**,
  superseded by this ADR for the environment-topology question; ADR-0008's two-env model stands.

## References

- `docs/audits/0001-infrastructure-audit.md` — §H2, §H5, §H11, §C5, §M4, and §6 (approach ranking +
  staging blueprint)
- ADR-0008 — cloud-agnostic deployment & environment strategy (staging + production); this ADR
  delivers its two-env intent in IaC
- ADR-0012 — continuous deployment pipeline; its "prod = de-facto staging" interim is superseded here
  for env topology
- ADR-0013 — Key Vault single source of truth for prod secrets (the separate staging vault is the
  seeding boundary, §C5)
- ADR-0016 — env_id-parametrized App Insights ("a future staging environment inherits it for free" —
  the precedent this generalizes to RG name + URLs + state key)
