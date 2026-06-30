# Cloud telemetry for the three web apps (audit 0001 H3, ADR-0016).
#
# All three apps already emit clean, vendor-neutral OTLP (traces + Serilog->OTLP logs), but no
# Container App sets OTEL_EXPORTER_OTLP_ENDPOINT, so in the cloud the telemetry goes nowhere. The
# Container Apps *managed* OpenTelemetry agent closes that gap with zero app change: enabled at the
# environment level it injects OTEL_EXPORTER_OTLP_ENDPOINT into every container automatically and
# forwards traces + logs to Application Insights. The app code stays cloud-agnostic (ADR-0008 §2 —
# only this infra couples to Azure).

# Workspace-based Application Insights, backed by the existing Log Analytics workspace (audit 0001
# H3 — reuse the LAW, never create a second workspace). Classic (non-workspace) AI is retired by
# Azure, so workspace_id is mandatory.
resource "azurerm_application_insights" "app_insights" {
  count               = local.create_env ? 1 : 0
  name                = "lotrotmsappinsights${var.env_id}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  workspace_id        = azurerm_log_analytics_workspace.law[0].id
  application_type    = "web"

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

# M6-22: app_insights gained a count for shared-environment mode (it backs the OTel agent on the
# environment we create, which staging skips), shifting its address to [0]. For prod (create_env =
# true) the resource is otherwise unchanged, so this is a pure state-address move.
moved {
  from = azurerm_application_insights.app_insights
  to   = azurerm_application_insights.app_insights[0]
}

# Enable the Container Apps managed OpenTelemetry agent on the managed environment. azurerm 4.7.0 has
# no native block for this (hashicorp/terraform-provider-azurerm#28217), so it is patched on with
# azapi: azapi_update_resource PATCHes only the properties in its body, and azurerm — which has no
# schema for these OTel fields — leaves them untouched, the augmentation pattern the azapi docs
# recommend for filling azurerm gaps.
#
# Metrics are intentionally NOT a destination: the managed agent forwards metrics only to
# OTLP/Datadog sinks, never to App Insights (traces + logs only). Platform metrics (sibling audit
# 0001 C3 ticket) cover that gap, and App Insights still reconstructs request metrics from the traces.
#
# Tradeoff — the App Insights connection string is wired straight from its TF-native attribute into
# the agent config rather than routed through Key Vault. Unlike the app secrets (ADR-0013) it is an
# ingestion key for a telemetry sink, not a KV-class credential (no data access, blast radius is only
# skewed telemetry) and it is already materialized in state; direct wiring is simpler and acceptable.
# It is passed via sensitive_body so it never surfaces in plan/console output.
#
# api-version: a *preview* version is required, not the stable 2024-03-01/2025-01-01 — the OTel-agent
# properties (appInsightsConfiguration + openTelemetryConfiguration) are not yet in the stable ARM
# surface (verified against the azapi 2.10.0 embedded schema: stable rejects them, the preview line
# accepts them). 2024-10-02-preview is the version Microsoft's ACA managed-OTel docs use. Revisit to a
# stable version once Azure GAs the agent (azapi schema-validates the body at plan, so a wrong version
# fails the infra.yml gate loudly rather than drifting).
resource "azapi_update_resource" "app_env_otel" {
  count       = local.create_env ? 1 : 0
  type        = "Microsoft.App/managedEnvironments@2024-10-02-preview"
  resource_id = azurerm_container_app_environment.app_env[0].id

  body = {
    properties = {
      openTelemetryConfiguration = {
        tracesConfiguration = {
          destinations = ["appInsights"]
        }
        logsConfiguration = {
          destinations = ["appInsights"]
        }
      }
    }
  }

  sensitive_body = {
    properties = {
      # #246 fix (M6-23): the 2024-10-02-preview managedEnvironments PATCH re-validates the WHOLE
      # resource, and the environment's existing appLogsConfiguration round-trips without the
      # write-only logAnalyticsConfiguration.sharedKey — so a body that omits it fails apply with
      # `400 LogAnalyticsConfiguration is invalid. Must provide a valid LogAnalyticsConfiguration`.
      # Re-supply the SAME workspace (customerId + sharedKey) so the existing log-analytics destination
      # stays valid while the OTel agent is added; this changes no destination, only re-asserts it.
      appLogsConfiguration = {
        destination = "log-analytics"
        logAnalyticsConfiguration = {
          customerId = azurerm_log_analytics_workspace.law[0].workspace_id
          sharedKey  = azurerm_log_analytics_workspace.law[0].primary_shared_key
        }
      }
      appInsightsConfiguration = {
        connectionString = azurerm_application_insights.app_insights[0].connection_string
      }
    }
  }
}

# M6-22: app_env_otel gained a count for shared-environment mode — we patch the OTel agent only onto
# the environment we create (staging never reconfigures prod's shared environment), shifting its
# address to [0]. For prod (create_env = true) the patch is otherwise unchanged, so this is a pure
# state-address move.
moved {
  from = azapi_update_resource.app_env_otel
  to   = azapi_update_resource.app_env_otel[0]
}
