# Staging Terraform state blob (audit 0001 / H5, ADR-0017) — a separate blob from prod in the same
# backend container, so a botched staging apply can never corrupt prod state. Select this key at init:
#   terraform init -reconfigure -backend-config=backend-config/staging.hcl
key = "staging.terraform.tfstate"
