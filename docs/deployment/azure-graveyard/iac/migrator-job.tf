resource "azurerm_container_app_job" "migrator" {
  name                         = "lotrotms-migrator-${var.env_id}"
  location                     = azurerm_resource_group.main.location
  resource_group_name          = azurerm_resource_group.main.name
  container_app_environment_id = local.app_env_id

  replica_timeout_in_seconds = 1800
  replica_retry_limit        = 1

  manual_trigger_config {
    parallelism              = 1
    replica_completion_count = 1
  }

  identity {
    type         = "UserAssigned"
    identity_ids = [data.azurerm_user_assigned_identity.aca.id]
  }

  # Secrets resolve from Key Vault at runtime through the user-assigned identity (ADR-0013) — no
  # plaintext value enters this configuration or the Terraform state.
  secret {
    name                = "connection-string-translation"
    key_vault_secret_id = local.kv_secret_id["connection-string-translation"]
    identity            = data.azurerm_user_assigned_identity.aca.id
  }

  secret {
    name                = "connection-string-auth"
    key_vault_secret_id = local.kv_secret_id["connection-string-auth"]
    identity            = data.azurerm_user_assigned_identity.aca.id
  }

  template {
    container {
      name   = "migrator"
      image  = "ghcr.io/koniecdev/lotrokoniecdev-migrator:${var.image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name        = "ConnectionStrings__TranslationDatabase"
        secret_name = "connection-string-translation"
      }
      env {
        name        = "ConnectionStrings__AuthDatabase"
        secret_name = "connection-string-auth"
      }
    }
  }

  # The rolling image is owned by the CD pipeline (az containerapp job update by commit SHA), not
  # Terraform. Ignore image drift so `terraform apply` never reverts the job to the bootstrap tag
  # (var.image_tag). See ADR-0012.
  lifecycle {
    ignore_changes = [template[0].container[0].image]
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}
