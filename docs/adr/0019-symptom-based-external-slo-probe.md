# ADR-0019: Symptom-based alerting — external synthetic SLO probe replaces the log-based auth check

> **Superseded in part by ADR-0027 (2026-07-09):** the three standard web tests and their availability
> alerts are removed. An external probe of the public origin wakes a scaled-to-zero app, so it cannot
> coexist with the warm window. The symptom-based *principle* stands; the instrument is now a daily
> GitHub Actions cron (`.github/workflows/health-ping.yml`).

**Status:** Accepted
**Date:** 2026-07-01
**Decision-makers:** Solo maintainer
**Related:** audit 0001 §C3 (zero alerting — the log-based check was its explicit *interim*), §M14 (Key
Vault not in readiness), §M9 (LAW cap blind-spot); ADR-0016 (App Insights via the ACA managed OTel
agent — now already provisioned), ADR-0017 (per-env IaC parametrization — made the public origin a
single `local.*` var), ADR-0012 (health-gated rollout — the source of the false-positive), ADR-0018
(shared-environment mode — every monitoring resource is `create_env`-guarded)

## Context

`iac/monitoring.tf`'s `auth_availability` rule (the platform's first SLO, Sev0) fires on **every
deploy** while the platform is fully healthy. Root cause, confirmed in code:

- The scheduled query matches `ContainerAppSystemLogs_CL` filtered **only by
  `ContainerAppName_s`** (`monitoring.tf:369-377`) — it cannot tell the production revision from a
  candidate.
- The health-gated rollout (ADR-0012, `deploy.yml`) creates each release as a **candidate revision
  at 0% traffic**, smokes it on a private label FQDN, then shifts traffic. A freshly-started
  candidate fails `/health/ready` for a few probes until its Npgsql pool connects (readiness =
  `AddNpgSql`, tagged `ready`; `Program.cs`), and ACA logs each miss as *"readiness probe failed /
  Unhealthy"* under the app name.
- The alert threshold is `Count > 0` over one 5-minute window, one failing period — **zero
  tolerance**. A single transient candidate-startup log = Sev0, even though users are served by the
  previous, healthy revision the whole time.

This is textbook **cause-based alerting** (an internal probe log) standing in for a **symptom**
(users cannot get a token). It manufactures alert fatigue: after N deploy-time Sev0s, a *real*
auth outage gets dismissed as "probably a deploy". The audit anticipated exactly this — §C3 shipped
the log query as *"the 'at least a basic' one"* and named a **true synthetic external probe (an
Application Insights availability test)** as the correct instrument, deferred only because it "needs
an App Insights resource + the public hostname extracted to a variable". **Both blockers are now
gone:** ADR-0016 provisioned workspace-based App Insights (`observability.tf`), and ADR-0017 made
`https://auth.<domain>` a single-source `local.auth_origin`.

Adjacent gaps the same audit flagged and this ADR closes opportunistically: **no Key Vault
availability alert** (§M14 — KV is the secret-resolution SPOF), **no latency signal**, and the
platform's SPOFs (auth, KV) having no leading indicator before a hard outage.

## Decision

### 1. The SLO is measured by an external synthetic probe of the public origin, not by logs

Replace `scheduled_query_rules_alert_v2.auth_availability` with an
`azurerm_application_insights_standard_web_test` per user-facing app, hitting the **public HTTPS
origin** (the revision serving 100% of traffic), plus an `azurerm_monitor_metric_alert` using the
`application_insights_web_test_location_availability_criteria` block:

| Web test | Target (`local.*`) | Expects | Severity |
|---|---|---|---|
| auth | `${auth_origin}/health/ready` | 200 | **0 — token-issuance SPOF** |
| tms | `${tms_origin}/health/ready` | 200 | 1 |
| frontend | `${apex_origin}/` | 200–399 (Static-SSR home) | 1 |

> Narrowed by ADR-0025 (2026-07-05): `/health/ready` is DB-free (the probe runs zero checks), so
> these web tests assert HTTP + TLS liveness of the public origin — not database health. The
> database is proven by the deploy smoke (legs 2/4) and per-request telemetry.

**This is what makes the fix architectural, not a threshold tweak:** a candidate revision is
reachable *only* through its private `<app>---cd-candidate.<domain>` label FQDN and takes **0%**
public traffic during smoke, so an external probe of the public origin **cannot observe it**. The
false-positive class is eliminated by construction — no exclusion filter, no debouncing that could
also mask a real outage.

### 2. Durability is geographic, not temporal

The web tests run from **three EU-centric locations** (West Europe, North Europe, France Central)
every 300 s. The alert fires only when **≥2 of 3 locations** fail
(`failed_location_count = 2`). This rejects a single-location network blip *without* delaying a real
outage the way "wait N consecutive windows" would — the right trade-off for a Sev0 availability SLO.

> **Amended by ADR-0020 (2026-07-01):** cadence is **900 s** and the alert window **PT30M** — at
> $0.0006/execution the 300 s cadence cost ≈ $47/month on the student subscription, and a PT5M
> window cannot reliably hold a 2-location quorum at the sparser cadence. Locations, quorum and SSL
> validation unchanged.

### 3. Certificate expiry rides on the same web test

Each standard web test enables SSL validation with a **7-day** remaining-lifetime threshold. The ACA
managed custom-domain cert auto-renews well before that, so the gate never trips in a healthy cycle;
if it does, auto-renew has failed and a real outage is imminent — correctly escalated with
availability. No separate resource, no steady-state noise.

### 4. Cover the SPOFs with leading indicators

- **Key Vault availability (§M14):** `azurerm_monitor_metric_alert` scoped to
  `data.azurerm_key_vault.secrets.id`, `Microsoft.KeyVault/vaults` → `Availability` < 100% over
  5 min, **Sev1**. KV resolves every app secret at revision start; its loss is a platform outage.
- **Auth latency:** `azurerm_monitor_metric_alert` on App Insights `requests/duration` (the OTel
  agent reconstructs request metrics from traces, `observability.tf`) filtered to the auth role,
  above a sustained threshold over 15 min, **Sev2** — a leading indicator before latency becomes a
  probe failure. Auth-only by intent (§7 YAGNI); a `for_each` extends it later.

### 5. Delivery stays email-only, one action group

Deliberate (maintainer decision, pre-release, zero users): the existing single
`azurerm_monitor_action_group` (email to `var.admin_email`) serves every severity. Severity is used
for **triage discipline** (Sev0 = platform outage, Sev1 = degraded, Sev2 = leading indicator), not
routing. Revisit when a second channel earns its keep.

### 6. Explicit YAGNI

Not built, and why: **multi-window multi-burn-rate error-budget SLO** (no traffic/SLA to spend a
budget against yet); **a second alert channel / escalation** (§5); **automated deploy-window
suppression** (the external probe is deploy-safe by construction, and the remaining log/metric
alerts get a documented manual suppression procedure in the runbook, not an automation). Each is a
one-change addition when a real need appears.

### 7. Shared-environment compliance

Every new resource is `create_env`-guarded exactly like the rest of `monitoring.tf` (ADR-0018):
the web tests + their alerts + the KV/latency alerts are `count`/`for_each`-gated on
`local.create_env`, so **staging inherits prod's monitoring and creates none of its own**, and the
prod plan for existing resources stays a no-op.

## Consequences

### Positive

- **Zero deploy-time false-positives** on the availability SLO — measured, not asserted: the probe
  physically cannot see a 0%-traffic candidate.
- The SLO now means what its name says — **user-facing availability of the serving revision**,
  from outside the cluster, across regions.
- Both platform SPOFs (auth, Key Vault) gain coverage; latency gives a leading indicator; cert
  expiry is covered for free.
- Reuses infra that already exists (App Insights, `local.auth_origin`) — no new App Insights, no new
  variable.

### Negative / Accepted Trade-offs

- **Availability signal now depends on App Insights ingestion** rather than the raw LAW log table.
  Acceptable: App Insights is already the telemetry sink (ADR-0016) and web tests are a first-class,
  supported availability instrument.
- **Standard web tests bill per execution** (3 tests × 3 locations × every 5 min). At this cadence
  the ingestion/cost is negligible against the LAW `daily_quota_gb`; noted rather than a concern.
  **Amended by ADR-0020 (2026-07-01):** this assessment missed the execution meter itself —
  ≈ $47/month at 300 s, the subscription's largest line item; cadence dropped to 900 s.
- **Email remains a single delivery path** (§5) — a missed email misses a Sev0. Accepted for
  pre-release; §6 revisit.
- Removing the log-based rule is a **destroy + create** in state (pre-release, breaking changes are
  free — CLAUDE.md).

## Alternatives Considered

- **Tune the log rule's threshold / add consecutive-window debounce** — rejected: still cause-based
  (an internal probe log), still filtered by app name, and a debounce long enough to survive a
  candidate's startup also delays a real Sev0. Treats the symptom of the symptom.
- **Exclude candidate revisions in KQL** (`RevisionName_s !contains "cd-candidate"`) — rejected as
  brittle: depends on undocumented `ContainerAppSystemLogs_CL` fields and re-breaks if the rollout's
  revision-suffix scheme changes.
- **Full burn-rate error-budget SLO** — deferred (§6): correct at scale, YAGNI at zero traffic.
- **Application Insights *classic* ping test** — rejected: Microsoft-retired; the standard web test
  is its supported successor (adds SSL validation, used in §3).

## Implementation Notes

- **New:** this ADR; in `iac/monitoring.tf` — three `azurerm_application_insights_standard_web_test`
  + three webtest-location-availability `azurerm_monitor_metric_alert` (auth Sev0 / tms,frontend
  Sev1), a Key Vault `Availability` metric alert (Sev1), an auth `requests/duration` metric alert
  (Sev2). All `create_env`-guarded; web tests + availability alerts fan out via a single `for_each`
  map carrying per-app origin + severity + expected-status.
- **Changed:** `iac/monitoring.tf` — remove `scheduled_query_rules_alert_v2.auth_availability` and
  its `moved {}`; `docs/deployment/runbook.md` — Monitoring & alerting table refreshed, per-alert
  response steps + a manual suppression procedure for planned work.
- **Unchanged (deliberately):** the metric alerts on ACA platform signals (`replica_restart`,
  `http_5xx`, `cpu`, `memory`) and `law_daily_cap` — well-formed, kept green with assertions
  untouched; the action group (§5); the health-gated rollout.
- **Supersession:** closes audit 0001 §C3's interim availability check with its named target
  instrument, and §M14; retires the deploy-time false-positive.

## References

- `docs/audits/0001-infrastructure-audit.md` — §C3 (interim log check + the deferred synthetic
  probe), §M14 (KV readiness), §M9 (LAW cap)
- ADR-0016 / ADR-0017 — App Insights + the single-source public origin that unblocked this
- ADR-0012 / ADR-0018 — the health-gated rollout (false-positive source) + shared-env `count` guard
