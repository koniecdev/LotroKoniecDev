terraform {
  # Pin the Terraform core version band (audit #0001 / M15) so a stray CLI version can't silently
  # apply against this state. Matches the 1.15.7 pinned in the CI workflow (.github/workflows/infra.yml).
  required_version = "~> 1.15"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "4.7.0"
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