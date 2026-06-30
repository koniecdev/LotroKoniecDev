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
