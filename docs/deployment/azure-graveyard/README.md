# Azure graveyard — the retired Azure Container Apps deployment

**Status: DEAD. Nothing in this folder is wired to anything, and nothing here should be run.**
It is a read-only record of how the platform was hosted between M6 and 2026-07-13, kept next to
the docs that talk about it.

The live deployment is a Hetzner VPS + Neon: read [`../runbook.md`](../runbook.md), and see
[ADR-0034](../../adr/0034-hetzner-vps-instead-of-azure-container-apps.md) for why the move
happened (the Azure for Students subscription ran out of credits, both prods went dark, renewal
was refused). Epic #486 executed it; #492 (HETZ-06) took the Terraform out of the build.

## Why keep it at all

Two reasons, both about reading — not about running:

1. **The ADRs still point here.** ADRs 0013, 0016, 0017, 0019, 0020, 0027 and 0029 argue
   line-by-line about `iac/monitoring.tf`, `iac/observability.tf`, `iac/setup.tf` and friends.
   Those decisions are still part of the project's history, and a decision record whose subject
   file cannot be opened is half a record. Their `iac/<file>` references resolve to
   `docs/deployment/azure-graveyard/iac/<file>`.
2. **The topology is reusable thinking.** The alert rules, the SLO probes, the Key-Vault-only
   secret path and the per-environment parametrization were designed against real production pain.
   If this project ever lands on a managed platform again, this is the starting point — not a
   blank page.

## DO NOT run this

- The subscription is **disabled**. `terraform apply` cannot work, and the state blob it wants
  lives in a storage account inside that same dead subscription.
- The state file is **not** here (it never was — it lived in Azure Blob Storage), so this
  configuration has no memory of what it created.
- The secrets are **not** here and never were: Key Vault held the values (ADR-0013), Terraform
  only data-read them into versionless URIs.
- `scripts/seed-keyvault.{sh,ps1}` were restored **without the executable bit**, on purpose.

## What is here

```
iac/
  setup.tf                    Terraform + azurerm/azapi provider pins, partial azurerm backend
  backend-config/{prod,staging}.hcl   the state blob key per environment (ADR-0017)
  env/staging.tfvars          staging's non-secret overrides (ADR-0017 — env_id, domain, names)
  vars.tf, locals.tf          the variables and the derived public origins (issuer/redirect/CORS)
  resource-group.tf           rg-lotrotms-<env>-polc-001
  azure_container_app_env.tf  the shared ACA environment (one per subscription — the staging squeeze)
  azure-law.tf                the shared Log Analytics workspace
  azure-container-apps.tf     the three apps: auth-api, tms-api, frontend
  migrator-job.tf             the EF Core migration job that ran before each rollout
  keyvault.tf                 the Vault + the app identity's read role (values seeded out-of-band)
  storage.tf                  the Data Protection keyring blob container (ADR-0005)
  observability.tf            App Insights + the ACA managed OTel agent (ADR-0016)
  monitoring.tf               Azure Monitor: alert rules, action groups, the SLO web tests (ADR-0019)
scripts/
  seed-keyvault.sh / .ps1     seeded the 8 Key Vault secrets out-of-band (ADR-0013)
```

`iac/.terraform.lock.hcl` was deliberately **not** restored — it is a machine artifact (provider
hashes for an init that will never run again). Git history has it, as it has every file above:
the last commit where this tree was live is the parent of `72e1a4b` (PR #504).

## Where each Azure piece went

| Azure | Now |
|---|---|
| Container Apps (3 apps + revisions) | `compose.hetzner.yaml` on one VPS per environment, `/opt/lotro` |
| Container App job (migrator) | the migrator container, run first by `scripts/hetzner/deploy.sh` |
| ACA ingress + managed certs | Caddy on the box (Let's Encrypt) |
| Key Vault + seeders | a `chmod 600` `/opt/lotro/.env` per box, written by CD from GitHub secrets |
| Terraform + `infra.yml` | nothing — a box is bootstrapped once by `scripts/hetzner/bootstrap.sh` |
| App Insights / LAW / Monitor alerts | container logs + the daily `health-ping.yml` (ADR-0027's probe outlived its platform) |
| Blob-backed DP keyring | a docker volume per app (ADR-0005 still applies) |
| Scale-to-zero + warm window | nothing — the containers run 24/7; only Neon still suspends |
