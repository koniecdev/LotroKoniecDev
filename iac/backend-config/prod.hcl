# Prod Terraform state blob (audit 0001 / H5, ADR-0017). The azurerm backend in setup.tf is partial;
# select this key at init:
#   terraform init -reconfigure -backend-config=backend-config/prod.hcl
key = "prod.terraform.tfstate"
