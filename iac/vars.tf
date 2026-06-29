variable "env_id" {
  type        = string
  description = "The environment id"
  default     = "prod"
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
