# ADR-0008: Cloud-agnostic deployment & environment strategy (staging + production)

**Status:** Accepted (its "no IaC / human-driven first deploy / provider undecided" stance superseded by ADR-0012, 2026-06-28; §6 migration wording amended 2026-07-05 by ADR-0023/#337 — see the in-body note)
**Date:** 2026-06-19
**Decision-makers:** Solo maintainer
**Related:** ADR-0002 (TMS pivot + KittySaver 1:1 lift), ADR-0005 (Frontend Data Protection key
persistence), ADR-0006 (Frontend not containerized in dev compose — production hosting + reverse
proxy / forwarded-headers **explicitly deferred there**, taken up here), spec 0001 (game-update
lifecycle — bootstrap/distribution paths), `compose.yaml`, the four multi-stage Dockerfiles
(auth/tms/frontend/migrator), milestone **M6** tickets #166–#178.

## Context

The TMS reaches the point where it must run on a cloud container host across **two environments —
staging + production**. There are no production users yet (pre-release), so breaking changes remain
free; the cost we are buying down is operational, not migrational.

Two constraints from the maintainer shape everything:

- **The provider is not chosen.** Azure Container Apps and AWS (ECS Fargate / App Runner) are both
  on the table. Existing cloud familiarity is **Azure App Service + Blob only**, and there is an
  explicit refusal to commit AI-generated IaC that would be operated without understanding it.
  Neutrality and a **human-driven** first deploy are therefore hard requirements, not preferences.
- **ADR-0006 already deferred this exact work.** It kept the dev stack backend-only and recorded
  that "production hosting (Azure Container Apps) and its public-origin/reverse-proxy topology are
  explicitly out of scope now," that the Frontend's containerized runtime path is "**only**
  exercised in production … its first real container run will surface origin/forwarded-headers
  work," and (Alternative C) that a reverse-proxy + `ForwardedHeaders` shape "is the shape
  production (ACA) will likely take, and is deferred to that decision." **This is that decision.**

Code facts that constrain and enable the choice (the floor is already high):

- Multi-stage Dockerfiles exist for auth-api, tms-api, frontend, migrator — non-root (`USER app`),
  `HEALTHCHECK`, dual `ASPNETCORE_URLS` (`https://+:8081;http://+:8080`).
- Health endpoints exist: `/health/live` (no checks) + `/health/ready` (Npgsql, plus SMTP on auth)
  on both APIs; tms also exposes anonymous `/health`.
- Telemetry is already vendor-neutral: Serilog → OTLP and OpenTelemetry `UseOtlpExporter`, endpoint
  from `OTEL_EXPORTER_OTLP_ENDPOINT`.
- Secrets are already env-injected: OpenIddict production keys and both connection strings come from
  the environment (`.env.example` documents them); Development uses ephemeral OpenIddict keys.
- The Frontend Data Protection keyring is already env-configurable with a non-Development fail-fast
  guard and a `/keys` mount in its Dockerfile (ADR-0005).

Gaps that will break a first cloud bring-up (the work this milestone closes):

- **No `UseForwardedHeaders`** in any of the three web apps — behind a TLS-terminating ingress the
  request scheme reads `http`, so OpenIddict `iss`, OIDC `redirect_uri`, and `Secure` cookies go
  wrong and `UseHttpsRedirection` can loop.
- **CORS origin hardcoded** `https://lotro.koniec.dev` in both APIs' `Program.cs` — no staging
  origin, not configurable.
- **auth-api does not persist its Data Protection keyring** (Identity login cookie, Razor
  antiforgery, password-reset tokens) — ephemeral FS rotates keys on every restart / per replica.
- TMS base `appsettings.json` bakes `https://auth.lotro.koniec.dev`; no CD publishes images; the
  migrator is an SDK-image `dotnet ef database update` (dev-flavored).

## Decision

### 1. Two environments; the deployment unit is an OCI image

staging + production. The only build artifact is the set of OCI container images already produced
by the existing Dockerfiles. Anything that can run an OCI image behind an HTTP ingress can run this
system; nothing else is assumed.

### 2. The neutrality contract (explicit, testable rules — no slice may break them)

- **All runtime configuration via environment variables (12-factor).** No `appsettings.*` carries
  an environment-specific URL or secret (cleaned up in M6-06).
- **No cloud-provider SDK or package** is referenced by any project. Telemetry leaves only via
  OTLP; secrets arrive only via env; persistent storage is a generic mounted filesystem path; the
  database is only a connection string.
- **A fixed container contract:** HTTP on `:8080` (plus `:8081` TLS where a cert is mounted),
  `/health/live` + `/health/ready`, non-root, structured JSON logs to stdout.
- **Provider-specific artifacts (a deploy walkthrough, an IaC template) live only under `docs/` and
  are produced only after the provider is chosen** — never referenced by the build or app code.

### 3. Configuration is env-driven and fails fast

Every deployment-critical setting is validated at startup in non-Development; a missing or invalid
value aborts boot with a message naming the key (M6-05). This is the primary defense against the
"cryptic 500 on staging" failure mode. CORS origins (M6-03), forwarded-headers config (M6-02), and
Data Protection keyrings (M6-04) all move under this regime.

### 4. Production parity is reproduced locally via `compose.prod.yaml`

A **separate** prod-like stack — `ASPNETCORE_ENVIRONMENT=Production`, real OpenIddict keys, DP
keyring volumes, the containerized Frontend, a TLS-terminating reverse proxy, and a
self-hosted-or-managed Postgres — runs on a laptop (M6-07 / M6-08). The dev `compose.yaml` is left
untouched; ADR-0006's host-run-Frontend dev posture still holds for development. Differences between
"works on my machine" and "works on staging" are caught locally **before** they cost a staging
debugging session. This is the milestone's core promise.

### 5. Postgres is provider-agnostic

The application knows only a connection string and an SSL mode, from env. `compose.prod.yaml` ships
a self-hosted Postgres (named volume) as the default so the parity stack is self-contained;
pointing at a managed instance (Azure Database for PostgreSQL / AWS RDS / Aiven) is a single env
change. No HA/backup topology is committed now — that belongs to the deferred provider decision.

> **Superseded (2026-07-05, ADR-0023 / #337):** the provider decision has since landed — prod and
> staging run on **Neon** (ADR-0014, ADR-0018), whose history retention (PITR) + instant branching
> is the de-facto backup topology; MIGR-01 (#336) verifies it and documents the recovery procedure
> in the runbook.

### 6. Images are published to GHCR; migrations run as a pre-deploy job

CD builds and pushes the four images to **GHCR**, tagged by commit SHA + semver (M6-09). Schema
changes apply via `dotnet ef migrations bundle` executed as a pre-deploy job / init step that the
APIs depend on; a failed migration blocks API startup, so there is never half-migrated serving
(M6-10). The dev compose migrator is kept as-is.

> **Amendment (2026-07-05, ADR-0023 / #337):** later docs (ADR-0012 §4/§5, ADR-0014, the runbook)
> cite this section for a "forward-only" migration discipline it never actually defined — it only
> specifies the pre-deploy bundle job. That contract (forward-only, N-1 backward-compatible,
> expand → backfill → contract, plus its enforcement and recovery net) is now formally
> **ADR-0023**.

### 7. The Frontend is containerized for production only

Per ADR-0006 the Frontend stays out of the **dev** compose, but it **is** containerized in
`compose.prod.yaml` and in production, behind the same forwarded-headers + DP-keyring regime as the
APIs. This is precisely the containerized-RP-behind-a-reverse-proxy shape ADR-0006 Alternative C
parked for "the production decision."

### 8. The provider is deliberately undecided; the first deploy is human-driven

We do **not** pick Azure vs AWS now and we do **not** generate IaC. M6-12 produces a neutral
"platform requirements + Azure⇄AWS service mapping" document so the choice is informed; the actual
bring-up is a manual runbook (M6-11) plus a provider-specific walkthrough authored only after the
choice. A post-deploy smoke test (M6-13) gives a green/red signal per environment.

## Consequences

### Positive

- One mental model — "a 12-factor OCI image behind a TLS ingress" — runs identically on Azure
  Container Apps, AWS ECS/App Runner, or a plain Docker host. The provider choice stays cheap and
  reversible because nothing is coupled to it.
- The real production topology (forwarded headers, DP persistence, containerized Frontend, TLS
  ingress, migration job) is exercised on a laptop before any cloud bill or staging triage.
- The forwarded-headers / containerized-RP-origin work ADR-0006 deferred is finally addressed —
  neutrally, and validated end-to-end by the reverse proxy in `compose.prod.yaml`.
- The high existing floor (Dockerfiles, health, OTLP, env-injected secrets, frontend DP) is reused;
  M6 is mostly closing specific gaps, not rebuilding.

### Negative / Accepted Trade-offs

- **No one-click deploy.** The first cloud bring-up is manual by design — the maintainer wants to
  learn the platform, not inherit opaque IaC. Mitigated by the runbook (M6-11) and smoke test
  (M6-13).
- Self-hosted Postgres in `compose.prod.yaml` is **not** production-grade (no HA/backups). It is a
  parity tool; real production is expected to use a managed database (swap = one env var).
- Maintaining `compose.prod.yaml` next to `compose.yaml` is a small duplication cost; accepted
  because dev simplicity (ADR-0006) and production parity have genuinely different needs.
- Cross-provider neutrality forgoes provider-native conveniences (ACR managed-identity pull, Key
  Vault references, IAM-scoped RDS auth) until a provider is chosen.

## Alternatives Considered

### A. App-readiness + prod-parity compose + manual deploy (this ADR)

Chosen. Maximum neutrality, lowest commitment, exercises the production topology locally, and
matches the maintainer's "no blind IaC" constraint head-on.

### B. Commit to Azure Container Apps now (Bicep / `az` + ACR + Key Vault)

Rejected for now. Fastest path to a running staging, but couples the repo to Azure before the
provider is chosen and asks the maintainer to operate IaC they do not yet understand — against an
explicit constraint. The neutral requirements doc (M6-12) keeps this option fully open for later.

### C. Full multi-cloud IaC in Terraform up front

Rejected. Best infra-layer neutrality, but a large amount of code and maintenance for zero present
users and an undecided provider — premature under the repo's YAGNI rule.

### D. Treat the dev compose / dev migrator as "production"

Rejected. The dev stack uses ephemeral OpenIddict keys, mailpit, an SDK migrator image, no
forwarded headers, and no DP persistence — every one of those is a staging failure in waiting.
Parity requires a distinct production stack (§4).

## Implementation Notes

- **New:** `compose.prod.yaml` + `.env.prod.example` (M6-07); `.github/workflows/cd.yml` (M6-09);
  `docs/deployment/` runbook + `target-requirements.md` + Azure⇄AWS mapping (M6-11/M6-12);
  `scripts/smoke.{sh,ps1}` (M6-13).
- **Changed (app):** `UseForwardedHeaders` as the first middleware in all three `Program.cs`
  (M6-02); CORS origins read from `Cors:AllowedOrigins` (M6-03); auth-api DP persistence + `/keys`
  mount mirroring the Frontend (M6-04); startup config validation across the critical settings
  classes (M6-05); add `appsettings.Production.json` for TMS and strip baked URLs from base
  settings (M6-06).
- **Changed (build):** production migrations via `ef migrations bundle` (M6-10); auth-api Dockerfile
  gains the keyring mount.
- **Unchanged:** dev `compose.yaml` (ADR-0006 holds for dev), the patcher (frozen, ADR-0002), and
  the already-neutral health / OTLP / OpenIddict-key / Serilog wiring.
- **Ticket map (milestone M6, dependency order in each body):** #166 this ADR → #167
  forwarded-headers (critical), #168 CORS, #169 auth DP, #170 fail-fast, #171 prod settings → #172
  `compose.prod.yaml` → #173 reverse proxy → #174 CD→GHCR, #175 migration bundle → #176 runbook,
  #177 target-requirements + Azure⇄AWS mapping → #178 smoke test.

## References

- ADR-0002 — TMS pivot + the TheKittySaver 1:1 lift (the neutrality contract keeps that topology
  portable)
- ADR-0005 — Frontend Data Protection key persistence (extended to auth-api by M6-04 / #169)
- ADR-0006 — Frontend not containerized in dev compose; production (ACA) + reverse-proxy /
  forwarded-headers **explicitly deferred** — its Alternative C is this ADR's Decision §7
- spec 0001 — game-update lifecycle (bootstrap + translation-file distribution paths that
  `compose.prod.yaml` must mount)
- Milestone **M6** tickets #166–#178
