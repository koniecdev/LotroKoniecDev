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
  name                = "lotrotmsappinsights${var.env_id}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  workspace_id        = azurerm_log_analytics_workspace.law.id
  application_type    = "web"

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
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
  type        = "Microsoft.App/managedEnvironments@2024-10-02-preview"
  resource_id = azurerm_container_app_environment.app_env.id

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
      appInsightsConfiguration = {
        connectionString = azurerm_application_insights.app_insights.connection_string
      }
    }
  }
}
