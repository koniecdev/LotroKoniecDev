# ADR-0020: FinOps right-sizing — staging scales to zero, SLO probe cadence drops to 15 min

**Status:** Accepted
**Date:** 2026-07-01
**Decision-makers:** Solo maintainer
**Related:** ADR-0012 §5 follow-up (min_replicas 0→1, R8), ADR-0018 (staging in the shared prod
environment), ADR-0019 §2 (probe cadence + geo quorum); DevOps/FinOps audit 2026-07-01

## Context

The subscription is an **Azure student plan** (~$100/year credit, one Container App Environment,
no room for slack). A cost pass over the running infra found the two dominant line items are not
the product at all:

1. **Synthetic availability probes (ADR-0019): ≈ $47/month.** Standard web tests bill **$0.0006
   per execution**; 3 tests × 3 locations × every 300 s ≈ 77.8k executions/month. ADR-0019 §
   trade-offs noted the per-execution billing but assessed it against LAW ingestion, not against
   the execution meter — at this cadence the probes alone out-bill the entire ACA compute.
2. **Staging compute: ≈ $15–18/month of pure idle.** Staging runs the same `min_replicas = 1` as
   prod (three always-warm 0.25 vCPU / 0.5 GiB replicas), but staging has **no steady traffic by
   construction**: it creates no monitoring/web tests (ADR-0018/M6-22 — it inherits prod's, which
   probe prod origins), has no users, and is exercised only by CD rollouts and occasional manual QA.

Prod's `min_replicas = 1` (ADR-0012 R8) is **not** in question: the prod origins are probed
continuously and the warm replica removes cold starts for real users; the first-rollout defect that
motivated R8 (cross-revision token validation cold-starting auth mid-smoke) was real.

## Decision

### 1. Staging scales to zero (`app_min_replicas` variable, prod default 1)

`iac/vars.tf` gains `app_min_replicas` (default **1** — prod unchanged, R8 stands);
`env/staging.tfvars` sets **0**. `max_replicas` stays 1 everywhere.

The health-gated rollout remains valid at min 0 **by its own existing design**: `deploy.yml`'s
readiness step polls each 0%-traffic candidate's label FQDN (60×10 s budget — the poll IS the
scale-from-zero warm-up) and explicitly warms the public auth origin before smoke — the "cheap
insurance" ADR-0012 kept after R8 becomes the load-bearing mechanism on staging. ACA's default HTTP
scale rule wakes a scaled-to-zero revision on the first request, including label-FQDN requests.

### 2. Probe cadence 300 s → 900 s; alert window 5 min → 30 min

All three web tests drop to **900 s** (≈ $15.6/month, and 3× less probe-generated request
telemetry). Geo quorum (2-of-3), locations, SSL-expiry validation and severities are unchanged.

The availability alert's `window_size` widens **PT5M → PT30M as a correctness prerequisite**: at a
900 s cadence a 5-minute window often holds ≤1 result *total*, so the 2-location quorum could never
be met and the alert would go permanently silent. PT30M holds ~2 results per location.

**The knob when users arrive:** flip auth (the Sev0 SPOF) back to 300 s (+$10/month) and leave
tms/frontend at 900 s.

## Consequences

### Positive

- ≈ **$46–49/month cut** (probes ~$31 + staging idle ~$15–18) with zero change to prod serving
  behavior — on this subscription that is the difference between the credit lasting weeks and
  months.
- Staging becomes pay-per-use: billed only during rollouts and QA sessions.
- The availability alert is now provably able to fire at the configured cadence (window ≥ quorum
  math), which the previous PT5M window silently was not guaranteed to do at ANY sub-5-min gap in
  probe scheduling.

### Negative / Accepted Trade-offs

- **Worst-case outage detection moves to ~15–30 min** (probe gap + window). Accepted pre-release
  with zero users; the 5xx/restart/log-spike alerts still react on their own cadences for
  serving-path failures.
- **First staging request after idle pays a cold start** (~10–20 s incl. Neon wake). Accepted for
  QA; the deploy pipeline absorbs it in its readiness budget.
- **Staging in-process background work would stop while scaled to zero.** None exists today (no
  hosted services in tms-api); if the forum watcher lands as a hosted service, staging simply won't
  poll while asleep — acceptable (prod polls), revisit if staging ever needs it.

## Alternatives Considered

- **Free GitHub-Actions cron probe instead of web tests** — rejected: best-effort scheduling (no
  cadence guarantee), single vantage point, loses the App Insights availability record + SSL-expiry
  validation ADR-0019 chose deliberately.
- **Keep 300 s for auth only now** — rejected for now: with zero users the +$10/month buys
  detection latency nobody experiences; recorded as the explicit first knob to flip.
- **Drop the tms/frontend tests entirely** — rejected: each origin has its own DNS/cert/ingress
  path (and its own managed cert to expiry-guard).
- **Tear staging down between QA sessions (`terraform destroy`)** — rejected: ops churn and
  bring-up risk for roughly the same savings scale-to-zero delivers automatically.

## Implementation Notes

- `iac/vars.tf` — new `app_min_replicas` (validated 0..1); `iac/azure-container-apps.tf` — the
  three apps use it; `iac/env/staging.tfvars` — sets 0.
- `iac/monitoring.tf` — web-test `frequency = 900`, availability alert `window_size = "PT30M"`,
  cost math in comments.
- `.github/workflows/deploy.yml` — readiness comment updated (staging polls are the warm-up).
- Amendment notes added to ADR-0012 (§5 follow-up) and ADR-0019 (§2, cost trade-off).
