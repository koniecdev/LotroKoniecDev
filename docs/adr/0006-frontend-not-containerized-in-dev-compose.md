# ADR-0006: The TMS Frontend is not containerized in dev compose — it runs on the host

**Status:** Accepted (amended 2026-06-20 — #190/M6-14, see the amendments under §1 and §3)
**Date:** 2026-06-15
**Decision-makers:** Solo maintainer
**Related:** Frontend (OIDC RP), AuthSystem (OpenIddict), `compose.yaml`, ticket #144 (M3-10 — HTTPS
in compose), ticket #40 (M3-06 — Frontend Dockerfile + join compose, **partially superseded**),
M3-01 #138 (lifted OIDC RP infra), ticket #190 (M6-14 — dev = infra-only compose + host Kestrels,
**amends §1/§3**), ADR-0002 (TMS pivot + KittySaver lift), ADR-0005 (Frontend Data Protection key
persistence), ADR-0008 (cloud-agnostic deployment — `compose.prod.yaml` is the parity stack this
amendment leans on), TheKittySaver `compose.yaml`

## Context

M3-10 (#144) set out to make the full translator loop work in the browser straight out of
`docker compose up` by lifting TheKittySaver's dev-HTTPS-cert setup. The HTTPS lift for the APIs is
mechanical, but containerizing the Blazor SSR Frontend as an OIDC relying party (the #40 "frontend
join compose" join) collides with a structural fact.

Code facts that constrain the choice:

- The Frontend RP collapses the front-channel and the back-channel onto a **single** authority
  value: `options.Authority = settings.Authority` in
  `Frontend/Infrastructure/Auth/AuthenticationDependencyInjectionExtensions.cs:100`. There is no
  `MetadataAddress` override. That one URL drives BOTH the browser `/authorize` redirect AND the
  server-side back-channel the container itself makes (discovery, JWKS, authorization-code→token,
  and userinfo — the RP sets `GetClaimsFromUserInfoEndpoint = true`).
- OpenIddict stamps an **absolute** issuer unconditionally:
  `options.Issuer = new Uri(settings.Issuer)` in
  `AuthSystem.API/Extensions/OpenIddictExtensions.cs:107`. The discovery document therefore
  advertises absolute endpoint URLs at the issuer origin (`https://localhost:5003/...`) regardless
  of which host fetched it, and the RP validates that discovery `issuer` equals `Authority`.
- Consequence for a **containerized** RP: no single `Authority` works. `https://localhost:5003` is
  what the browser needs (host-reachable, and it matches the token `iss`), but inside the Frontend
  container `localhost` is the container's own loopback and cannot reach the auth container.
  `http://auth-api:8080` is reachable in-network but is meaningless to the browser and mismatches
  `iss`. PR #145 documented exactly this and left the browser-login leg open.
- TheKittySaver — the reference this repo mirrors 1:1 (ADR-0002) — **has no frontend service in its
  `compose.yaml`** (postgres + migrator + auth-api + adoption-api + dev infra only). Its Frontend
  runs on the host via `dotnet run` (`launchSettings` `applicationUrl https://localhost:7017`),
  where `localhost:5003`/`localhost:5024` resolve for both the browser and the host process. So the
  "lift 1:1" instruction never actually had a containerized-RP recipe to copy — #40 invented the
  compose `frontend` service beyond the reference.
- ADR-0005 persists the Frontend Data Protection keyring for "the containerized/multi-replica
  deployment — which is precisely what ticket #40 adds." With this ADR there is no dev container;
  ADR-0005's posture is retained for a **future** production host, not exercised in dev (see below).
- Production hosting (Azure Container Apps) and its public-origin/reverse-proxy topology are
  **explicitly out of scope now** — not designed in this decision.

## Decision

### 1. The dev compose stack is backend-only

`compose.yaml` runs postgres + migrator + auth-api + tms-api + aspire-dashboard + mailpit. The
`frontend` service added in #40 is removed. Compose is the backend the translator's browser and a
host-run Frontend talk to — not a place the Frontend itself runs in dev.

**Amendment (2026-06-20, #190 / M6-14) — dev compose is now *infra-only*; all three apps run on host
Kestrels.** §1's "backend-only compose" is superseded for the dev inner loop. `compose.yaml` is
demoted to **infra-only** — `postgres` + `migrator` + `mailpit` + `aspire-dashboard` — and the
**three apps run on the host** as the canonical dev loop: auth-api (`https://localhost:5003`),
tms-api (`https://localhost:5002`), frontend (`https://localhost:7017`), each via its `https`
`launchSettings` profile (`dotnet run`, or a Rider compound — `.run/TMS dev (all hosts).run.xml`).
The containerized `auth-api`/`tms-api` services are removed from `compose.yaml`.

Rationale: the containerized middle tier no longer earned its place. It was *not* the fast dev loop
(host Kestrels give hot reload, breakpoints, and no `docker compose build <api>` on every code
change), and it was *not* prod-parity either — `compose.prod.yaml` (M6-07/08) already exercises the
**same Dockerfiles** behind Caddy with real keys / forwarded headers / DP volumes, and CD (M6-09)
builds those images in CI. So "the API images build & wire up" stays covered without paying a
rebuild on every inner-loop change, and the dev posture moves even closer to the all-host
TheKittySaver loop (§Context).

The two-legs / `iss` reasoning of §2 is unchanged and now applies to all three apps uniformly:
every app resolves `localhost:5003` / `:5002` identically (browser, host RP, host resource server),
so one `Authority` / `Issuer` serves every leg. tms-api's back-channel relies on the
`Auth:Authority` → `Auth:Issuer` fallback (`AuthSettings.EffectiveAuthority`) to reach the host auth
Kestrel at `https://localhost:5003`; the in-network `http://auth-api:8080` was a compose-ism,
removed with the service. `compose.prod.yaml` remains the **sole** containerized / parity stack and
is **untouched** (out of scope, per the ticket).

### 2. The Frontend runs on the host via `dotnet run`, exactly like TheKittySaver

The translator (and the developer) launches `LotroKoniecDev.Frontend` on the host
(`https://localhost:7017`, its `launchSettings` https profile). The browser and that host process
both reach the compose backend over its published HTTPS host ports — `https://localhost:5003`
(auth-api) and `https://localhost:5002` (tms-api). Because the host-run RP and the browser share the
same name resolution, one `Authority = https://localhost:5003` serves both legs and matches the
token `iss`. The two-legs / `iss` problem of §Context dissolves entirely — no `MetadataAddress`
split, reverse proxy, or `host.docker.internal` rewrite is needed.

### 3. The APIs serve HTTPS in compose; the dev-cert lift stays

auth-api and tms-api keep the lifted dev-HTTPS-cert setup (cert mounted into Kestrel, dual
`ASPNETCORE_URLS` `https://+:8081;http://+:8080`, host port → `:8081`), mirroring what TheKittySaver
does for its auth-api + adoption-api. In-network API↔API + a host-run SSR→API call use the published
HTTPS host ports; the `http://…:8080` in-network listener remains for container-to-container traffic
(e.g. tms-api → auth-api JWKS). `scripts/init-dev-https.{sh,ps1}` + `up.{sh,ps1}` and the
`.env.example` `ASPNETCORE_KESTREL_CERT_PASSWORD` key stay — the cert is still required by the two
APIs.

**Amendment (2026-06-20, #190 / M6-14):** with the APIs off dev compose (§1 amendment), the mounted
dev-cert PFX is gone from the dev loop. Host Kestrels use the **native** ASP.NET Core dev certificate
(`dotnet dev-certs https --trust`) selected by their `https` `launchSettings` profile — no PFX, no
mount, no `ASPNETCORE_Kestrel__Certificates__*`. `scripts/init-dev-https.{sh,ps1}` and the
`.env.example` `ASPNETCORE_KESTREL_CERT_PASSWORD` key are therefore **retired** (the PFX export
served only the now-removed containerized APIs). Prod-parity TLS — `scripts/init-prod-https` + Caddy
+ `.docker/trust-ca-entrypoint.sh` — is a separate path and is unaffected.

### 4. The Frontend Dockerfile is kept but unreferenced

`src/Frontend/LotroKoniecDev.Frontend/Dockerfile` is retained (TheKittySaver keeps its Frontend
Dockerfile too, also unreferenced by its compose) so a future ACA/production image build is not
blocked. It MUST NOT be referenced by `compose.yaml`. Keeping it is free; removing it would diverge
from the reference and discard work needed later.

### 5. This supersedes the in-compose-Frontend scope

The #40 "frontend join compose" join and #144's "browser OIDC login works fully inside the compose
stack" acceptance criterion are **withdrawn** as design errors for the dev posture; the loop is
proven with a host-run Frontend instead (the TheKittySaver flow). ADR-0005 (key persistence) is
**not** superseded — it is retained for the deferred production host.

## Consequences

### Positive

- The browser login loop works with no proxy, no forwarded-headers middleware, and no
  `MetadataAddress` plumbing — one `Authority` for both legs, `iss` matches by construction.
- Dev posture matches the reference (ADR-0002) exactly: TheKittySaver also runs its Frontend on the
  host against an in-compose backend.
- Smaller, faster dev stack (no Frontend image build on `compose up`); the API HTTPS lift — the part
  with real value, including closing the prior `iss`/listener mismatch — is delivered.
- Dev Data Protection keys persist "by accident" at `~/.aspnet/DataProtection-Keys` for a host-run
  app (ADR-0005 §Context), so login/antiforgery survive restarts with zero extra config.

### Negative / Accepted Trade-offs

- "Run the whole thing with one command" no longer includes the Frontend — the translator starts the
  backend with `scripts/up.*` and the Frontend with `dotnet run` (two steps). Documented in
  CLAUDE.md.
- The Frontend's containerized runtime path is now **only** exercised in production (ACA), which is
  deferred and undesigned — its first real container run will surface origin/forwarded-headers work
  that this decision postpones rather than solves.
- The kept-but-unreferenced Dockerfile can drift from a working state until production revisits it
  (mitigated: it still builds today; CI builds the solution, and the Dockerfile is a thin publish
  over that).

## Alternatives Considered

### A. Frontend on the host, backend in compose over HTTPS (this ADR)

Chosen. Mirrors TheKittySaver, dissolves the two-legs/`iss` problem with zero added infra, and ships
the API HTTPS lift now.

### B. Containerize the Frontend and split `MetadataAddress` from `Authority`

Set `MetadataAddress` to the in-network discovery URL, keep `Authority` browser-facing. Rejected.
OpenIddict's absolute `iss` (`OpenIddictExtensions.cs:107`) means the discovery doc still hands the
container absolute `https://localhost:5003/...` token/userinfo endpoints, so the back-channel still
needs a host rewrite (`extra_hosts`/gateway) on top — new RP code plus fragile per-endpoint routing,
for a dev convenience the reference doesn't even attempt.

### C. Containerize the Frontend behind a reverse proxy on one shared HTTPS origin

The classic fix, and the "reverse-proxy/forwarded-headers ADR" floated in #138/#40. Rejected for
dev. Adds a proxy service and the `ForwardedHeaders` middleware the Frontend currently lacks, to
solve a problem that only exists because we put the RP in a container — which §2 simply doesn't do.
This is the shape production (ACA) will likely take, and is deferred to that decision.

### D. Share `host.docker.internal` as the OIDC origin for both legs

Rejected. Docker-Desktop-only, and it forces the dev `iss`/seeded redirect URI off `localhost` onto
`host.docker.internal`, diverging from the reference and from the all-local `dotnet run` workflow for
no gain over A.

## Implementation Notes

- Changed: `compose.yaml` — remove the `frontend` service (and the HTTPS bits added for it in #145:
  cert mount, dual URLs, `5001:8081`, browser-facing `AuthSystem__Authority`); keep auth-api +
  tms-api HTTPS.
- Frontend host-run config: `Frontend/appsettings.Development.json` —
  `AuthSystem:Authority`/`AuthSystem:BaseUrl` → `https://localhost:5003`, `TranslationSystem:BaseUrl`
  → `https://localhost:5002` (the compose host ports), so a host-run Frontend hits the in-compose
  backend and `iss` matches. `Properties/launchSettings.json` https profile stays
  `https://localhost:7017` (the seeded web-client redirect host).
- Kept: `scripts/init-dev-https.{sh,ps1}`, `scripts/up.{sh,ps1}`, `.env.example`
  `ASPNETCORE_KESTREL_CERT_PASSWORD`, `src/Frontend/LotroKoniecDev.Frontend/Dockerfile`
  (unreferenced).
- **Amended by #190 (M6-14) — superseding the two lines above:** `compose.yaml` drops the
  `auth-api`/`tms-api` services (now infra-only); `scripts/init-dev-https.{sh,ps1}` and the
  `.env.example` `ASPNETCORE_KESTREL_CERT_PASSWORD` key are **removed** (host Kestrels use the native
  dev cert — §3 amendment); auth `appsettings.Development.json` connection string is corrected to
  `Database=lotro_auth` and carries the dev Postgres password, and tms keeps no `Auth:Authority`
  (the `EffectiveAuthority` fallback resolves the host auth Kestrel) so host Kestrels hit the
  compose Postgres + auth over `localhost`. `scripts/up.{sh,ps1}` boot infra + migrator only (no
  `--build` for app-code changes). `src/Frontend/.../Dockerfile` stays kept-but-unreferenced.
- Docs: `CLAUDE.md` — compose stack is backend-only over HTTPS; Frontend runs locally via
  `dotnet run`; correct the lift-map "frontend joins compose" line and the stale "HTTPS arrives with
  the M3 Frontend" note.
- AuthSystem seeded web-client redirect URIs (`https://localhost:7017/callback`, root post-logout)
  are unchanged — they already target the host-run Frontend.

## References

- ADR-0002 — TMS pivot + the TheKittySaver 1:1 lift (this decision restores compose to the reference
  topology)
- ADR-0005 — Frontend Data Protection key persistence (retained for the deferred production host;
  **not** superseded)
- Ticket #144 (M3-10) — re-scoped to this decision; PR #145
- Ticket #40 (M3-06) — its compose `frontend` service is withdrawn by §1/§5
- TheKittySaver (`~/RiderProjects/TheKittySaver`) — `compose.yaml` (no frontend service) and
  `src/Frontend/.../launchSettings.json` (host-run on `https://localhost:7017`)
