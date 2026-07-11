# Azure Monitor alerting (audit 0001 §C3). Production was operationally blind: zero monitor
# resources existed, so a crash-loop, an error storm, a stopped log pipeline, or an auth outage
# (token issuance is the platform SPOF) surfaced only when the owner or a user noticed.
#
# Most alerts deliberately stand on signals that ALREADY exist — ACA platform metrics
# (Microsoft.App/containerApps) and the Log Analytics workspace the apps already stream console logs
# to. The auth latency alert additionally reads Application Insights (the OTel-reconstructed request
# metrics, observability.tf). The external availability SLO that used to live here was retired by
# ADR-0027 — see the comment above the auth_role_name local. Everything is parametrized by var.env_id
# so a future staging inherits alerting for free.
#
# Delivery is EMAIL ONLY (owner decision): one action group with a single email receiver
# (var.admin_email). No SMS, no webhook. Every alert below references that action group.
#
# Plan-on-PR caveat: Terraform `plan` does not validate metric names, log-table schemas, KQL, or the
# auth cloud_RoleName against the live Azure catalog — those are checked
# server-side at `apply` (or observed only once telemetry flows). The metric names used here
# (RestartCount / Requests / UsageNanoCores / WorkingSetBytes / KeyVault Availability / requests-
# duration) and the log tables (ContainerAppConsoleLogs_CL / Operation) are the documented
# Container Apps + Key Vault + App Insights + LAW signals.

locals {
  # All three container apps share one resource type, region and sizing (0.25 vCPU / 0.5 GiB,
  # ≤1 replica), so each metric alert below is a single rule fanned out per app via for_each.
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

  # Daily cold-start suppression window (the alert processing rule at the end of this file). Derived
  # from var.app_warm_window.start (5-field cron: minute hour …) so moving the warm window moves the
  # suppression with it — minute-of-day arithmetic, wrapping across midnight. 5 minutes of lead-in,
  # 35 minutes of tail: the observed noise fires within ~3 minutes of the wake-up and auto-resolves
  # within ~20 (2026-07-11: fired 05:03 UTC, resolved 05:10/05:19).
  warm_start_minute_of_day            = var.app_warm_window == null ? 0 : tonumber(split(" ", trimspace(var.app_warm_window.start))[1]) * 60 + tonumber(split(" ", trimspace(var.app_warm_window.start))[0])
  cold_start_suppression_from_minute  = (local.warm_start_minute_of_day + 1435) % 1440
  cold_start_suppression_until_minute = (local.warm_start_minute_of_day + 35) % 1440
  cold_start_suppression_start_time   = format("%02d:%02d:00", floor(local.cold_start_suppression_from_minute / 60), local.cold_start_suppression_from_minute % 60)
  cold_start_suppression_end_time     = format("%02d:%02d:00", floor(local.cold_start_suppression_until_minute / 60), local.cold_start_suppression_until_minute % 60)

  # Alert processing rules take a WINDOWS time-zone id, unlike KEDA's IANA name in app_warm_window.
  # Extend this map if the warm window ever moves to another zone — lookup fails loudly on a miss.
  windows_time_zone_by_iana = {
    "Europe/Warsaw" = "Central European Standard Time"
  }
}

# ---------------------------------------------------------------------------------------------------
# Action group — the single delivery channel for every alert. Email only (owner decision); no SMS.
# ---------------------------------------------------------------------------------------------------
resource "azurerm_monitor_action_group" "alerts" {
  count               = local.create_env ? 1 : 0
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

# M6-22: the action group gained a count for shared-environment mode — alerting belongs to the env
# that owns the workspace, so staging (which reuses prod's environment + workspace) inherits prod's
# alerts and creates none of its own. This shifts the address to [0]. For prod (create_env = true)
# the resource is otherwise unchanged, so this is a pure state-address move.
moved {
  from = azurerm_monitor_action_group.alerts
  to   = azurerm_monitor_action_group.alerts[0]
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
  for_each            = local.create_env ? local.monitored_container_apps : {}
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
    action_group_id = azurerm_monitor_action_group.alerts[0].id
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
  for_each            = local.create_env ? local.monitored_container_apps : {}
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
    action_group_id = azurerm_monitor_action_group.alerts[0].id
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

# Sustained CPU saturation (>80% of the 0.25-vCPU limit over 15 minutes). Informational: at
# max_replicas = 1 there is no horizontal headroom, so this is a capacity signal — investigate or
# raise the CPU request before it throttles.
resource "azurerm_monitor_metric_alert" "cpu_saturation" {
  for_each            = local.create_env ? local.monitored_container_apps : {}
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
    action_group_id = azurerm_monitor_action_group.alerts[0].id
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
  for_each            = local.create_env ? local.monitored_container_apps : {}
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
    action_group_id = azurerm_monitor_action_group.alerts[0].id
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
  count               = local.create_env ? 1 : 0
  name                = "lotrotms-alert-log-error-spike-${var.env_id}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  description         = "Spike of Error/Fatal Serilog entries in a container app's console logs. Fires by email."
  severity            = 2 # Warning

  evaluation_frequency = "PT5M"
  window_duration      = "PT5M"
  scopes               = [azurerm_log_analytics_workspace.law[0].id]
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
    action_groups = [azurerm_monitor_action_group.alerts[0].id]
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
  count               = local.create_env ? 1 : 0
  name                = "lotrotms-alert-law-daily-cap-${var.env_id}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  description         = "Log Analytics daily ingestion cap reached — log collection has stopped. Fires by email."
  severity            = 2 # Warning

  evaluation_frequency = "PT1H"
  window_duration      = "PT1H"
  scopes               = [azurerm_log_analytics_workspace.law[0].id]
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
    action_groups = [azurerm_monitor_action_group.alerts[0].id]
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

# ---------------------------------------------------------------------------------------------------
# The synthetic availability probes of ADR-0019 (three azurerm_application_insights_standard_web_test +
# their location-availability alerts) were REMOVED here by ADR-0027, and the removal is not a cost tweak
# but a correctness one: an external probe hitting the public origin every 15 minutes from three regions
# is itself a request, so it would wake a scaled-to-zero app around the clock and cancel the very saving
# the warm window exists to make. A probe cannot both watch an app and let it sleep.
#
# The replacement lives outside Azure: .github/workflows/health-ping.yml probes the same three public
# origins once a day, just before the warm window opens, on free GitHub-hosted minutes. It costs nothing,
# wakes the apps once, and a failed run emails the owner. The trade is deliberate — detection latency
# goes from ~20 minutes to ~24 hours, which is the right shape for a pre-release platform with zero users
# whose real requirement is "tell me in the morning if it died overnight". Restore the web tests (git
# history) the day real users arrive: at that point an always-warm prod is justified anyway, and the
# probe/warm-window conflict dissolves.
locals {
  # cloud_RoleName as the apps tag it via OTel (service.name = IHostEnvironment.ApplicationName = the
  # entry-assembly name; each Program.cs ConfigureResource). Scopes the auth latency alert below.
  # Confirm against live App Insights on first apply (managed OTel agent → AI role-name mapping).
  auth_role_name = "LotroKoniecDev.AuthSystem.API"
}

# Key Vault availability (audit 0001 §M14). KV is the secret-resolution SPOF — every app + the migrator
# resolve their connection strings / OpenIddict keys from it at revision start (ADR-0013), so a KV outage
# is a platform outage that today surfaces only as a hard boot failure. The vault publishes an
# Availability percentage; alert when it drops below 99% (a small buffer over a single transient throttle).
resource "azurerm_monitor_metric_alert" "key_vault_availability" {
  count               = local.create_env ? 1 : 0
  name                = "lotrotms-alert-keyvault-availability-${var.env_id}"
  resource_group_name = azurerm_resource_group.main.name
  scopes              = [data.azurerm_key_vault.secrets.id]
  description         = "Key Vault availability dropped below 99% (secret-resolution SPOF). Fires by email."
  severity            = 1 # Error
  frequency           = "PT5M"
  window_size         = "PT15M"

  criteria {
    metric_namespace = "Microsoft.KeyVault/vaults"
    metric_name      = "Availability"
    aggregation      = "Average"
    operator         = "LessThan"
    threshold        = 99
  }

  action {
    action_group_id = azurerm_monitor_action_group.alerts[0].id
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

# Auth latency — a LEADING indicator (Sev2), not an outage. Sustained high server response time on the
# token-issuance SPOF precedes readiness failures and user-visible slowness. Reads App Insights request
# duration (the managed OTel agent reconstructs request metrics from traces, observability.tf) scoped to
# the auth role. Auth-only by intent (ADR-0019 §4); a for_each extends it to tms/frontend if a need appears.
resource "azurerm_monitor_metric_alert" "auth_latency" {
  count               = local.create_env ? 1 : 0
  name                = "lotrotms-alert-auth-latency-${var.env_id}"
  resource_group_name = azurerm_resource_group.main.name
  scopes              = [azurerm_application_insights.app_insights[0].id]
  description         = "Auth-api server response time sustained above 2s (degradation leading indicator). Fires by email."
  severity            = 2 # Warning (leading indicator)
  frequency           = "PT5M"
  window_size         = "PT15M"

  criteria {
    metric_namespace = "microsoft.insights/components"
    metric_name      = "requests/duration"
    aggregation      = "Average"
    operator         = "GreaterThan"
    threshold        = 2000 # milliseconds

    dimension {
      name     = "cloud/roleName"
      operator = "Include"
      values   = [local.auth_role_name]
    }
  }

  action {
    action_group_id = azurerm_monitor_action_group.alerts[0].id
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

# M6-22: the three log alerts gained a count for shared-environment mode — they query the workspace,
# which staging reuses from prod, so staging inherits prod's log alerts and creates none of its own.
# Each address shifts to [0]; for prod (create_env = true) the rules are otherwise unchanged, so these
# are pure state-address moves.
moved {
  from = azurerm_monitor_scheduled_query_rules_alert_v2.log_error_spike
  to   = azurerm_monitor_scheduled_query_rules_alert_v2.log_error_spike[0]
}

moved {
  from = azurerm_monitor_scheduled_query_rules_alert_v2.law_daily_cap
  to   = azurerm_monitor_scheduled_query_rules_alert_v2.law_daily_cap[0]
}

# ---------------------------------------------------------------------------------------------------
# Alert processing rule — mute the daily cold-start false positives (#448, owner decision 2026-07-11).
# ADR-0027's warm-up cron wakes the scaled-to-zero apps every morning at app_warm_window.start, and
# the wake-up itself trips two alerts within minutes: auth-api restarts a replica once during its
# cold start (known, open investigation) and tms-api's warm-up burst crosses the 80% CPU threshold.
# Both auto-resolve, so the owner got four e-mails a day carrying zero information. This rule
# suppresses NOTIFICATIONS for exactly those two alert rules in a short daily window around the
# wake-up (the alerts still fire and stay visible in the portal); everything else — 5xx, memory,
# log-error spike, the other apps' restart alerts — keeps e-mailing even inside the window.
#
# Accepted trade-off: processing rules apply at FIRE time and the replica-restart alert
# auto-mitigates only when a fresh revision resets RestartCount, so a REAL auth-api crash-loop that
# starts inside the window would fire once, be suppressed, and never re-notify. The unsuppressed 5xx
# and log-error-spike alerts plus the daily health-ping cover that gap — the right shape for a
# pre-release platform with zero users.
resource "azurerm_monitor_alert_processing_rule_suppression" "cold_start_noise" {
  count               = local.create_env && var.app_warm_window != null ? 1 : 0
  name                = "lotrotms-apr-cold-start-noise-${var.env_id}"
  resource_group_name = azurerm_resource_group.main.name
  scopes              = [azurerm_resource_group.main.id]
  description         = "Suppress the known warm-up cold-start alert noise (auth-api replica restart + tms-api CPU burst) around the daily ADR-0027 wake-up."

  condition {
    alert_rule_id {
      operator = "Equals"
      values = [
        azurerm_monitor_metric_alert.replica_restart["auth-api"].id,
        azurerm_monitor_metric_alert.cpu_saturation["tms-api"].id,
      ]
    }
  }

  schedule {
    time_zone = local.windows_time_zone_by_iana[var.app_warm_window.timezone]

    recurrence {
      daily {
        start_time = local.cold_start_suppression_start_time
        end_time   = local.cold_start_suppression_end_time
      }
    }
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}
