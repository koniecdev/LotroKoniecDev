locals {
  # Single source of truth for the platform's public origins (audit 0001 / H2, ADR-0017). Every OIDC
  # issuer, redirect, CORS allow-list and base URL in azure-container-apps.tf derives from
  # var.public_base_domain, so a new environment is one variable away. For prod
  # (var.public_base_domain = "lotro-translator.pl") each value below is byte-identical to the literal
  # it replaced — the OIDC `iss` and redirect_uri must match exactly (audit G15). Staging sets
  # public_base_domain = "staging.lotro-translator.pl", yielding auth.staging.… / tms.staging.… /
  # staging.… (the origins audit §6 prescribes).
  apex_origin  = "https://${var.public_base_domain}"
  auth_origin  = "https://auth.${var.public_base_domain}"
  tms_origin   = "https://tms.${var.public_base_domain}"
  callback_url = "${local.apex_origin}/callback"
}

locals {
  # Shared-environment mode (M6-22). The Azure subscription allows only ONE Container App Environment
  # total, and prod holds it. When var.aca_environment_name is set this env (staging) deploys its apps
  # INTO that existing managed environment instead of creating its own: create_env is false, so the
  # managed-environment + its workspace / app-insights / OTel-agent / monitoring singletons are skipped
  # and the shared environment is data-read instead. For prod the vars are empty -> create_env = true
  # -> every singleton is created exactly as before (byte-identical plan). app_env_id is the single
  # knob every container app, the migrator job, and the keyring storage point at.
  create_env = var.aca_environment_name == ""
  app_env_id = local.create_env ? azurerm_container_app_environment.app_env[0].id : data.azurerm_container_app_environment.shared[0].id
}
