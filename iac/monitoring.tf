# Azure Monitor alerting (audit 0001 §C3). Production was operationally blind: zero monitor
# resources existed, so a crash-loop, an error storm, a stopped log pipeline, or an auth outage
# (token issuance is the platform SPOF) surfaced only when the owner or a user noticed.
#
# Most alerts deliberately stand on signals that ALREADY exist — ACA platform metrics
# (Microsoft.App/containerApps) and the Log Analytics workspace the apps already stream console logs
# to. The availability SLO + auth latency added in ADR-0019 additionally read Application Insights
# (the synthetic web tests + the OTel-reconstructed request metrics, observability.tf). Everything is
# parametrized by var.env_id so a future staging inherits alerting for free.
#
# Delivery is EMAIL ONLY (owner decision): one action group with a single email receiver
# (var.admin_email). No SMS, no webhook. Every alert below references that action group.
#
# Plan-on-PR caveat: Terraform `plan` does not validate metric names, log-table schemas, KQL, the
# web-test geo codes, or the auth cloud_RoleName against the live Azure catalog — those are checked
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
# min=max=1 replica there is no horizontal headroom, so this is a capacity signal — investigate or
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
# External synthetic availability — the platform's real SLO (ADR-0019). This REPLACES the former
# log-based auth_availability, which fired on EVERY deploy: that rule matched ContainerAppSystemLogs_CL
# by app NAME (not revision), so a health-gated candidate revision (0% traffic, still starting → a few
# transient /health/ready misses until its Npgsql pool connects) tripped a Sev0 while users were served
# the whole time by the previous, healthy revision.
#
# A standard web test probes each app's PUBLIC origin from multiple regions. The public origin only
# ever resolves to the revision serving 100% of traffic — a 0%-traffic candidate is reachable solely via
# its private <app>---cd-candidate label FQDN — so a deploy CANNOT trip these. The false-positive class
# is eliminated by construction, not by threshold tuning. This is exactly the synthetic probe audit 0001
# §C3 named as the correct instrument, deferred only for want of an App Insights resource + a public-
# origin variable; both now exist (ADR-0016 / ADR-0017).
locals {
  # cloud_RoleName as the apps tag it via OTel (service.name = IHostEnvironment.ApplicationName = the
  # entry-assembly name; each Program.cs ConfigureResource). Scopes the auth latency alert below.
  # Confirm against live App Insights on first apply (managed OTel agent → AI role-name mapping).
  auth_role_name = "LotroKoniecDev.AuthSystem.API"

  # Probe locations (Azure Monitor availability population tags): West Europe (Amsterdam), North Europe
  # (Dublin), France Central (Paris) — EU-centric, closest to the Poland Central deployment.
  availability_test_geo_locations = ["emea-nl-ams-azr", "emea-gb-db3-azr", "emea-fr-pra-edge"]

  # One entry per user-facing app: its public origin, the status it must return, and the alert severity.
  # auth is Sev0 (token-issuance SPOF = platform outage); tms + frontend are Sev1 (degraded, not a total
  # outage). expected_status_code 0 = "any code < 400" — the Static-SSR frontend has no /health endpoint,
  # so a 2xx/3xx on '/' is its liveness.
  availability_web_tests = {
    "auth"     = { url = "${local.auth_origin}/health/ready", expected_status_code = 200, severity = 0 }
    "tms"      = { url = "${local.tms_origin}/health/ready", expected_status_code = 200, severity = 1 }
    "frontend" = { url = "${local.apex_origin}/", expected_status_code = 0, severity = 1 }
  }
}

# The web tests. Standard tests (classic ping tests are Microsoft-retired) run from the geo locations
# above every 15 minutes, validating the status code + the SSL certificate's remaining lifetime. The
# 7-day SSL threshold is a free, low-noise cert-expiry guard: the ACA managed cert auto-renews well
# before 7 days, so a trip means auto-renew has failed — a real imminent outage, correctly escalated.
#
# Cadence is a cost knob (ADR-0020): standard tests bill $0.0006 PER EXECUTION, so 3 tests × 3
# locations × every 300 s ≈ 77.8k executions ≈ $47/month — the single largest line item on the
# student subscription, dwarfing the apps' compute. 900 s cuts that to ≈ $15.6/month (and cuts the
# probes' own request-telemetry ingestion 3×) at the price of ~15-20 min worst-case detection —
# acceptable pre-release with zero users. When real users arrive, drop auth (the Sev0 SPOF) back to
# 300 (+$10/month) and leave tms/frontend at 900.
resource "azurerm_application_insights_standard_web_test" "availability" {
  for_each                = local.create_env ? local.availability_web_tests : {}
  name                    = "lotrotms-webtest-${each.key}-${var.env_id}"
  resource_group_name     = azurerm_resource_group.main.name
  location                = azurerm_resource_group.main.location
  application_insights_id = azurerm_application_insights.app_insights[0].id
  geo_locations           = local.availability_test_geo_locations
  frequency               = 900
  timeout                 = 30
  enabled                 = true
  retry_enabled           = true
  description             = "External synthetic availability probe of ${each.key} at its public origin (ADR-0019)."

  request {
    url                      = each.value.url
    follow_redirects_enabled = true
  }

  validation_rules {
    expected_status_code        = each.value.expected_status_code
    ssl_check_enabled           = true
    ssl_cert_remaining_lifetime = 7
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

# Availability alert per web test. Durability is GEOGRAPHIC, not temporal: the alert fires only when at
# least 2 of the 3 locations fail in the window — rejecting a single-location network blip WITHOUT the
# detection delay a "wait N consecutive windows" debounce would add to a real Sev0. A
# location-availability criterion needs BOTH the web test id and the App Insights component id in scopes.
# The window must be wide enough to hold results from ≥2 locations at the 900 s cadence (ADR-0020):
# PT30M holds ~2 results per location; the former PT5M would often hold ≤1 result TOTAL, so the
# 2-location quorum could never be reached and the alert would go silent — widening it is correctness,
# not tuning.
resource "azurerm_monitor_metric_alert" "availability" {
  for_each            = local.create_env ? local.availability_web_tests : {}
  name                = "lotrotms-slo-availability-${each.key}-${var.env_id}"
  resource_group_name = azurerm_resource_group.main.name
  scopes = [
    azurerm_application_insights_standard_web_test.availability[each.key].id,
    azurerm_application_insights.app_insights[0].id,
  ]
  description = "SLO: ${each.key} unreachable at its public origin from multiple regions. Fires by email."
  severity    = each.value.severity
  frequency   = "PT1M"
  window_size = "PT30M"

  application_insights_web_test_location_availability_criteria {
    web_test_id           = azurerm_application_insights_standard_web_test.availability[each.key].id
    component_id          = azurerm_application_insights.app_insights[0].id
    failed_location_count = 2
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
