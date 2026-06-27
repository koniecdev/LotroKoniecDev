resource "azurerm_container_app" "auth_api" {
  name                         = "lotrotms-auth-api-${var.env_id}"
  container_app_environment_id = azurerm_container_app_environment.app_env.id
  resource_group_name          = azurerm_resource_group.rg-lotrotms-dev-polc-001.name
  revision_mode                = "Single"

  secret {
    name  = "connection-string-auth"
    value = var.connection_string_auth
  }
  secret {
    name  = "openiddict-signing-key"
    value = var.openiddict_signing_key_rsa_private_key_xml
  }
  secret {
    name  = "openiddict-encryption-key"
    value = var.openiddict_encryption_key
  }
  secret {
    name  = "openiddict-api-client-secret"
    value = var.openiddict_api_client_secret
  }
  secret {
    name  = "smtp-username"
    value = var.smtp_username
  }
  secret {
    name  = "smtp-password"
    value = var.smtp_password
  }
  secret {
    name  = "admin-password"
    value = var.admin_password
  }

  template {
    min_replicas = 0
    max_replicas = 1

    container {
      name   = "auth-api"
      image  = "ghcr.io/koniecdev/lotrokoniecdev-auth-api:latest"
      cpu    = 0.25
      memory = "0.5Gi"

      liveness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/live"

        initial_delay           = 5
        interval_seconds        = 30
        timeout                 = 5
        failure_count_threshold = 3
      }

      readiness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/ready"

        interval_seconds        = 30
        timeout                 = 5
        failure_count_threshold = 3
        success_count_threshold = 1
      }

      startup_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/live"

        interval_seconds        = 10
        timeout                 = 5
        failure_count_threshold = 30
      }

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name  = "ASPNETCORE_URLS"
        value = "http://+:8080"
      }
      env {
        name        = "ConnectionStrings__AuthDatabase"
        secret_name = "connection-string-auth"
      }
      env {
        name  = "OpenIddict__Issuer"
        value = "https://auth.lotro-translator.pl"
      }
      env {
        name        = "OpenIddict__SigningKey__RsaPrivateKeyXml"
        secret_name = "openiddict-signing-key"
      }
      env {
        name        = "OpenIddict__EncryptionKey__Key"
        secret_name = "openiddict-encryption-key"
      }
      env {
        name        = "OpenIddict__ApiClientSecret"
        secret_name = "openiddict-api-client-secret"
      }
      env {
        name  = "OpenIddict__WebClient__RedirectUris__0"
        value = "https://lotro-translator.pl/callback"
      }
      env {
        name  = "OpenIddict__WebClient__PostLogoutRedirectUris__0"
        value = "https://lotro-translator.pl"
      }
      env {
        name  = "Cors__AllowedOrigins__0"
        value = "https://lotro-translator.pl"
      }
      env {
        name  = "DataProtection__KeyRingPath"
        value = "/keys"
      }
      env {
        name  = "Email__Host"
        value = "smtp-relay.brevo.com"
      }
      env {
        name  = "Email__Port"
        value = "587"
      }
      env {
        name  = "Email__Mode"
        value = "StartTls"
      }
      env {
        name  = "Email__SenderEmail"
        value = var.smtp_sender_email
      }
      env {
        name  = "Email__Sender"
        value = "LOTRO PL"
      }
      env {
        name        = "Email__Username"
        secret_name = "smtp-username"
      }
      env {
        name        = "Email__Password"
        secret_name = "smtp-password"
      }
      env {
        name  = "AdminUser__Username"
        value = var.admin_username
      }
      env {
        name  = "AdminUser__Email"
        value = var.admin_email
      }
      env {
        name        = "AdminUser__Password"
        secret_name = "admin-password"
      }

      volume_mounts {
        name = "keys"
        path = "/keys"
      }
    }

    volume {
      name         = "keys"
      storage_type = "AzureFile"
      storage_name = azurerm_container_app_environment_storage.auth_keys.name
    }
  }

  ingress {
    external_enabled           = true
    target_port                = 8080
    transport                  = "auto"
    allow_insecure_connections = false

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

resource "azurerm_container_app" "tms_api" {
  name                         = "lotrotms-tms-api-${var.env_id}"
  container_app_environment_id = azurerm_container_app_environment.app_env.id
  resource_group_name          = azurerm_resource_group.rg-lotrotms-dev-polc-001.name
  revision_mode                = "Single"

  secret {
    name  = "connection-string-translation"
    value = var.connection_string_translation
  }

  template {
    min_replicas = 0
    max_replicas = 1

    container {
      name   = "tms-api"
      image  = "ghcr.io/koniecdev/lotrokoniecdev-tms-api:latest"
      cpu    = 0.25
      memory = "0.5Gi"

      liveness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/live"

        initial_delay           = 5
        interval_seconds        = 30
        timeout                 = 5
        failure_count_threshold = 3
      }

      readiness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/ready"

        interval_seconds        = 30
        timeout                 = 5
        failure_count_threshold = 3
        success_count_threshold = 1
      }

      startup_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/live"

        interval_seconds        = 10
        timeout                 = 5
        failure_count_threshold = 30
      }

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name  = "ASPNETCORE_URLS"
        value = "http://+:8080"
      }
      env {
        name        = "ConnectionStrings__TranslationDatabase"
        secret_name = "connection-string-translation"
      }
      env {
        name  = "Auth__Issuer"
        value = "https://auth.lotro-translator.pl"
      }
      env {
        name  = "Auth__Authority"
        value = "https://auth.lotro-translator.pl"
      }
      env {
        name  = "Auth__Audience"
        value = "lotrokoniecdev-api"
      }
      env {
        name  = "Cors__AllowedOrigins__0"
        value = "https://lotro-translator.pl"
      }
      env {
        name  = "Bootstrap__Enabled"
        value = "false"
      }
    }
  }

  ingress {
    external_enabled           = true
    target_port                = 8080
    transport                  = "auto"
    allow_insecure_connections = false

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

resource "azurerm_container_app" "frontend" {
  name                         = "lotrotms-frontend-${var.env_id}"
  container_app_environment_id = azurerm_container_app_environment.app_env.id
  resource_group_name          = azurerm_resource_group.rg-lotrotms-dev-polc-001.name
  revision_mode                = "Single"

  template {
    min_replicas = 0
    max_replicas = 1

    container {
      name   = "frontend"
      image  = "ghcr.io/koniecdev/lotrokoniecdev-frontend:latest"
      cpu    = 0.25
      memory = "0.5Gi"

      liveness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/live"

        initial_delay           = 5
        interval_seconds        = 30
        timeout                 = 5
        failure_count_threshold = 3
      }

      readiness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/ready"

        interval_seconds        = 30
        timeout                 = 5
        failure_count_threshold = 3
        success_count_threshold = 1
      }

      startup_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/live"

        interval_seconds        = 10
        timeout                 = 5
        failure_count_threshold = 30
      }

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name  = "ASPNETCORE_URLS"
        value = "http://+:8080"
      }
      env {
        name  = "AuthSystem__Authority"
        value = "https://auth.lotro-translator.pl"
      }
      env {
        name  = "AuthSystem__BaseUrl"
        value = "https://auth.lotro-translator.pl/"
      }
      env {
        name  = "AuthSystem__ClientId"
        value = "lotrokoniecdev-web"
      }
      env {
        name  = "TranslationSystem__BaseUrl"
        value = "https://tms.lotro-translator.pl/"
      }
      env {
        name  = "DataProtection__KeyRingPath"
        value = "/keys"
      }

      volume_mounts {
        name = "keys"
        path = "/keys"
      }
    }

    volume {
      name         = "keys"
      storage_type = "AzureFile"
      storage_name = azurerm_container_app_environment_storage.frontend_keys.name
    }
  }

  ingress {
    external_enabled           = true
    target_port                = 8080
    transport                  = "auto"
    allow_insecure_connections = false

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}
