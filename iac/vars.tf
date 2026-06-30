variable "env_id" {
  type        = string
  description = "The environment id"
  default     = "prod"
}

variable "public_base_domain" {
  type        = string
  description = "Public apex domain for this environment. Every OIDC issuer/redirect/CORS/base URL derives from it via iac/locals.tf (audit 0001 / H2). The default keeps prod byte-identical; staging sets \"staging.lotro-translator.pl\"."
  default     = "lotro-translator.pl"
}

variable "src_key" {
  type        = string
  description = "The infrastructure source"
  default     = "terraform"
}

variable "image_tag" {
  type        = string
  description = "Bootstrap image tag for the four GHCR images. Only consumed when a Container App / Job is first created; afterwards the rolling tag is owned solely by the CD pipeline (az containerapp update by commit SHA) and Terraform ignores image drift via lifecycle.ignore_changes."
  default     = "latest"
}

variable "subscription_id" {
  type        = string
  description = "The Azure subscription id"
}

variable "key_vault_name" {
  type        = string
  description = "Name of the Key Vault that holds the production app secrets (ADR-0013). Seeded out-of-band by scripts/seed-keyvault.{sh,ps1}; Terraform only data-references it."
  default     = "lotrotms-kv-prod"
}

variable "aca_identity_name" {
  type        = string
  description = "Name of the user-assigned managed identity the ACA apps + migrator job assume to read secrets from the Key Vault. Seeded out-of-band by scripts/seed-keyvault.{sh,ps1}; Terraform only data-references it."
  default     = "lotrotms-aca-prod"
}

variable "smtp_sender_email" {
  type        = string
  description = "Verified sender email address"
}

variable "admin_username" {
  type        = string
  description = "Seeded admin username"
}

variable "admin_email" {
  type        = string
  description = "Seeded admin email"
}

variable "location" {
  type        = string
  description = "Azure region for this environment's compute resources (ACA environment, apps, migrator job, Log Analytics, App Insights, the Data Protection storage account, alerts). Defaults to polandcentral so prod is byte-identical; staging overrides to germanywestcentral because the student subscription allows only ONE Container App Environment per region and prod already holds Poland Central (ADR-0018). The resource group, Key Vault and identity stay in polandcentral."
  default     = "polandcentral"
}
