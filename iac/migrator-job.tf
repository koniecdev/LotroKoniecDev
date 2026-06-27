resource "azurerm_container_app_job" "migrator" {
  name                         = "lotrotms-migrator-${var.env_id}"
  location                     = azurerm_resource_group.rg-lotrotms-prod-polc-001.location
  resource_group_name          = azurerm_resource_group.rg-lotrotms-prod-polc-001.name
  container_app_environment_id = azurerm_container_app_environment.app_env.id

  replica_timeout_in_seconds = 1800
  replica_retry_limit        = 1

  manual_trigger_config {
    parallelism              = 1
    replica_completion_count = 1
  }

  secret {
    name  = "connection-string-translation"
    value = var.connection_string_translation
  }

  secret {
    name  = "connection-string-auth"
    value = var.connection_string_auth
  }

  template {
    container {
      name   = "migrator"
      image  = "ghcr.io/koniecdev/lotrokoniecdev-migrator:latest"
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

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}
