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

variable "aca_environment_name" {
  type        = string
  description = "Name of an EXISTING managed Container Apps environment to deploy this env's apps into (shared-environment mode, M6-22). Empty = create our own. Set because the Azure subscription allows only ONE Container App Environment total, held by prod — staging runs its apps inside the prod environment."
  default     = ""
}

variable "aca_environment_resource_group" {
  type        = string
  description = "Resource group of the existing managed environment named by var.aca_environment_name. Empty = our own resource group."
  default     = ""
}

variable "app_min_replicas" {
  type        = number
  description = "min_replicas for the three container apps. Prod keeps the default 1 (ADR-0012 R8 — a warm replica per revision, no cold starts). Staging sets 0 (ADR-0020 FinOps): scale-to-zero between rollouts/QA — the health-gated rollout stays valid because deploy.yml's readiness polls wake the candidates and warm the public auth origin before smoke."
  default     = 1

  validation {
    condition     = var.app_min_replicas >= 0 && var.app_min_replicas <= 1
    error_message = "app_min_replicas must be 0 or 1 (max_replicas is fixed at 1)."
  }
}

variable "tms_api_cpu" {
  type        = number
  description = "vCPU for the tms-api container. Every environment keeps the default 0.25: the exported.txt import OOM (incident 2026-07-02) was fixed by the #290 streaming two-pass import (spec 0006), and the temporary staging 2-vCPU bridge (#300) was removed with #290's DoD. Kept as a knob for future per-env sizing."
  default     = 0.25
}

variable "tms_api_memory" {
  type        = string
  description = "Memory for the tms-api container, paired with tms_api_cpu (ACA Consumption allows only fixed pairs: 0.25/0.5Gi, 0.5/1Gi, 1/2Gi, 1.5/3Gi, 2/4Gi). Every environment keeps the default 0.5Gi — see tms_api_cpu."
  default     = "0.5Gi"
}
