# ADR-0016: Cloud telemetry via the ACA managed OpenTelemetry agent → Application Insights

**Status:** Accepted
**Date:** 2026-06-30
**Decision-makers:** Solo maintainer
**Related:** observability / `iac/` (`observability.tf`, `setup.tf`); ADR-0008 (cloud-agnostic
deployment — app-code neutrality), ADR-0012 (CD pipeline — provider chosen = Azure/ACA), ADR-0013
(Key Vault = source of truth for prod secrets); audit 0001 §H3 (+ §C3 sibling); issue #245

## Context

Audit 0001 §H3: all three web apps emit **clean, vendor-neutral OTLP** — OpenTelemetry traces +
metrics and Serilog→OTLP logs — but the exporter is gated on `OTEL_EXPORTER_OTLP_ENDPOINT` (the
`if (!IsNullOrWhiteSpace(otlpEndpoint))` guard in each app's `Program.cs`) and **no Container App
sets that variable**. So in the cloud there are **zero traces, zero metrics, zero OTLP logs**;
production runs on console logs to the
Log Analytics workspace only — operationally near-blind (observability scored 1.5/5). Dev is already
5/5 via the aspire-dashboard in `compose.yaml`. The app code is ready; this is pure infra.

Forces and code facts that constrain the choice:

- **App-code neutrality is a contract, not a preference (ADR-0008 §2):** no project references a
  cloud-provider SDK; telemetry leaves *only* via OTLP. The vendor-neutral pipeline is a deliberate
  strength — the owner's decision is to keep it untouched and add the Azure coupling **in infra
  only**.
- **The provider is chosen (ADR-0012):** all-in-Azure. The owner's call is to land telemetry in
  **Application Insights** and keep everything in Azure, *without* coupling app code.
- **azurerm 4.7.0 has no native block** for the Container Apps managed OpenTelemetry agent
  (hashicorp/terraform-provider-azurerm#28217).
- Existing infra to reuse: `azurerm_log_analytics_workspace.law` (`azure-law.tf`) and
  `azurerm_container_app_environment.app_env` (`azure_container_app_env.tf`). Provider discipline is a
  strength to preserve: exact-pinned providers + tracked lockfile (audit G20).

## Decision

### 1. Enable the ACA managed OpenTelemetry agent → App Insights (traces + logs), zero app change

The agent is enabled at the **Container App Environment** level. It **injects
`OTEL_EXPORTER_OTLP_ENDPOINT` into every container automatically**, so the apps' existing OTLP
pipeline starts shipping to Application Insights with **no app or env-var change**. The clean
`Program.cs` OTLP bootstrap stays exactly as is. App code remains cloud-agnostic (ADR-0008 §2 upheld —
only infra couples to Azure).

### 2. Application Insights is workspace-based, on the existing LAW, parametrized by `env_id`

`azurerm_application_insights.app_insights` with `workspace_id = azurerm_log_analytics_workspace.law.id`
(reuse the LAW — never a second workspace; classic non-workspace AI is retired by Azure),
`application_type = "web"`, `name = "lotrotmsappinsights${var.env_id}"`, tagged like its siblings. No
hardcoded `prod`.

### 3. Configure the agent via the `azapi` provider (azurerm gap)

`azapi_update_resource` **PATCHes only** the OTel properties onto `app_env`; azurerm — which has no
schema for these fields — leaves them untouched (the augmentation pattern the azapi docs recommend
for filling azurerm gaps). `azapi` is **exact-pinned `2.10.0`** (mirroring `azurerm = 4.7.0`, audit
G20), with **multi-platform hashes** (`linux_amd64`/`darwin_arm64`/`windows_amd64`) committed to
`.terraform.lock.hcl`.

### 4. The api-version is a *preview* one (`2024-10-02-preview`), verified against the schema

The OTel-agent properties (`appInsightsConfiguration` + `openTelemetryConfiguration`) are **not yet in
the stable ARM surface**. Verified directly against the azapi 2.10.0 embedded schema: the stable
`2024-03-01` and `2025-01-01` versions **reject** both properties, while the preview line
(`2024-02-02-preview` … `2025-02-02-preview`) **accepts** them. `2024-10-02-preview` is the version
Microsoft's ACA managed-OTel documentation uses. Revisit to a stable version once Azure GAs the agent.
This is an empirical schema fact, not a guess; azapi **schema-validates the body at plan**, so a wrong
api-version fails the `infra.yml` PR gate loudly rather than drifting silently. (This deliberately
diverges from the ticket's suggested `2024-03-01` — that version does not carry the feature.)

### 5. Traces + logs only — no metrics destination (YAGNI)

The managed agent forwards **metrics only to OTLP/Datadog sinks, never to App Insights** — so
`tracesConfiguration.destinations` and `logsConfiguration.destinations` are `["appInsights"]` and
there is **no `metricsConfiguration`**. Metric-shaped signal comes from **ACA platform metrics** (the
sibling audit 0001 §C3 alerting ticket) plus the request/dependency metrics App Insights
**reconstructs from the traces**. A metrics destination is deferred until a real need appears.

### 6. The connection string is wired directly (not via Key Vault), through `sensitive_body`

The App Insights `connection_string` is a TF-native attribute already materialized in state; it is
wired straight into the agent config. Unlike the app secrets (ADR-0013) it is an **ingestion key for a
telemetry sink, not a KV-class credential** — no data access, blast radius is only skewed/forged
telemetry — so Key Vault routing would add a secret + identity wiring for no real blast-radius
reduction. It is passed via azapi `sensitive_body` so it never surfaces in plan/console output.

## Consequences

### Positive

- Production gains **distributed traces + logs** (Application Map, end-to-end transaction search,
  KQL over `requests`/`dependencies`/`traces`/`exceptions`) — audit §H3 closed — with **zero app
  change**.
- **App neutrality preserved (ADR-0008 §2):** no Azure SDK enters app code; the clean vendor-neutral
  OTLP pipeline (a strength) is untouched. Re-pointing telemetry at another backend is an infra-only
  edit.
- Reuses the existing LAW and the exact-pin + tracked-lockfile discipline (audit G20); everything is
  `env_id`-parametrized, so a future staging environment inherits it for free.

### Negative / Accepted Trade-offs

- **Infra now couples to Azure** (azapi + App Insights). Accepted — owner decision (all-in-Azure);
  ADR-0008's neutrality is an *app-code* contract, which still holds. Infra was always Terraform-on-
  Azure.
- **A preview api-version sits in the critical infra path** — it may change or be deprecated.
  Mitigated: it is schema-validated at plan, the provider is pinned, and the revisit-on-GA is recorded
  here and in `observability.tf`.
- **No OpenTelemetry metrics in App Insights** (agent limitation). Mitigated by platform metrics
  (§C3) + trace-reconstructed request metrics.
- The AI ingestion connection string lives in Terraform state (like every other materialized
  attribute). Accepted — it is not a KV-class secret.

## Alternatives Considered

### A. ACA managed OTel agent → App Insights via `azapi` (this ADR)

Chosen. Lands telemetry in App Insights with **zero app change**, keeps app code cloud-agnostic, reuses
the LAW, and fills the azurerm gap with the standard azapi augmentation pattern.

### B. Azure Monitor OpenTelemetry Distro (`Azure.Monitor.OpenTelemetry.AspNetCore`) in each app

Rejected. Adds an Azure-specific package + bootstrap to **app code**, violating ADR-0008 §2 and
discarding the clean vendor-neutral OTLP pipeline — the exact vendor-lock the owner called out. The
managed agent gets the same data into App Insights without touching a single line of app code.

### C. Self-hosted OpenTelemetry Collector (sidecar or standalone) exporting to App Insights

Rejected. A whole component to run, scale, secure and upgrade, for zero present benefit over the
managed agent at pre-release scale. YAGNI now; reconsider only if multi-backend fan-out or
processing/sampling pipelines become a real need.

### D. App Insights connection string via Key Vault (like the ADR-0013 secrets)

Rejected. Over-engineered for a telemetry **ingestion key** (not a data-access credential): it is
already a TF-native attribute in state, and KV routing adds a secret + identity wiring for no real
blast-radius reduction. `sensitive_body` already keeps it out of plan output. Revisit if it ever
graduates to secret-class.

### E. Use a stable api-version (`2024-03-01` / `2025-01-01`)

Rejected. Schema-verified that stable does **not** expose the agent properties; the preview line is
the only path until Azure GAs the feature (Decision §4).

## Implementation Notes

- **New:** `iac/observability.tf` (`azurerm_application_insights.app_insights` +
  `azapi_update_resource.app_env_otel`); this ADR.
- **Changed:** `iac/setup.tf` (`azapi` in `required_providers` + provider block);
  `iac/.terraform.lock.hcl` (azapi 2.10.0 multi-platform hashes); `docs/deployment/runbook.md`
  (Observability section + metrics caveat).
- **Unchanged (deliberately):** the three `Program.cs` OTLP bootstraps and every app project (no Azure
  SDK — ADR-0008 §2); the LAW; the azurerm provider entry in the lockfile.

## References

- `docs/audits/0001-infrastructure-audit.md` — §H3 (telemetry connected to nothing in cloud), §C3
  (alerting / platform-metrics sibling)
- ADR-0008 — cloud-agnostic deployment & environment strategy (app-code neutrality upheld; only infra
  couples)
- ADR-0012 — continuous deployment pipeline (provider chosen = Azure / ACA)
- ADR-0013 — Key Vault single source of truth for prod secrets (the scope boundary for §6's tradeoff)
- issue #245 — audit 0001 H3 remediation (Tier D, observability)
- hashicorp/terraform-provider-azurerm#28217 — no native azurerm block for ACA OTel config
- Microsoft Learn — "Collect and read OpenTelemetry data in Azure Container Apps"
