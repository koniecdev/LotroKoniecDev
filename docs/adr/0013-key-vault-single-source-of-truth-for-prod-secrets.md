# ADR-0013: Azure Key Vault is the single source of truth for prod secrets (managed-identity references, no plaintext on disk / in state / in CI)

**Status:** Accepted
**Date:** 2026-06-29
**Decision-makers:** Solo maintainer
**Related:** ADR-0012 (continuous deployment — **supersedes its §8** "app secrets stay in ACA; TF
inputs move to GitHub Secrets"), ADR-0008 (cloud-agnostic deployment — **refines §R3** of
`docs/deployment/target-requirements.md`, which already named "Key Vault references / managed
identity" as the Azure-native secret-injection option), ADR-0005 (DP keyring persistence),
`iac/`, `scripts/seed-keyvault.{sh,ps1}`, `.github/workflows/infra.yml`, issue #222.

## Context

After ADR-0012 the 8 production app secrets (`connection-string-{translation,auth}`, the three
`openiddict-*`, `smtp-username/password`, `admin-password`) rested as **plaintext in three places**:

1. **Local disk** — `iac/terraform.tfvars` (gitignored, but plaintext on the operator's machine).
2. **Terraform state** — the values flowed through `var.*` into inline ACA `secret { value = … }`
   blocks, so every `terraform apply` wrote them into the remote `azurerm` state.
3. **GitHub repo Secrets** — the `TF_VAR_*` inputs `infra.yml` fed to the gated `terraform apply`.

Three copies of every secret means three blast radii, three rotation surfaces, and a standing
invitation for a stray secret to land in a log or a state diff. ADR-0012 §8 accepted this as a
pragmatic step; `target-requirements.md` §R3 had already flagged the better Azure-native target:
**Key Vault references + a managed identity**. The app layer never needed the change — it reads its
secrets from environment variables (12-factor; ADR-0008 §3) and does not care where they come from.
Only the **IaC wiring** decides their resting place.

## Decision

### 1. Key Vault is the single source of truth; nothing else stores a plaintext value

The 8 secrets live **only** in an Azure Key Vault (`lotrotms-kv-prod`, RBAC-authorization mode). They
do not rest on local disk, in the Terraform state, or in GitHub Secrets.

### 2. ACA reads secrets at runtime through a user-assigned managed identity

A single user-assigned identity (`lotrotms-aca-prod`) is assigned to the three apps + the migrator
job and granted **`Key Vault Secrets User`** on the Vault. Each ACA `secret` block references the
Vault instead of carrying a value:

```hcl
secret {
  name                = "connection-string-auth"
  key_vault_secret_id = local.kv_secret_id["connection-string-auth"]  # versionless URI
  identity            = data.azurerm_user_assigned_identity.aca.id
}
```

Versionless URIs mean a rotation (a new secret version) is picked up on the **next** revision with no
Terraform change.

### 3. Terraform wires references; it never receives a plaintext value

Terraform builds the secret URI from `data.azurerm_key_vault.secrets.vault_uri` + the secret name —
it never reads the secret **value** (no `azurerm_key_vault_secret` data source, whose `.value` would
land in state). So the state stays free of plaintext, and the 8 sensitive `variable`s + their
`TF_VAR_*` inputs are **deleted** from `iac/vars.tf` and `infra.yml`.

### 4. The Vault, secrets, identity and role assignment are foundational (seeded out-of-band)

Like the tfstate backend storage account, these are bootstrapped **outside** Terraform by an
idempotent script — `scripts/seed-keyvault.{sh,ps1}` — that reads the values from `SEED_*` env vars
and ensures the Vault, the identity, the `Key Vault Secrets User` grant, and the 8 secrets. Two
payoffs: (a) no chicken-and-egg (the Vault + secrets exist before any ACA references them, so a
single `apply` converges), and (b) Terraform — and therefore the CI deploy principal — never needs
to **create a role assignment**, so CI keeps needing only **Contributor** (ADR-0012 §7), not
RBAC-admin. The script is also the rotation tool: re-run it with new `SEED_*` values.

### 5. App code is unchanged; the neutrality contract holds

The apps still read the same env vars; ACA resolves `secret_name` → Key Vault transparently. The
Key Vault dependency lives entirely in the Azure IaC, never in a slice — consistent with ADR-0008's
neutrality contract (porting to another provider swaps the IaC secret-injection wiring, not a line
of application code).

## Consequences

### Positive

- **One copy of each secret, in a purpose-built store.** No plaintext on disk, in the Terraform
  state, or in GitHub Secrets. The 8 secret repo Secrets (`CONNECTION_STRING_TRANSLATION/AUTH`, the
  three `OPENIDDICT_*`, `SMTP_USERNAME/PASSWORD` and `ADMIN_PASSWORD`) are now unused and can be deleted.
- **Rotation without a deploy or a code change** — `seed-keyvault` sets a new version; the next
  revision picks it up via the versionless URI.
- **Least-privilege CI is preserved** — the role assignment is foundational, so CI stays Contributor.
- **Audited, access-controlled, soft-delete-protected** secret access (Key Vault data-plane logs).

### Negative / Accepted Trade-offs

- **An out-of-band bring-up/rotation step** (`seed-keyvault`) instead of a single `terraform apply`.
  Accepted: it is what keeps plaintext out of the state, and it mirrors the existing
  bootstrapped-tfstate precedent.
- **Azure-specific wiring** (Key Vault references). Accepted: the IaC is already 100% `azurerm`; the
  *app* stays provider-neutral (§5), so this does not deepen lock-in where it would hurt.
- **The seed script holds the values transiently** in process env while setting them. Accepted: it
  is a local operator action against the operator's own session; nothing is written to disk.
- **Purge protection is left off** for now (soft-delete only), trading a hard-delete safety net for
  pre-release teardown flexibility. Flip `--enable-purge-protection` when users arrive.

## Alternatives Considered

### A. Key Vault as source of truth; ACA reads via managed-identity references; TF wires only (this ADR)

Chosen. Removes plaintext from all three resting places at once; rotation is decoupled from deploys;
CI stays least-privilege.

### B. Terraform seeds the Vault (`azurerm_key_vault_secret { value = var.x }`)

Rejected. One `apply` does everything, but the values still flow through `var.*` → still need a
plaintext source (tfvars / CI) **and still land in the Terraform state**. It removes the inline-ACA
copy but not the two that matter most — a half-measure against the actual goal.

### C. Keep inline ACA secrets; just drop `terraform.tfvars` and inject every value via `TF_VAR_*` env

Rejected. No Key Vault at all (the explicit ask), secrets still written into state on every apply,
and "no secrets on disk" reduced to operator discipline that regresses the first time someone writes
a local tfvars to debug.

### D. Access policies instead of RBAC on the Vault

Rejected. Azure's current guidance is RBAC authorization (unified, auditable, least-privilege via
`Key Vault Secrets User`/`Officer`); access policies are legacy.

## Implementation Notes

- **New:** `iac/keyvault.tf` (Vault + identity data sources, versionless secret-URI locals);
  `scripts/seed-keyvault.sh` + `.ps1` twin; this ADR.
- **Changed (infra):** `iac/azure-container-apps.tf` + `iac/migrator-job.tf` (`identity {}` block +
  `secret { key_vault_secret_id, identity }` — auth-api ×7, tms-api ×1, migrator ×2; frontend has no
  secrets); `iac/vars.tf` (drop the 8 sensitive variables; add non-secret `key_vault_name` +
  `aca_identity_name`).
- **Changed (CI):** `.github/workflows/infra.yml` (drop the 8 secret `TF_VAR_*` envs).
- **Deleted:** `iac/terraform.tfvars` (was untracked; the values now live only in the Vault).
- **Changed (docs):** `docs/deployment/runbook.md` (Key Vault seed step + rotation + the secret
  matrix).
- **Operator one-time setup** (enumerated in the runbook): `az login` as an Owner / User Access
  Administrator, export the 8 `SEED_*`, run `scripts/seed-keyvault.sh`. The role assignment needs a
  privileged principal **once** (the script, not CI). Thereafter rotation is the same script.
- **Unchanged:** the four Dockerfiles, the app code, the patcher (ADR-0002), `cd.yml`'s rollout, and
  the migration gate.

## References

- ADR-0012 — continuous deployment pipeline (its §8 GitHub-Secrets posture is superseded here)
- ADR-0008 + `docs/deployment/target-requirements.md` §R3 — the Key-Vault-references target
- `iac/keyvault.tf`, `iac/azure-container-apps.tf`, `iac/migrator-job.tf`, `iac/vars.tf`
- `scripts/seed-keyvault.sh`, `scripts/seed-keyvault.ps1`
- `.github/workflows/infra.yml`; issue #222
