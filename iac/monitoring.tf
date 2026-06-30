# Azure Monitor alerting (audit 0001 §C3). Production was operationally blind: zero monitor
# resources existed, so a crash-loop, an error storm, a stopped log pipeline, or an auth outage
# (token issuance is the platform SPOF) surfaced only when the owner or a user noticed.
#
# These alerts deliberately stand on signals that ALREADY exist — ACA platform metrics
# (Microsoft.App/containerApps) and the Log Analytics workspace the apps already stream console
# logs to — so they carry NO dependency on the cloud-telemetry / OTLP work (audit H3). Everything
# is parametrized by var.env_id so a future staging inherits alerting for free.
#
# Delivery is EMAIL ONLY (owner decision): one action group with a single email receiver
# (var.admin_email). No SMS, no webhook. Every alert below references that action group.
#
# Plan-on-PR caveat: Terraform `plan` does not validate metric names, log-table schemas, or KQL
# against the live Azure metric/log catalog — those are checked server-side at `apply`. The metric
# names used here (RestartCount / Requests / UsageNanoCores / WorkingSetBytes) and the log tables
# (ContainerAppConsoleLogs_CL / ContainerAppSystemLogs_CL / Operation) are the documented
# Container Apps + LAW signals.

locals {
  # All three container apps share one resource type, region and sizing (0.25 vCPU / 0.5 GiB,
  # min=max=1 replica), so each metric alert below is a single rule fanned out per app via for_each.
  # Single-scope alerts are universally supported by Azure Monitor (multi-resource metric alerts are
  # only allowed for a small allowlist of resource types that does NOT include Container Apps), need
  # no target_resource_type/location, and keep the config DRY. The app key is folded into each alert
  # name so the three instances stay uniquely identifiable.
  monitored_container_apps = {
    "auth-api" = azurerm_container_app.auth_api.id
    "tms-api"  = azurerm_container_app.tms_api.id
    "frontend" = azurerm_container_app.frontend.id
  }

  # Per-container limits (mirror azure-container-apps.tf): 0.25 vCPU, 0.5 GiB. Saturation
  # thresholds below derive from these so they track any future sizing change in one place.
  container_cpu_cores    = 0.25
  container_memory_bytes = 0.5 * 1024 * 1024 * 1024 # 0.5 GiB = 536870912 bytes

  # Alert at 80% sustained utilisation — high enough to be a real signal, low enough to act before
  # CPU throttling or an OOM kill (memory saturation is the usual precursor to a replica restart).
  cpu_saturation_nanocores  = local.container_cpu_cores * 1000 * 1000 * 1000 * 0.8 # 200,000,000 ncores
  memory_saturation_bytes   = floor(local.container_memory_bytes * 0.8)            # 429,496,729 bytes (80% of 0.5 GiB)
  http_server_error_floor   = 5                                                    # 5xx responses in a 5-minute window before the request alert fires
  log_error_spike_threshold = 10                                                   # Error/Fatal log entries (per app) in 5 minutes = an error storm
}

# ---------------------------------------------------------------------------------------------------
# Action group — the single delivery channel for every alert. Email only (owner decision); no SMS.
# ---------------------------------------------------------------------------------------------------
resource "azurerm_monitor_action_group" "alerts" {
  name                = "lotrotmsag${var.env_id}"
  resource_group_name = azurerm_resource_group.main.name
  # short_name shows up in the notification subject and is capped by Azure at 12 characters, so it
  # cannot simply carry the full env-qualified name — substr keeps it env-aware within the cap
  # (e.g. prod -> "lotrotmsprod", staging -> "lotrotmsstag").
  short_name = substr("lotrotms${var.env_id}", 0, 12)

  email_receiver {
    name                    = "admin-email"
    email_address           = var.admin_email
    use_common_alert_schema = true
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

# ---------------------------------------------------------------------------------------------------
# Metric alerts — ACA platform metrics (Microsoft.App/containerApps). Each metric is one rule fanned
# out per app (for_each over local.monitored_container_apps), single-scope — see the local for why.
# ---------------------------------------------------------------------------------------------------

# Crash-loop detector. Any replica restart is a strong signal something is wrong (OOM, failed
# readiness, unhandled crash). Threshold > 0 is intentionally sensitive per the audit; Maximum
# aggregation means the alert stays active for the lifetime of the affected revision (a fresh, clean
# revision resets the metric and auto-mitigates it) — read it as "this revision restarted,
# investigate", not "restarting right now".
resource "azurerm_monitor_metric_alert" "replica_restart" {
  for_each            = local.monitored_container_apps
  name                = "lotrotms-alert-replica-restart-${each.key}-${var.env_id}"
  resource_group_name = azurerm_resource_group.main.name
  scopes              = [each.value]
  description         = "Container app ${each.key} restarted a replica (crash-loop signal). Fires by email."
  severity            = 1 # Error
  frequency           = "PT1M"
  window_size         = "PT5M"

  criteria {
    metric_namespace = "Microsoft.App/containerApps"
    metric_name      = "RestartCount"
    aggregation      = "Maximum"
    operator         = "GreaterThan"
    threshold        = 0
  }

  action {
    action_group_id = azurerm_monitor_action_group.alerts.id
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

# Server-error spike. 4xx is deliberately NOT alerted: on the auth server 401/400 are normal control
# flow (bad credentials, expired tokens) and would only create noise. 5xx is the app failing to serve
# — the user-visible signal worth waking up for.
resource "azurerm_monitor_metric_alert" "http_server_errors" {
  for_each            = local.monitored_container_apps
  name                = "lotrotms-alert-http-5xx-${each.key}-${var.env_id}"
  resource_group_name = azurerm_resource_group.main.name
  scopes              = [each.value]
  description         = "Spike of HTTP 5xx responses from container app ${each.key}. Fires by email."
  severity            = 1 # Error
  frequency           = "PT1M"
  window_size         = "PT5M"

  criteria {
    metric_namespace = "Microsoft.App/containerApps"
    metric_name      = "Requests"
    aggregation      = "Total"
    operator         = "GreaterThan"
    threshold        = local.http_server_error_floor

    dimension {
      name     = "statusCodeCategory"
      operator = "Include"
      values   = ["5xx"]
    }
  }

  action {
    action_group_id = azurerm_monitor_action_group.alerts.id
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

# Sustained CPU saturation (>80% of the 0.25-vCPU limit over 15 minutes). Informational: at
# min=max=1 replica there is no horizontal headroom, so this is a capacity signal — investigate or
# raise the CPU request before it throttles.
resource "azurerm_monitor_metric_alert" "cpu_saturation" {
  for_each            = local.monitored_container_apps
  name                = "lotrotms-alert-cpu-high-${each.key}-${var.env_id}"
  resource_group_name = azurerm_resource_group.main.name
  scopes              = [each.value]
  description         = "Container app ${each.key} CPU sustained above 80% of its limit. Fires by email."
  severity            = 3 # Informational (capacity signal)
  frequency           = "PT5M"
  window_size         = "PT15M"

  criteria {
    metric_namespace = "Microsoft.App/containerApps"
    metric_name      = "UsageNanoCores"
    aggregation      = "Average"
    operator         = "GreaterThan"
    threshold        = local.cpu_saturation_nanocores
  }

  action {
    action_group_id = azurerm_monitor_action_group.alerts.id
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

# Sustained memory saturation (>80% of the 0.5-GiB limit over 15 minutes). Warning, not
# informational: hitting the memory limit triggers an OOM kill and a replica restart, so this is the
# usual leading indicator of the crash-loop alert above.
resource "azurerm_monitor_metric_alert" "memory_saturation" {
  for_each            = local.monitored_container_apps
  name                = "lotrotms-alert-memory-high-${each.key}-${var.env_id}"
  resource_group_name = azurerm_resource_group.main.name
  scopes              = [each.value]
  description         = "Container app ${each.key} memory sustained above 80% of its limit (OOM precursor). Fires by email."
  severity            = 2 # Warning
  frequency           = "PT5M"
  window_size         = "PT15M"

  criteria {
    metric_namespace = "Microsoft.App/containerApps"
    metric_name      = "WorkingSetBytes"
    aggregation      = "Average"
    operator         = "GreaterThan"
    threshold        = local.memory_saturation_bytes
  }

  action {
    action_group_id = azurerm_monitor_action_group.alerts.id
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

# ---------------------------------------------------------------------------------------------------
# Log alerts — scheduled queries over the Log Analytics workspace the apps already stream to.
# ---------------------------------------------------------------------------------------------------

# Error/Fatal log spike. In Production all three apps log via Serilog's CompactJsonFormatter (see
# appsettings.Production.json), so each console line in ContainerAppConsoleLogs_CL is a JSON document
# whose level is the "@l" field — present for Warning/Error/Fatal, omitted for Information. The query
# parses that field and counts Error/Fatal entries per app over the window; the rule fires for any
# app whose count crosses the spike threshold. This complements the metric alerts by catching
# logged failures that never become a 5xx or a restart (e.g. a background job throwing).
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "log_error_spike" {
  name                = "lotrotms-alert-log-error-spike-${var.env_id}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  description         = "Spike of Error/Fatal Serilog entries in a container app's console logs. Fires by email."
  severity            = 2 # Warning

  evaluation_frequency = "PT5M"
  window_duration      = "PT5M"
  scopes               = [azurerm_log_analytics_workspace.law.id]
  # The *_CL custom-log tables only materialise once the apps have emitted those records, which lags
  # a fresh environment's first apply. Skip create-time KQL schema validation so the alert can be
  # provisioned before any logs flow — letting a future staging inherit alerting without a
  # chicken-and-egg apply failure.
  skip_query_validation = true

  criteria {
    query                   = <<-KQL
      ContainerAppConsoleLogs_CL
      | extend entry = parse_json(Log_s)
      | where tostring(entry['@l']) in ('Error', 'Fatal')
      | summarize ErrorCount = count() by ContainerAppName_s
    KQL
    time_aggregation_method = "Maximum"
    metric_measure_column   = "ErrorCount"
    threshold               = local.log_error_spike_threshold
    operator                = "GreaterThan"

    dimension {
      name     = "ContainerAppName_s"
      operator = "Include"
      values   = ["*"]
    }

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  auto_mitigation_enabled = true

  action {
    action_groups = [azurerm_monitor_action_group.alerts.id]
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

# LAW daily-cap reached (audit M9). The workspace runs a deliberately small daily ingestion cap
# (azure-law.tf, daily_quota_gb) to bound cost; the failure mode is that once the cap is hit, log
# ingestion STOPS — exactly during an error storm, when logs matter most. Azure records that event
# in the Operation table ("Data collection stopped due to daily limit reached" / OverQuota), so this
# rule turns a silent blind-spot into an email. Checked hourly because the cap is a daily event.
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "law_daily_cap" {
  name                = "lotrotms-alert-law-daily-cap-${var.env_id}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  description         = "Log Analytics daily ingestion cap reached — log collection has stopped. Fires by email."
  severity            = 2 # Warning

  evaluation_frequency = "PT1H"
  window_duration      = "PT1H"
  scopes               = [azurerm_log_analytics_workspace.law.id]
  # The *_CL custom-log tables only materialise once the apps have emitted those records, which lags
  # a fresh environment's first apply. Skip create-time KQL schema validation so the alert can be
  # provisioned before any logs flow — letting a future staging inherit alerting without a
  # chicken-and-egg apply failure.
  skip_query_validation = true

  criteria {
    query                   = <<-KQL
      Operation
      | where OperationCategory =~ "Data collection Status"
      | where Detail has "OverQuota"
    KQL
    time_aggregation_method = "Count"
    threshold               = 0
    operator                = "GreaterThan"

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  auto_mitigation_enabled = true

  action {
    action_groups = [azurerm_monitor_action_group.alerts.id]
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

# Auth availability — the platform's first SLO. Auth issues every token, so an unhealthy auth app
# is a total-platform outage (front-channel login AND tms back-channel validation both fail). This
# is the external availability check of /health/ready: the ACA readiness probe hits /health/ready,
# and when it fails the platform records it in ContainerAppSystemLogs_CL. The query watches the
# auth app for genuine health failures only and fires at the highest severity. (A true synthetic
# external probe — Application Insights availability test — is the richer option, but it needs an
# App Insights resource + the public hostname extracted to a variable; that is deferred to the
# H2/H3 work. This log-based check uses signals that exist today, per the ticket's
# "scheduled-query / availability test — at least a basic one".)
#
# Token discipline (this is severity 0, so a false positive is expensive): the match is deliberately
# scoped to unhealthy / probe-failure signals and EXCLUDES routine lifecycle events — every deploy
# deactivates the superseded revision (azure-container-apps.tf, Multiple revision_mode +
# health-gated rollout), which would otherwise trip a Terminated/Killing match and page Critical on
# every release. Multi-word phrases use `contains` (term operators like `has` would not match them).
# The exact ACA system-log vocabulary cannot be queried from the plan sandbox — confirm/tune the
# token set against real ContainerAppSystemLogs_CL on first apply.
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "auth_availability" {
  name                = "lotrotms-slo-auth-availability-${var.env_id}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  description         = "SLO: auth /health/ready failing (token-issuance SPOF unavailable). Fires by email."
  severity            = 0 # Critical — platform-wide outage

  evaluation_frequency = "PT5M"
  window_duration      = "PT5M"
  scopes               = [azurerm_log_analytics_workspace.law.id]
  # The *_CL custom-log tables only materialise once the apps have emitted those records, which lags
  # a fresh environment's first apply. Skip create-time KQL schema validation so the alert can be
  # provisioned before any logs flow — letting a future staging inherit alerting without a
  # chicken-and-egg apply failure.
  skip_query_validation = true

  criteria {
    query                   = <<-KQL
      ContainerAppSystemLogs_CL
      | where ContainerAppName_s == "${azurerm_container_app.auth_api.name}"
      | where Reason_s has_any ("Unhealthy", "ProbeFailed")
          or Log_s contains "readiness probe failed"
          or Log_s contains "liveness probe failed"
          or Log_s contains "unhealthy"
    KQL
    time_aggregation_method = "Count"
    threshold               = 0
    operator                = "GreaterThan"

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  auto_mitigation_enabled = true

  action {
    action_groups = [azurerm_monitor_action_group.alerts.id]
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}
