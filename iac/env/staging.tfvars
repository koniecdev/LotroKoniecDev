# Staging environment overrides (audit 0001 / H2+H5, ADR-0017). Apply with:
#   terraform init -reconfigure -backend-config=backend-config/staging.hcl
#   terraform apply -var-file=env/staging.tfvars
#
# Prerequisites (out-of-band, ADR-0017 §7): a SEPARATE lotrotms-kv-staging Key Vault with freshly
# generated secrets (never the prod vault — audit §C5), a lotrotms-aca-staging identity, and a Neon
# `staging` branch (audit §H13). The required secret-free inputs (subscription_id, smtp_sender_email,
# admin_username, admin_email) still arrive as TF_VAR_*, not from this file.
# See docs/deployment/runbook.md §"Staging bring-up" for the ordered first-time bring-up sequence.
env_id             = "staging"
public_base_domain = "staging.lotro-translator.pl"
key_vault_name     = "lotrotms-kv-staging"
aca_identity_name  = "lotrotms-aca-staging"

# Shared-environment mode (M6-22): the subscription allows only ONE Container App Environment total,
# held by prod, so staging deploys its apps INTO prod's managed environment instead of creating its
# own. These point at prod's environment (name + resource group); see iac/locals.tf (create_env).
aca_environment_name           = "lotrotmsenvprod"
aca_environment_resource_group = "rg-lotrotms-prod-polc-001"
