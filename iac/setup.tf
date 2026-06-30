terraform {
  # Pin the Terraform core version band (audit #0001 / M15) so a stray CLI version can't silently
  # apply against this state. Matches the 1.15.7 pinned in the CI workflow (.github/workflows/infra.yml).
  required_version = "~> 1.15"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "4.7.0"
    }
    # azapi patches the Container Apps managed OpenTelemetry agent onto the managed environment
    # (iac/observability.tf, ADR-0016): azurerm 4.7.0 has no native block for it
    # (hashicorp/terraform-provider-azurerm#28217). Exact-pinned like azurerm above (audit 0001 G20);
    # multi-platform hashes are tracked in .terraform.lock.hcl.
    azapi = {
      source  = "Azure/azapi"
      version = "2.10.0"
    }
  }

  backend "azurerm" {
    resource_group_name  = "rg-lotrotms-tfstate"
    storage_account_name = "sttflotrotms23957"
    container_name       = "tfstate"
    key                  = "prod.terraform.tfstate"
  }
}

provider "azurerm" {
  features {

  }

  subscription_id = var.subscription_id
}

# Used only by iac/observability.tf to patch the managed environment's OpenTelemetry config. Shares
# the azurerm OIDC Azure CLI session in CI (use_cli default, established by azure/login); the
# subscription mirrors the azurerm provider above.
provider "azapi" {
  subscription_id = var.subscription_id
}