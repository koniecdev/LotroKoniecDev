# ADR-0027: Prod buys its warm replica from a schedule, not a floor — and the availability probe moves out of Azure

**Status:** Accepted (ops-amended 2026-07-11 — #450 moved the daily ping from 03:00 to 06:40 UTC after observed GitHub cron delays; #449 suppressed the warm-up cold-start alert noise)
**Date:** 2026-07-09
**Decision-makers:** Solo maintainer
**Related:** ADR-0012 §5 R8 (**reversed** — prod `min_replicas = 1`), ADR-0019 (external synthetic
SLO probe — **web tests removed**), ADR-0020 (FinOps right-sizing — its "GitHub-Actions cron probe"
rejection is **overturned**; its staging scale-to-zero stands), ADR-0018 (staging shares the prod
ACA environment), ADR-0025 (DB-free readiness probes), ticket #409, TheKittySaver ADR-0015 (the
mirror decision on the other project sharing this subscription).

## Context

The subscription is an **Azure for Students** plan (~$100 for the year, one Container Apps
Environment). It carries **two** projects — LotroKoniecDev and TheKittySaver — each with a prod and
a staging environment. Both prods are links in the maintainer's CV while he is job-hunting.

Cost Management (`ActualCost`, MonthToDate), read on 2026-07-09 for **1–9 July 2026**:

| Meter | EUR (9 days) |
|---|---|
| Standard vCPU Active Usage | 25.06 |
| Standard Memory Active Usage | 6.28 |
| Standard Memory Idle Usage | 5.34 |
| Standard Web Test Execution | 3.42 |
| Standard vCPU Idle Usage | 2.66 |
| Alerts | 0.48 |
| **Total** | **43.35** |

| Resource group | EUR (9 days) |
|---|---|
| `rg-lotrotms-prod` | 19.29 |
| `rg-tks-prod` | 14.03 |
| `rg-tks-staging` | 8.04 |
| `rg-lotrotms-staging` | 1.98 |

That is **EUR 4.82/day** against roughly EUR 49 of remaining credit: about **ten days**. June's total
was EUR 0.23 — the whole platform came up on 1 July, so this is the first real bill, not a regression.

Three facts drive the decision:

1. **Six always-on prod replicas serve zero users.** `min_replicas = 1` × 3 apps × 2 projects. The
   product is pre-release; the only human who opens prod is a recruiter, and only during waking hours.
2. **The compute bills at the *active* rate, not the idle rate.** EUR 25.06 vCPU-active against EUR
   2.66 vCPU-idle. A replica with no users is still running health probes every 10–30 s and an OTel
   exporter, so it never sinks into ACA's cheaper idle billing. **Reducing replica-hours is the only
   lever we can rely on** — hoping for the idle rate is not a plan.
3. **The synthetic probes are self-defeating against scale-to-zero.** ADR-0019's three standard web
   tests hit the *public origins* every 15 minutes from three regions. A probe is a request; a request
   wakes a scaled-to-zero app. Keeping them would hold every app warm around the clock and cancel the
   entire saving, on top of their own EUR 3.42/9 days.

ADR-0012 R8 made prod always-warm for a real reason (a cold auth mid-smoke broke a rollout, and users
should not pay a cold start). ADR-0020 then explicitly rejected a GitHub-Actions cron probe in favour
of the web tests. **What has changed is that prod has no users and the credit has a deadline.** R8's
premise — "the warm replica removes cold starts for real users" — is currently vacuous, and ADR-0020's
rejection was reasoned against an always-warm prod, where a probe costs nothing but money.

## Decision

### 1. `min_replicas = 0` everywhere; prod's warm replica comes from a KEDA cron rule

`var.app_min_replicas` defaults to **0** (staging already was). A new nullable
`var.app_warm_window` object (`timezone` / `start` / `end`) renders a `custom_scale_rule` of type
`cron` on each app, with `desiredReplicas = "1"`. Prod's default window is
**07:00–22:00 Europe/Warsaw, daily** — the hours a recruiter opens the link. KEDA takes `max()` over
all scalers, so inside the window the cron rule is a *dynamic minimum* of one replica; outside it the
app returns to `min_replicas` after the platform's 300 s cool-down. Staging sets `app_warm_window = null`
and stays pure scale-to-zero (ADR-0020).

The window is one variable. Widen it, narrow it, or make it weekday-only (`0 8 * * 1-5`) in one line.

### 2. Every app declares an explicit `http_scale_rule`

Azure applies its implicit HTTP scale rule **only while no scale rule is declared** ("If you don't
create a scale rule, the default scale rule is applied"). Declaring the cron rule *replaces* it. An
app at zero replicas with only a cron rule would have no trigger to start on, and a request outside
the warm window would reach nothing — a silent nightly outage instead of a cold start. The explicit
`http_scale_rule` (`concurrent_requests = 10`) is the 0→1 activation; `max_replicas = 1` caps it.

**Off-hours is therefore a cold start, never an outage.** Measured on the live stack at 04:15 Warsaw,
all three apps at zero replicas:

| request | result |
|---|---|
| first hit on `https://lotro-translator.pl/` | **200 in 42.8 s** (TTFB 11.9 s) |
| same page while everything is warm | **200 in 0.21 s** |
| `auth` cold start alone | **31.4 s** |
| `tms` / frontend cold start alone | ~11 s |

The chain compounds: the frontend cold-starts, then blocks on OIDC discovery against a still-cold auth.
**Auth's ~31 s dominates**, so it — not the frontend — is what to optimise if this ever needs to be
faster. Warming only the frontend would buy nothing, which is why the cron rule covers all three apps.

### 3. The Azure web tests are deleted; a daily GitHub Actions cron replaces them

`.github/workflows/health-ping.yml` probes the three public origins once a day at 03:00 UTC (05:00
Warsaw in summer), on free GitHub-hosted minutes, and fails the run when any origin is unhealthy —
GitHub emails the last committer of the workflow file. Running *before* the warm window opens is
deliberate: the probe pays the day's first cold start, and a green run proves both platform health and
that scale-from-zero actually serves traffic.

The APIs are probed on **`/health`**, not `/health/ready`. ADR-0025 keeps the Postgres check out of the
readiness tag so ACA's probes cannot hold Neon awake, and names the full `/health` as the on-demand deep
check; once a day is precisely that occasion, and it is the only probe that proves the database is
reachable.

## Consequences

### Positive

- Projected run rate **EUR 4.82/day → ~EUR 2.4/day** (~EUR 145 → ~EUR 72 per month), roughly half:
  prod compute −37.5% (15 warm hours of 24), TheKittySaver staging to zero, web tests to zero.
  The remaining credit stretches from ~10 days to the end of the month.
- Prod is fast exactly when it is looked at, and reachable — slowly — when it is not.
- The daily probe is strictly *more* honest than the web tests it replaces: it exercises the deep
  `/health` (database included), which `/health/ready` deliberately does not.
- The cost knob is a Terraform variable, not a code change.

### Negative / Accepted Trade-offs

- **Outage detection latency goes from ~20 minutes to ~24 hours.** Accepted: zero users, and the
  5xx / restart / log-spike metric alerts still fire on their own cadences for serving-path failures.
- **A visitor outside 07:00–22:00 waits ~43 s for the first page** (measured, above) — noticeably worse
  than the 10–30 s this ADR first assumed before the stack was measured. This is the explicit price of
  the credit lasting the month. If it ever costs a real opportunity, the window is one line, and auth's
  31 s cold start is the thing to attack.
- **The SSL-expiry guard and the App Insights availability record are lost** with the web tests. The
  ACA managed certificate auto-renews; losing the guard is a real, accepted reduction in cover.
- **A red morning run can mean "Brevo is down", not "the site is down"** — auth's deep `/health`
  includes SMTP. The failing check's name is printed in the job summary.
- **The daily deep probe wakes Neon once**, against ADR-0025's spirit. One wake per day is noise next
  to the compute cap.
- **GitHub disables scheduled workflows in repositories with 60 days of no activity.** Both repos are
  active; if that ever changes, the probe silently stops.

### Operational note — a Terraform apply alone does NOT take effect

The apps run `revision_mode = "Multiple"` with `ingress[0].traffic_weight` under
`lifecycle.ignore_changes` (ADR-0012 §5). Scale rules live in `template`, so changing them mints a
**new revision that receives 0% of traffic**, while the old revision keeps serving *and keeps its old
`min_replicas`*. Terraform will not move traffic. Every change to this file therefore only lands once
the rollout shifts traffic to the new revision and deactivates the previous one — which `deploy.yml`
does on a normal deploy, and which must be done by hand after an out-of-band `terraform apply`.

## Alternatives Considered

- **Keep prod always warm (status quo, R8).** Rejected: EUR ~145/month against ~EUR 49 of credit — the
  sites would go dark mid-month, which is worse for a CV link than a nightly cold start.
- **Keep the web tests at a lower cadence.** Rejected as *incoherent*, not merely expensive: any
  external probe of the public origin wakes the app it is watching. A probe and a scale-to-zero app
  cannot coexist. This is what ADR-0020 could not have foreseen while prod was always warm.
- **Weekday-only window (`0 8 * * 1-5`).** Rejected for now: a recruiter clicking on Sunday evening is
  plausible, and the extra saving (~EUR 15/month) does not buy that risk. It is the next knob if the
  credit runs short — a one-line change to `app_warm_window`.
- **Take one prod down (it is two portfolio projects).** Rejected: both are CV links.
- **Scale prod to zero with no window at all.** Rejected: it makes every recruiter visit a cold start,
  the one outcome the maintainer explicitly ruled out.
- **Lengthen the health-probe intervals so replicas fall into ACA's idle billing.** Rejected as
  unverified: the active/idle split above suggests it would not work, and it would slow crash detection.
  Fewer replica-hours is the lever we can prove.

## Implementation Notes

- `iac/vars.tf` — `app_min_replicas` default 1 → 0; new nullable `app_warm_window` object with cron
  shape validation.
- `iac/azure-container-apps.tf` — all three apps gain an explicit `http_scale_rule` plus a
  `dynamic "custom_scale_rule"` (type `cron`) rendered only when `app_warm_window != null`.
- `iac/env/staging.tfvars` — `app_warm_window = null`.
- `iac/monitoring.tf` — `azurerm_application_insights_standard_web_test.availability` and
  `azurerm_monitor_metric_alert.availability` deleted, with their locals; the App Insights component,
  the auth-latency alert and every metric/log alert stay.
- `.github/workflows/health-ping.yml` — new.
- `docs/deployment/runbook.md` — warm window + the new probe documented.
- TheKittySaver receives the mirror change (its ADR-0015 / issue #281); the two projects share this
  subscription, and fixing only one of them fixes nothing.
