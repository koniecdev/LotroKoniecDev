# Staging environment overrides (audit 0001 / H2+H5, ADR-0017). Apply with:
#   terraform init -reconfigure -backend-config=backend-config/staging.hcl
#   terraform apply -var-file=env/staging.tfvars
#
# Prerequisites (out-of-band, ADR-0017 §7): a SEPARATE lotrotms-kv-staging Key Vault with freshly
# generated secrets (never the prod vault — audit §C5), a lotrotms-aca-staging identity, and a Neon
# `staging` branch (audit §H13). The required secret-free inputs (subscription_id, smtp_sender_email,
# admin_username, admin_email) still arrive as TF_VAR_*, not from this file.
env_id             = "staging"
public_base_domain = "staging.lotro-translator.pl"
key_vault_name     = "lotrotms-kv-staging"
aca_identity_name  = "lotrotms-aca-staging"
