# Azure Key Vault is the single source of truth for the production app secrets (ADR-0013). The Vault,
# its secrets, the user-assigned identity, and the identity's "Key Vault Secrets User" role assignment
# are foundational infrastructure — seeded out-of-band by scripts/seed-keyvault.{sh,ps1} (like the
# tfstate backend storage account), so the CI deploy principal never needs RBAC-admin and no plaintext
# secret value ever enters this configuration or the Terraform state. Here Terraform only data-reads
# them and builds the versionless secret URIs that the ACA apps + migrator job reference via the
# identity.

data "azurerm_key_vault" "secrets" {
  name                = var.key_vault_name
  resource_group_name = azurerm_resource_group.main.name
}

data "azurerm_user_assigned_identity" "aca" {
  name                = var.aca_identity_name
  resource_group_name = azurerm_resource_group.main.name
}

locals {
  # Versionless Key Vault secret URIs. ACA resolves the current version on each revision, so a
  # rotation (a new secret version set by the seed script) is picked up on the next deployment
  # without touching Terraform. Names must match the secrets seeded by scripts/seed-keyvault.{sh,ps1}.
  kv_secret_id = {
    for name in [
      "connection-string-translation",
      "connection-string-auth",
      "openiddict-signing-key",
      "openiddict-encryption-key",
      "openiddict-api-client-secret",
      "smtp-username",
      "smtp-password",
      "admin-password",
    ] : name => "${data.azurerm_key_vault.secrets.vault_uri}secrets/${name}"
  }
}
