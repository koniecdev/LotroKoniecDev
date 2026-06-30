resource "azurerm_container_app" "auth_api" {
  name                         = "lotrotms-auth-api-${var.env_id}"
  container_app_environment_id = azurerm_container_app_environment.app_env.id
  resource_group_name          = azurerm_resource_group.rg-lotrotms-prod-polc-001.name
  revision_mode                = "Multiple"

  identity {
    type         = "UserAssigned"
    identity_ids = [data.azurerm_user_assigned_identity.aca.id]
  }

  # The rolling image is owned by the CD pipeline (az containerapp update by commit SHA), not
  # Terraform. Ignore image drift so `terraform apply` never reverts a deployed revision back to the
  # bootstrap tag (var.image_tag). See ADR-0012 §2.
  #
  # Traffic weights are likewise owned by the CD health-gated rollout (ADR-0012 §5 amendment, audit
  # 0001 H7): each deploy creates a candidate revision at 0% traffic, smokes it, then shifts 100% to
  # it. Terraform only seeds the initial weight below (latest_revision = true) so a freshly-created
  # app serves its first revision; it must NOT revert the pipeline's per-deploy traffic decisions.
  lifecycle {
    ignore_changes = [
      template[0].container[0].image,
      ingress[0].traffic_weight,
    ]
  }

  # Secrets resolve from Key Vault at runtime through the user-assigned identity (ADR-0013) — no
  # plaintext value enters this configuration or the Terraform state.
  secret {
    name                = "connection-string-auth"
    key_vault_secret_id = local.kv_secret_id["connection-string-auth"]
    identity            = data.azurerm_user_assigned_identity.aca.id
  }
  secret {
    name                = "openiddict-signing-key"
    key_vault_secret_id = local.kv_secret_id["openiddict-signing-key"]
    identity            = data.azurerm_user_assigned_identity.aca.id
  }
  secret {
    name                = "openiddict-encryption-key"
    key_vault_secret_id = local.kv_secret_id["openiddict-encryption-key"]
    identity            = data.azurerm_user_assigned_identity.aca.id
  }
  secret {
    name                = "openiddict-api-client-secret"
    key_vault_secret_id = local.kv_secret_id["openiddict-api-client-secret"]
    identity            = data.azurerm_user_assigned_identity.aca.id
  }
  secret {
    name                = "smtp-username"
    key_vault_secret_id = local.kv_secret_id["smtp-username"]
    identity            = data.azurerm_user_assigned_identity.aca.id
  }
  secret {
    name                = "smtp-password"
    key_vault_secret_id = local.kv_secret_id["smtp-password"]
    identity            = data.azurerm_user_assigned_identity.aca.id
  }
  secret {
    name                = "admin-password"
    key_vault_secret_id = local.kv_secret_id["admin-password"]
    identity            = data.azurerm_user_assigned_identity.aca.id
  }

  template {
    # Keep one warm replica per revision (audit 0001 M3 / ADR-0012 R8): no cold starts, and the
    # health-gated rollout's 0%-traffic candidate stays smokeable while the previous revision stays
    # warm for cross-revision token validation. The CD rollout deactivates superseded revisions so
    # min_replicas=1 doesn't accumulate idle replicas.
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "auth-api"
      image  = "ghcr.io/koniecdev/lotrokoniecdev-auth-api:${var.image_tag}"
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
  resource_group_name          = azurerm_resource_group.rg-lotrotms-prod-polc-001.name
  revision_mode                = "Multiple"

  identity {
    type         = "UserAssigned"
    identity_ids = [data.azurerm_user_assigned_identity.aca.id]
  }

  # The rolling image is owned by the CD pipeline (az containerapp update by commit SHA), not
  # Terraform. Ignore image drift so `terraform apply` never reverts a deployed revision back to the
  # bootstrap tag (var.image_tag). See ADR-0012 §2.
  #
  # Traffic weights are likewise owned by the CD health-gated rollout (ADR-0012 §5 amendment, audit
  # 0001 H7): each deploy creates a candidate revision at 0% traffic, smokes it, then shifts 100% to
  # it. Terraform only seeds the initial weight below (latest_revision = true) so a freshly-created
  # app serves its first revision; it must NOT revert the pipeline's per-deploy traffic decisions.
  lifecycle {
    ignore_changes = [
      template[0].container[0].image,
      ingress[0].traffic_weight,
    ]
  }

  # Secret resolves from Key Vault at runtime through the user-assigned identity (ADR-0013).
  secret {
    name                = "connection-string-translation"
    key_vault_secret_id = local.kv_secret_id["connection-string-translation"]
    identity            = data.azurerm_user_assigned_identity.aca.id
  }

  template {
    # Keep one warm replica per revision (audit 0001 M3 / ADR-0012 R8): no cold starts, and the
    # health-gated rollout's 0%-traffic candidate stays smokeable while the previous revision stays
    # warm for cross-revision token validation. The CD rollout deactivates superseded revisions so
    # min_replicas=1 doesn't accumulate idle replicas.
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "tms-api"
      image  = "ghcr.io/koniecdev/lotrokoniecdev-tms-api:${var.image_tag}"
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
  resource_group_name          = azurerm_resource_group.rg-lotrotms-prod-polc-001.name
  revision_mode                = "Multiple"

  # The rolling image is owned by the CD pipeline (az containerapp update by commit SHA), not
  # Terraform. Ignore image drift so `terraform apply` never reverts a deployed revision back to the
  # bootstrap tag (var.image_tag). See ADR-0012 §2.
  #
  # Traffic weights are likewise owned by the CD health-gated rollout (ADR-0012 §5 amendment, audit
  # 0001 H7): each deploy creates a candidate revision at 0% traffic, smokes it, then shifts 100% to
  # it. Terraform only seeds the initial weight below (latest_revision = true) so a freshly-created
  # app serves its first revision; it must NOT revert the pipeline's per-deploy traffic decisions.
  lifecycle {
    ignore_changes = [
      template[0].container[0].image,
      ingress[0].traffic_weight,
    ]
  }

  template {
    # Keep one warm replica per revision (audit 0001 M3 / ADR-0012 R8): no cold starts, and the
    # health-gated rollout's 0%-traffic candidate stays smokeable while the previous revision stays
    # warm for cross-revision token validation. The CD rollout deactivates superseded revisions so
    # min_replicas=1 doesn't accumulate idle replicas.
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "frontend"
      image  = "ghcr.io/koniecdev/lotrokoniecdev-frontend:${var.image_tag}"
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
