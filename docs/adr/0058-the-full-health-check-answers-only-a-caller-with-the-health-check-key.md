# ADR-0058: The Full Health Check Answers Only a Caller With the Health Check Key

**Status:** Accepted
**Date:** 2026-09-24
**Decision-makers:** Solo maintainer (ticket #853)
**Related:** AuthSystem.API and TranslationSystem.API (`Health/`, `Settings/`, `Program.cs`),
Utilities/Hateoas.Abstractions (`HealthCheckHeaders`), `.github/workflows/health-ping.yml`,
`compose.hetzner.yaml`, `compose.prod.yaml`; ADR-0025 (DB-free probes, whose last accepted trade-off
this closes), ADR-0027 (the daily health ping), ADR-0054 (the same key shape for the frontend caller
key); tickets #409, #829, #853

## Context

Both APIs serve three health endpoints. `/health/live` and `/health/ready` run no checks (ADR-0025).
The full `/health` runs every check on every call: the TMS API queries its database, and the auth API
queries its database, opens a TCP connection to the Brevo SMTP relay and does a full AMQP handshake
with the broker. Caddy passes every path of both API domains to the app, and the full `/health` was
anonymous and outside every rate-limited group. So anyone could run all of that as often as they
liked. ADR-0025 accepted this as a trade-off and named the fix it would take: "auth on the deep
endpoint".

Code facts that shaped this decision:

- The daily health ping reads the full `/health` of both prod APIs from GitHub Actions (ADR-0027,
  #409). It is the only check that proves the databases are reachable, so the endpoint cannot simply
  be removed from the public origin.
- Nothing else reads it. The smoke test, the deploy, the Dockerfile `HEALTHCHECK` and the E2E suites
  read `/health/live` or `/health/ready`.
- The answer carried each check's `description` and `exception` text. The Npgsql check copies the
  exception message into its description, and the health check service does the same when a check
  throws. So a failed check could show a host, a port or a database user name, and the daily ping
  prints the body of a failed probe into the job log of a public repository.
- Closing `/health` alone does **not** stop someone from keeping the Neon database awake. The TMS API
  also has anonymous endpoints that read the database (`GET /api/v1/translations`,
  `GET /api/v1/progress`, `GET /api/v1/translation-files/{lang}`), and so does the auth API. One call
  every four minutes is enough, and no per-address limit stops that (ADR-0025 says the same). What only
  the full `/health` added was an unlimited fan-out: a database query, an SMTP connection and a broker
  handshake per call, plus the error text.
- The `production` GitHub environment requires a reviewer, so a secret scoped to it would hold every
  scheduled run of the daily ping until someone approved it.

## Decision

### 1. The full /health runs only for a caller with the key

Each API maps the full `/health` through `MapKeyGatedHealthChecks`, which builds the endpoint the same
way `MapHealthChecks` does, with `HealthCheckKeyMiddleware` in front of `HealthCheckMiddleware` inside
the one endpoint. A request that does not carry exactly one `X-LOTRO-Health-Key` header whose value
matches the configured key gets 404, and no check runs. The keys are compared as SHA-256 digests in
constant time, like the frontend caller key (ADR-0054). The endpoint is anonymous, because the key and
not a login admits the caller. `/health/live` and `/health/ready` stay open.

The gate lives inside the endpoint on purpose. Endpoint metadata plus a separate `app.Use…` would work
too, but the metadata is inert if someone removes or reorders the middleware, and the endpoint would
quietly open again.

### 2. One key per box, required outside Development and Testing

`HEALTH_CHECK_KEY` in the box `.env` feeds `HealthCheck__Key` on both APIs, like `FRONTEND_CALLER_KEY`.
Compose refuses to render without it, and `HealthCheckSettingsValidator` stops the boot outside
Development and Testing, or anywhere the key is shorter than 32 characters. In Development and Testing
an empty key leaves the gate open, so the local `curl …/health` and the integration suites keep
working. `scripts/gen-openiddict-keys` mints it for the parity stack.

### 3. The daily ping sends the prod key from a repository secret

`health-ping.yml` sends the key from the `PROD_HEALTH_CHECK_KEY` repository secret, to the two API
probes only. It is a repository secret because an environment secret on `production` would need an
approval on every scheduled run. The ping fails with a clear message when the secret is missing.

### 4. The answer names each check and its status, and nothing else

Both `HealthCheckResponseWriter`s write `status`, `totalDuration` and, per check, `name`, `status` and
`duration`. The description, the exception and the data are gone. The health check service already
logs every failed check at Error level with its exception, so the detail is in the application log.

## Consequences

### Positive

- A stranger can no longer make either API query its database, connect to Brevo or open a broker
  connection through `/health`, at any rate.
- The error text of a failed check no longer reaches the public job log of the daily ping, or anyone
  holding the key.
- The daily ping still proves the databases, the SMTP relay and the broker are reachable, once a day.

### Negative / Accepted Trade-offs

- **One more secret per box, and a GitHub secret that must match the prod box.** A mismatch turns the
  daily ping red with HTTP 404, which reads as "the key is wrong", not "the site is down". The runbook
  says so next to the key.
- **The answer is less useful by hand.** A key holder sees which check failed but not why; the why is
  in the application log (Loki). Accepted: the log is where the operator looks anyway, and the body is
  printed in public.
- **The 404 hides nothing.** On the auth API an unknown path also gets 404, but on the TMS API an
  unknown path gets 401 from the fallback policy, so the TMS 404 shows that `/health` exists. The repo
  is public, so the endpoint's existence was never a secret. The 404 only says "no report for you".
- **This does not stop a Neon keep-alive.** The other anonymous database readers remain. ADR-0025's
  reasoning stands: no per-address limit stops one call every four minutes.
- **A leaked key** lets its holder run the checks as often as they like, and nothing more. Rotate it
  like the frontend caller key: new value in the box `.env`, redeploy, and update the GitHub secret
  for prod.

## Alternatives Considered

### A. A small per-address rate limit on the full /health

Rejected. It needs no secret, but someone who rotates addresses still gets the SMTP connection and the
broker handshake at will, and the endpoint stays public. It fixes the flood from one address, not the
exposure.

### B. Block the full /health at Caddy and run the deep check from the box

Rejected. It closes the endpoint completely, but the daily ping would need its own path onto the box
(ssh, or a box cron with its own way to send the failure mail), and ADR-0027's "GitHub mails on a
failed run" would have to be rebuilt. That is more moving parts for the same result as a key.

### C. A normal login (a bearer token) on the full /health

Rejected. The ping would need a client and a token flow for two GET requests, and the TMS API would
depend on the auth API being up to report its own health.

## Implementation Notes

- Both APIs: `Health/HealthCheckKeyMiddleware.cs`, `Health/HealthCheckEndpointRouteBuilderExtensions.cs`,
  `Health/HealthCheckResponseWriter.cs`, `Settings/HealthCheckSettings.cs`,
  `Settings/HealthCheckSettingsValidator.cs`; registration in the TMS `Program.cs` and the auth
  `ApiDependencyInjection.cs`. The header name is `HealthCheckHeaders.Key` in
  `Utilities/LotroKoniecDev.Hateoas.Abstractions`, next to `FrontendCallerHeaders`.
- Ops: `compose.hetzner.yaml`, `compose.prod.yaml`, `.env.hetzner.example`, `.env.prod.example`,
  `scripts/gen-openiddict-keys.{sh,ps1}`, `scripts/up-prod.{sh,ps1}`, `.github/workflows/health-ping.yml`,
  `docs/deployment/runbook.md` (env matrix and "Secrets").
- Tests: `HealthCheckKeyMiddlewareTests`, `HealthCheckResponseWriterTests`,
  `HealthCheckSettingsValidatorTests` (unit, both APIs); `HealthEndpointsTests` (integration, both
  APIs: no key or a wrong key gets 404, the key runs the checks, the probes stay open, and on the auth
  API a failed check shows no error text).

## References

- Ticket #853 and its comments (the owner's choice between the three options); ADR-0025 (its last
  accepted trade-off named this fix); ADR-0027 (the daily ping); ADR-0054 (the key shape).
