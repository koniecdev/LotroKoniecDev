# ADR-0025: DB-free readiness probes — health checks must not keep the scale-to-zero database awake

**Status:** Accepted
**Date:** 2026-07-05
**Decision-makers:** Solo maintainer
**Related:** ADR-0012 (health-gated rollout; prod `min_replicas = 1`), ADR-0014 (Neon adoption),
ADR-0019 (external availability web tests target `/health/ready` — semantics narrowed here),
ADR-0020 (monitoring cost cadence), ticket #346, MIGR-01 / #336 + PR #344 (the Neon audit that
surfaced the burn), `iac/azure-container-apps.tf` (probe wiring),
`TranslationSystem.API/Program.cs` + `AuthSystem.API/Program.cs` (health registrations).

## Context

Live Neon consumption read via the API on 2026-07-05 (billing period started 2026-07-01):

| project | compute used | wall-clock active | verdict |
|---|---|---|---|
| prod | **44.80 of 100 CU-h/mo** after 4.65 days | 109.8 h of ~111.6 h possible (**~98% awake**) | **Free-plan cap hit ~2026-07-10** |
| staging | 10.85 CU-h | 41.8 h | fits (~72 CU-h projected) |

The mechanism: the ACA readiness probes poll `/health/ready` every few seconds on the always-on
prod replicas (`min_replicas = 1`, ADR-0012 R8). Both APIs tagged their Npgsql health check
`"ready"`, so every probe pinged Postgres — and the single Neon compute per branch (it serves both
`lotro_translation` and `lotro_auth`) never saw the 5 idle minutes its autosuspend requires.

The math leaves no middle ground: a permanently-awake compute at the 0.25 CU floor is
182 CU-h/month against the Free plan's 100. **On a scale-to-zero database, only a database that
actually sleeps fits the plan** — and when the cap hits, Neon hard-stops the compute until the
next billing month: 503s on every DB path, availability alerts firing, and every deploy red
(migrator + smoke against a stopped database).

The deeper point is not the quota: *readiness-pings-the-database is the wrong pattern on
scale-to-zero Postgres*. A suspended database is normal operation, not unreadiness — and the probe
itself is what prevents the suspend. The probe defeats the platform feature it shares a box with.

## Decision

1. **The Npgsql health check loses its `"ready"` tag in both APIs.** The ACA-probed
   `/health/ready` runs zero checks (the tag predicate matches nothing) and returns 200 as soon as
   the app serves HTTP — exactly what a container probe should assert, and DB-free.
2. **The full `/health` runs every check on demand** — db (+ smtp on auth). AuthSystem.API gains
   the endpoint (it only had `/live` + `/ready`), reaching parity with TranslationSystem.API. This
   is the operator's deep-diagnostics endpoint; none of our infrastructure polls it.
3. **The database is proven where real proof lives:** the deploy smoke exercises both databases
   through real endpoints on every rollout (leg 2 — token issuance hits the auth DB; leg 4 — the
   translation-file GET hits the TMS DB), and at runtime a DB outage surfaces as request 5xx in
   telemetry. Integration tests pin the new contract in both suites (`/health/ready` carries no db
   entry; `/health` does).
4. **The availability web tests keep targeting `/health/ready`** (ADR-0019). Their meaning
   narrows: they now assert HTTP + TLS liveness of the public origin, not database health.
   Accepted — with the probes DB-free they also stop waking the database every 15 minutes.

## Consequences

- The Neon compute can finally autosuspend; at today's zero traffic the burn drops from
  ~9.6 CU-h/day to near zero, and the Free plan holds with a wide margin.
- Readiness no longer pulls an app with a dead DB out of ingress rotation. With a single replica
  this loses nothing — out-of-rotation also serves the user a 503, just from the ingress instead
  of the app — while per-request failures and the Sev alerts carry the actual signal.
- A candidate revision with a broken connection string now passes readiness during a rollout; the
  smoke gate (legs 2/4) catches it before promote, and the existing rollback path handles it.
- The first request after an idle spell pays the Neon cold start (~0.5–1 s). Acceptable at zero
  users; if that ever violates an SLO, the knob is a paid always-on compute or a scheduled warmer —
  never re-tagging the probe.
- `Down()`-style regression risk is pinned by tests: re-tagging the db check `"ready"` fails both
  integration suites.
- The full `/health` is public and unthrottled (mapped outside the rate-limited group in both
  APIs), and each hit pings the DB (+ an SMTP TCP connect on auth) — an external scanner or a
  deliberate slow poller can keep Neon awake through it. Accepted: the TMS `/health` carried the
  identical exposure before this ADR, and no rate limit stops a one-request-per-4-minutes
  keep-awake anyway. If it ever shows in the consumption numbers, the knob is auth on the deep
  endpoint (or dropping it from the public ingress) — not re-tagging the probe.

## Alternatives Considered

- **TTL-cached DB check inside readiness.** Still wakes the database once per TTL: with the 5-min
  suspend window, a 10-min TTL keeps it ~50% awake (~90 CU-h/mo), a 30-min TTL ~17% (~31 CU-h) —
  material burn for a signal that is by then up to 30 min stale, plus custom caching code. Rejected.
- **Upgrade Neon to Launch and keep the pattern.** Pays real money (~$30/month at the observed
  burn) for an idle database kept awake by a probe — a bug tax, not a fix. A plan upgrade stays a
  valid future move for its own reasons (7-day PITR, storage headroom), never as the probe fix.
- **Azure Database for PostgreSQL Flexible Server.** ~$15–18/month flat, erases the scale-to-zero
  economics and the entire Neon branch/PITR machinery this repo just built and verified
  (ADR-0023/0024 proofs, the MIGR-01 runbook, MIGR-04 snapshots). Revisit only with sustained
  24/7 traffic or hard private-networking needs.
- **`min_replicas = 0` for the prod apps.** Fixes nothing (probes run whenever a replica exists),
  adds whole-app cold starts for users, and contradicts ADR-0012 R8. Rejected.

## Implementation Notes

- `TranslationSystem.API/Program.cs`, `AuthSystem.API/Program.cs` — tag change + the why-comment;
  auth additionally maps the full `/health`.
- `AuthSystemApiFactory` points SMTP at a deliberately dead port so the full-`/health` test is
  deterministic (a locally running mailpit on :1025 must not flip it).
- Verification after the fix reaches prod: a next-day `GET /projects/{id}` must show
  `active_time_seconds` decoupled from wall-clock (the compute sleeps between requests).

## References

- Neon autosuspend / compute lifecycle: https://neon.com/docs/introduction/auto-suspend
- Ticket #346; consumption numbers read live via `GET /api/v2/projects/{id}` on 2026-07-05.
