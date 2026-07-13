# ADR-0029: Exactly one active revision per app — the rollout sweeps superseded revisions, rollback pays a cold start

**Status:** **Obsolete by platform** (ADR-0034, 2026-07-13 / #492 — ACA revisions no longer exist, so there is nothing to sweep and no 0%-traffic candidate to keep warm. The Hetzner rollout recreates containers in place; a rollback is "redeploy the previous image tag". The *concern* this ADR encoded — an orphaned revision quietly burning compute — is what the flat-price box removes structurally.) Previously: Accepted
**Date:** 2026-07-09
**Decision-makers:** Solo maintainer
**Related:** deployment pipeline (`.github/workflows/deploy.yml`, `cd.yml`, `infra.yml`), ticket #407 (CD-01),
ADR-0012 §5 (amended — the H7 "previous revision is kept" clause), ADR-0025 (DB-free readiness probes),
ADR-0027 (scheduled warm window)

## Context

On 2026-07-09 the prod Neon project stood at 86.03 of its 100 CU-hour monthly allowance, 9 days into
the period, having never suspended once. The compute was held awake by a revision serving **0% of
traffic**: `lotrotms-tms-api-prod--0000016`, created by `terraform apply` on 2026-06-30, whose
template still carried `min_replicas = 1` and whose image (built at `cd498a2`) still tagged the
Postgres health check `ready` — so ACA's readiness probe queried Postgres every 30 s for 8 days and
13 hours from a replica no user could reach. The ADR-0025 fix (untag the DB check) deployed to prod
on 2026-07-05 and changed nothing for four days, because the old revision kept probing alongside the
new one. "Is the fix on prod?" answered *yes* while the defect ran on.

Code facts that constrain the choice:

- The rollout **already deactivates a previous revision** — since #258,
  `deploy.yml`'s promote step runs `az containerapp revision deactivate --revision "$prev"`. But it
  targets exactly **one** revision, resolved from the highest traffic weight, and the call is
  `|| true`. Two failure modes follow: revisions that never enter the traffic config are never
  matched (Terraform creates its revisions with 0% traffic — `--0000016` was invisible to it), and a
  failed deactivation is silently swallowed (the 2026-07-05 deploy left `--ccd498a2…` active despite
  the deactivate line, and nothing noticed).
- **Two independent mechanisms mint revisions** and neither cleans up the other's: `deploy.yml`
  (image by digest, suffix `c<sha>-<run>-<attempt>`) and `terraform apply` (image by tag,
  auto-numbered). Any durable fix must match on *"not the promoted revision"*, never on a name
  pattern.
- `cd.yml:29` states the design assumption: *"The previous revision is kept (0% traffic, scales to
  zero) so any failure rolls straight back to it."* Both halves are false — promote deactivates it,
  and on prod nothing at 0% traffic reliably scaled to zero. This comment is what hid the defect.
- **ADR-0027 changed the price, not the leak.** `min_replicas` is now 0 everywhere, but the prod
  warm window is a KEDA cron rule with `desiredReplicas = 1` — and scale rules apply **per active
  revision**, so every leaked active revision still buys a warm replica 15 hours a day. ADR-0025
  closed the Neon-burn leg (probes no longer touch Postgres); the ACA replica-hours leg stays open
  until leaked revisions stop existing.
- The in-job auto-rollback already handles a deactivated `prev`: it runs
  `revision activate` before steering traffic back (`deploy.yml`, the `Roll back on failure` step).
  Only the *post-job* rollback story still assumes a warm, still-present previous revision
  (runbook: "instant manual revert").
- `infra.yml`'s prod apply and `deploy.yml` share the `prod-mutation` concurrency group, so a
  Terraform apply cannot interleave mid-rollout — it can only create an orphan *between* rollouts.

## Decision

### 1. The invariant: one active revision per app, holding 100% of traffic

After a completed rollout, each of the three apps has **exactly one active revision** — the promoted
candidate — at weight 100. Every other revision is deactivated, whoever created it. Deactivated
revisions remain in ACA's revision history and cost nothing.

### 2. Promote sweeps every non-promoted revision, matched by exclusion

The promote step replaces the single `deactivate $prev` with a sweep: list all active revisions of
the app and deactivate each one whose name is not the promoted candidate. No name patterns — the
match is *"not the one we just promoted"*, so Terraform-minted revisions and previously-leaked ones
are reaped on the next deploy regardless of origin. Each deactivation stays best-effort (a cleanup
hiccup must not fail a healthy deploy mid-step); loudness is the assertion's job.

### 3. A loud post-rollout assertion, ordered so it can never revert a healthy deploy

A new final step asserts the §1 invariant per app — exactly one active revision, name equal to the
candidate, traffic weight 100 — and **fails the pipeline** on any violation. It is placed *after*
the `Roll back on failure` step in step order: when the assertion is reached, rollback has already
been evaluated (and skipped), so an assertion failure reddens the run without touching traffic — a
leftover active revision is a cost defect, not a serving defect, and the promoted revision has
already passed both smokes. This is the regression guard the incident lacked: the silent
`|| true` deactivation failure of 2026-07-05 would have failed the deploy red instead of hiding for
four days.

### 4. Rollback after the job is `revision activate` + traffic shift — one cold start, accepted

The H7 premise "keep the previous revision for instant rollback" is retired (this amends
ADR-0012 §5). In-job auto-rollback is unchanged — activate `prev`, steer traffic back, deactivate
the candidate. After the job ends, rolling back means: `az containerapp revision activate` on the
previous revision, then `az containerapp ingress traffic set` — one cold start (~43 s worst case,
measured in ADR-0027), the same price ADR-0027 already accepted for any off-window visitor. Paying a
warm replica 15 h/day to keep an instant-rollback path that has never been used post-smoke is
exactly the economics ADR-0027 rejected.

### 5. Terraform orphans are bounded, not prevented

An out-of-band `terraform apply` still mints a 0%-traffic revision by design (ADR-0027 operational
note); the operator shifts traffic and deactivates by hand per the runbook. If that step is
forgotten, the orphan lives **at most until the next deploy** — the §2 sweep reaps it — and holds a
replica only inside the warm window while it lives. The shared `prod-mutation` lock guarantees no
apply can slip a revision in between the sweep and the assertion.

## Consequences

### Positive

- The leak class is closed: no superseded or Terraform-minted revision can hold a warm replica past
  the next deploy, and none can hold one silently — the assertion is red otherwise.
- "Deployed but inert behind a stale revision" (the ADR-0025 fix's four dark days) can no longer
  happen quietly: the 2026-07-05 deploy would have failed its assertion.
- The runbook rollback path becomes honest — it documents what the pipeline actually leaves behind.
- Staging gets the same sweep and assertion for free (same reusable workflow), keeping both
  environments' revision lists clean.

### Negative / Accepted Trade-offs

- **Post-job rollback costs a cold start** instead of being instant. Accepted: same price as any
  off-window request under ADR-0027, and the health-gated rollout makes post-smoke rollbacks rare.
- A transient ARM read failure in the assertion can redden a rollout whose traffic is correctly
  placed. Accepted: the step prints per-app state, so the operator verifies in one `az` call; a
  re-run re-deploys the same digest idempotently.
- Three extra `az` reads per app per rollout. Negligible.

## Alternatives Considered

### A. Keep the previous revision active for instant rollback (the H7 comment's design)

Rejected. Under the warm window every active revision is a paid replica 15 h/day; ADR-0027 already
decided cold starts are acceptable when the alternative is burning the credit. The incident shows
the "kept" revision also keeps its *old* template and image — probing with yesterday's bugs.

### B. Alert on a second active revision instead of failing the pipeline

Rejected. On a solo project an alert is just a slower pipeline failure. The deploy job already holds
the OIDC identity and the candidate names; the assertion is a few lines in the one place that knows
the expected state — the ticket's "cheapest form".

### C. Sweep in `infra.yml` after `terraform apply` too

Rejected (YAGNI). The apply deliberately leaves its new revision at 0% for the operator to promote
(ADR-0027 operational note) — an automatic sweep there would deactivate the revision the apply just
created or, worse, guess at promotion. The mutation lock plus the next deploy's sweep already bound
an orphan's lifetime.

### D. Match CD-created revisions by name pattern (`--c*`)

Rejected. The costly orphan of the incident was `--0000016` — a Terraform name. Exclusion-matching
against the promoted candidate is the only rule that survives a new revision creator.

## Implementation Notes

- `.github/workflows/deploy.yml` — promote step: sweep replaces the single `deactivate $prev`; new
  `Assert exactly one active revision per app` step after `Roll back on failure`; stale
  `min_replicas=1` comments refreshed to the ADR-0027 world.
- `.github/workflows/cd.yml` — header comment rewritten: superseded revisions are deactivated and
  asserted away, not "kept, scales to zero".
- `docs/deployment/runbook.md` — rollback path (activate + shift + cold-start caveat), pipeline
  table row, out-of-band-apply note cross-referenced to the sweep.
- `docs/adr/0012-continuous-deployment-pipeline.md` — §5 blockquote noting this amendment.

## References

- Ticket #407 (CD-01) — incident evidence: Azure CLI + Neon API forensics, 2026-07-09
- ADR-0012 §5 — the H7 health-gated rollout this ADR amends
- ADR-0025 — DB-free readiness probes (the fix that ran dark behind the leaked revision)
- ADR-0027 — scheduled warm window; the economics this rollback strategy inherits
- ADR-0023 — forward-only migrations (why rolling back *code* is always schema-safe)
