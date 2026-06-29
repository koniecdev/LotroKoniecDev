# Deployment runbook

> Operator-facing procedures for running LotroKoniecDev on a container host. Provider-neutral by
> design (ADR-0008): everything here is "a 12-factor OCI image behind a TLS ingress" and applies to
> Azure Container Apps, AWS ECS/App Runner, or a plain Docker host alike.
>
> **Scope:** the full configuration surface and bring-up of the four service images across
> environments — the environment-variable matrix, secret generation, the consistency rules that bite,
> the bring-up sequence, and database migrations. The platform requirements + Azure⇄AWS service
> mapping live in [`target-requirements.md`](target-requirements.md) (M6-12); the only deferred
> piece is the provider-specific deploy walkthrough, authored once the provider is chosen.

## Contents

- [Services & the container contract](#services--the-container-contract)
- [Environment variable matrix](#environment-variable-matrix) — the single source of truth, per service × environment
- [Generating secrets](#generating-secrets)
- [Consistency rules that bite](#consistency-rules-that-bite) — issuer / redirect / authority / CORS
- [Bringing the stack up](#bringing-the-stack-up)
- [Continuous deployment (CI/CD)](#continuous-deployment-cicd) — the automated rollout, the approval gate, and the one-time operator setup
- [Database migrations](#database-migrations)
- [Post-deploy smoke test](#post-deploy-smoke-test) — one command verifies a deployed environment end-to-end
- [See also](#see-also)

## Services & the container contract

Four OCI images (built by the four multi-stage Dockerfiles, published to GHCR by CD — M6-09) behind
one TLS-terminating ingress:

| Service | Image (`ghcr.io/koniecdev/…`) | Listens | Health | Persists |
|---|---|---|---|---|
| **auth-api** | `lotrokoniecdev-auth-api` | `:8080` (HTTP) | `/health/live`, `/health/ready` (DB + SMTP) | Data Protection keyring → `/keys` |
| **tms-api** | `lotrokoniecdev-tms-api` | `:8080` (HTTP) | `/health` (anon), `/health/live`, `/health/ready` (DB) | translation artifacts (read-only mount) |
| **frontend** | `lotrokoniecdev-frontend` | `:8080` (HTTP) | — | Data Protection keyring → `/keys` |
| **migrator** | `lotrokoniecdev-migrator` | one-shot (exits 0) | exit code | — |
| _ingress_ | any TLS-terminating reverse proxy | `:443` | — | — |

Container contract (ADR-0008 §2): each app serves **plain HTTP on `:8080`** and expects a
TLS-terminating ingress in front; runs **non-root**; logs **structured JSON to stdout**; takes **all
runtime configuration from environment variables**. The migrator runs to completion *before* the APIs
serve traffic, and the APIs depend on its success — so there is never half-migrated serving.

## Environment variable matrix

The single source of truth: every deployment-relevant setting, per service, per environment. One
table per service.

**Reading the tables:**

- **Key form.** Keys are shown in the env-var (double-underscore) form ASP.NET Core binds —
  `Section__Sub__Leaf`, e.g. `OpenIddict__WebClient__RedirectUris__0`. The config/appsettings form is
  the same with `:` (`OpenIddict:WebClient:RedirectUris:0`); error messages use the `:` form.
- **Required?** = whether boot **fails fast** without it (M6-05) in that environment. `✅ all` =
  required in every environment; `✅ non-dev` = required in Staging/Production only (Development
  supplies a default or skips the guard); `optional` = safe default.
- **Source.** **secret** = inject via the platform's secret store (or the git-ignored `.env*`), never
  commit; **plain** = non-sensitive, fine in plain app config / `appsettings.*`.
- **local-dev** column = how the value is set in dev. Since ADR-0006 (amended by #190 / M6-14) the
  dev `compose.yaml` is **infra-only** and all three apps (auth-api, tms-api, frontend) run on the
  **host** via `dotnet run` — so for the apps the dev column is `appsettings.Development.json` +
  `launchSettings.json`; only postgres / migrator / mailpit / aspire are set by `compose.yaml`.
- **Staging / Production** are structurally identical — same required set, same sources; they differ
  only in **hostnames** (use the environment's own domain) and **secret values**. This column shows
  **production placeholders**; substitute `lotro.koniec.dev` with your environment's domain (e.g. a
  `*.staging.lotro.koniec.dev` for staging). The local production-parity stack
  (`compose.prod.yaml`) wires the very same keys with `*.lotro.test` hostnames.

Purely optional tuning knobs with safe defaults are omitted (e.g. `OpenIddict:AccessTokenLifetimeMinutes`
= 60, `OpenIddict:RefreshTokenLifetimeDays` = 14, `Import:*`, `Email:TimeoutSeconds`/`MaxSendAttempts`,
`AllowedHosts` = `*`).

> ⚠️ **Live prod domain is `lotro-translator.pl`** — auth → `https://auth.lotro-translator.pl`,
> tms → `https://tms.lotro-translator.pl`, frontend → `https://lotro-translator.pl`; live RG
> `rg-lotrotms-prod-polc-001`. The `*.lotro.koniec.dev` strings in the tables below are historical
> placeholders — read each as the live domain (or your own environment's).

### auth-api

| Variable | local-dev | Staging / Production (placeholder) | Required? | Source | Notes |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` | ✅ all | plain | Gates fail-fast validation, ephemeral-vs-real keys, the CORS policy. |
| `ASPNETCORE_URLS` | `https://localhost:5003` (launchSettings) | `http://+:8080` | ✅ all | plain | Host dev Kestrel (native dev cert); prod serves HTTP only (the ingress owns TLS). |
| `ConnectionStrings__AuthDatabase` | `…;Database=lotro_auth;…;Password=changeme` (appsettings.Development) | `Host=…;Database=lotro_auth;Username=…;Password=…;Ssl Mode=Require;Trust Server Certificate=true` | ✅ all | **secret** | Carries the DB password. Host dev hits the compose Postgres on `localhost:5432`. Managed DB: keep `Ssl Mode=Require`, drop `Trust Server Certificate`. |
| `OpenIddict__Issuer` | `https://localhost:5003` | `https://auth.lotro.koniec.dev` | ✅ non-dev | plain | **THE token `iss`.** Absolute http(s), no `localhost`. Must equal tms `Auth__Issuer`. |
| `OpenIddict__SigningKey__RsaPrivateKeyXml` | — (ephemeral) | base64 of RSA XML (≥2048-bit) | ✅ non-dev | **secret** | Generate with `gen-openiddict-keys`. |
| `OpenIddict__EncryptionKey__Key` | — (ephemeral) | base64 of a ≥32-byte key | ✅ non-dev | **secret** | Generate with `gen-openiddict-keys`. |
| `OpenIddict__ApiClientSecret` | `dev-api-secret-min-32-characters-long` | ≥32-char random secret | ✅ non-dev | **secret** | Generate with `gen-openiddict-keys`. Shared with the service client. |
| `OpenIddict__WebClient__RedirectUris__0` | `https://localhost:7017/callback` | `https://lotro.koniec.dev/callback` | ✅ non-dev | plain | MUST equal the Frontend callback (its public origin + `AuthSystem__CallbackPath`). |
| `OpenIddict__WebClient__PostLogoutRedirectUris__0` | `https://localhost:7017` | `https://lotro.koniec.dev` | ✅ non-dev | plain | Frontend post-logout return URL. |
| `Cors__AllowedOrigins__0` | — (AllowAnyOrigin) | `https://lotro.koniec.dev` | ✅ non-dev | plain | Bare origin = Frontend public URL. Lowercase, no port-if-default, no path/slash. |
| `DataProtection__KeyRingPath` | — (host default) | `/keys` | ✅ non-dev | plain | Persistent, replica-shared volume; else logins/antiforgery/reset links break on deploy/scale. |
| `Email__Host` | `mailpit` | `smtp.sendgrid.net` | ✅ all | plain | SMTP host. Validated on start (every environment). |
| `Email__Port` | `1025` | `587` | ✅ all | plain | 1–65535. |
| `Email__Mode` | `None` | `StartTls` | ✅ all | plain | One of `None` / `StartTls` / `TLS`. |
| `Email__SenderEmail` | `noreply@lotro.koniec.dev` | `no-reply@lotro.koniec.dev` | ✅ all | plain | Must be a valid email. |
| `Email__Sender` | `lotro.koniec.dev` | `LOTRO PL` | ✅ all | plain | Display name. |
| `Email__Username` / `Email__Password` | — | provider credentials | optional¹ | **secret** (Password) | ¹If `Username` is set, `Password` is required. |
| `AdminUser__Username` / `AdminUser__Email` / `AdminUser__Password` | from `AUTH_ADMIN_*` | from `AUTH_ADMIN_*` | optional | **secret** (Password) | Seeds one admin on first boot; leave blank to skip. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://aspire-dashboard:18889` | OTLP collector URL | optional | plain | Empty = telemetry export disabled. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` | `grpc` / `http/protobuf` | optional | plain | Defaults to `grpc`. |

### tms-api

| Variable | local-dev | Staging / Production (placeholder) | Required? | Source | Notes |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` | ✅ all | plain | Gates fail-fast validation + the CORS policy. |
| `ASPNETCORE_URLS` | `https://localhost:5002` (launchSettings) | `http://+:8080` | ✅ all | plain | Host dev Kestrel (native dev cert); prod serves HTTP only (ingress owns TLS). |
| `ConnectionStrings__TranslationDatabase` | `…;Database=lotro_translation;…;Password=changeme` (appsettings.Development) | `Host=…;Database=lotro_translation;Username=…;Password=…;Ssl Mode=Require;Trust Server Certificate=true` | ✅ all | **secret** | TMS write context. Host dev hits the compose Postgres on `localhost:5432`. Managed DB swap = change just this value. |
| `Auth__Issuer` | `https://localhost:5003` | `https://auth.lotro.koniec.dev` | ✅ all | plain | MUST equal auth `OpenIddict__Issuer` (the token `iss`); tokens are rejected otherwise. |
| `Auth__Authority` | — (unset → falls back to `Issuer`, `https://localhost:5003`) | `https://auth.lotro.koniec.dev` | optional² | plain | Back-channel for OIDC metadata + JWKS. ²Unset → falls back to `Issuer`; the dev host run relies on that fallback to reach the host auth Kestrel. Prod: must be `https` (OpenIddict rejects plain HTTP) and reachable from the container. |
| `Auth__Audience` | `lotrokoniecdev-api` | `lotrokoniecdev-api` | ✅ all | plain | Default in base `appsettings.json`. |
| `Cors__AllowedOrigins__0` | — (AllowAnyOrigin) | `https://lotro.koniec.dev` | ✅ non-dev | plain | Bare origin = Frontend public URL. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` / `OTEL_EXPORTER_OTLP_PROTOCOL` | aspire / `grpc` | collector / `grpc` | optional | plain | Empty endpoint = export disabled. |
| `Bootstrap__Enabled` | `false` | `false` | optional | plain | One-time DB seed of the first export (spec 0001). Off by default. |
| `Bootstrap__GameVersion` / `Bootstrap__ExportedTextPath` / `Bootstrap__PolishTextPath` | — / — / `/app/translations/polish.txt` | as needed | optional | plain | Only consulted when `Bootstrap__Enabled=true`. |

### frontend

Runs on the **host** in dev (`dotnet run`, ADR-0006 — and since #190 / M6-14 so do auth-api + tms-api) —
its dev column is `appsettings.Development.json` + `launchSettings.json`; it is containerized only in
Staging/Production.

| Variable | local-dev | Staging / Production (placeholder) | Required? | Source | Notes |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` | ✅ all | plain | Gates the DP keyring guard. |
| `ASPNETCORE_URLS` | `https://localhost:7017` (launchSettings) | `http://+:8080` | ✅ all | plain | Prod serves HTTP only (ingress owns TLS). |
| `AuthSystem__Authority` | `https://localhost:5003` | `https://auth.lotro.koniec.dev` | ✅ all | plain | Drives the `/authorize` redirect (front-channel) **and** discovery/token (back-channel). |
| `AuthSystem__BaseUrl` | `https://localhost:5003/` | `https://auth.lotro.koniec.dev/` | ✅ all | plain | Auth origin (trailing slash). |
| `AuthSystem__ClientId` | `lotrokoniecdev-web` | `lotrokoniecdev-web` | ✅ all | plain | Must match the OpenIddict web-client id. |
| `AuthSystem__CallbackPath` | `/callback` | `/callback` | ✅ all | plain | Origin + this MUST be registered in auth `RedirectUris`. |
| `AuthSystem__SignedOutCallbackPath` | `/signout-callback-oidc` | `/signout-callback-oidc` | ✅ all | plain | Origin + this MUST be in auth `PostLogoutRedirectUris`. |
| `AuthSystem__Scopes` | `openid,email,profile,roles,api,offline_access` | same | ✅ all | plain | At least one scope. |
| `TranslationSystem__BaseUrl` | `https://localhost:5002/` | `https://tms.lotro.koniec.dev/` | ✅ all | plain | TMS API origin (trailing slash). |
| `DataProtection__KeyRingPath` | — (host default) | `/keys` | ✅ non-dev | plain | Persistent, replica-shared volume (ADR-0005); else antiforgery + auth cookies break on deploy/scale. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` / `OTEL_EXPORTER_OTLP_PROTOCOL` | — | collector / `grpc` | optional | plain | Empty endpoint = export disabled. |

### migrator

A one-shot job — reads exactly the two connection strings (see [Database migrations](#database-migrations)
for the full strategy):

| Variable | local-dev | Staging / Production (placeholder) | Required? | Source | Notes |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` | ✅ all | plain | |
| `ConnectionStrings__TranslationDatabase` | from `POSTGRES_PASSWORD` | managed/self-hosted connection string | ✅ all | **secret** | TMS write context (`lotro_translation`). |
| `ConnectionStrings__AuthDatabase` | from `POSTGRES_PASSWORD` | managed/self-hosted connection string | ✅ all | **secret** | Auth context (`lotro_auth`). |

## Generating secrets

Every value marked **secret** above. All commands are copy-paste runnable; `.sh` shown, each has a
`.ps1` twin.

### OpenIddict production keys (auth-api)

The three OpenIddict secrets — generate all at once and append to your env / secret store:

```bash
scripts/gen-openiddict-keys.sh >> .env.prod        # PowerShell: scripts/gen-openiddict-keys.ps1 >> .env.prod
```

It prints a leading comment line plus three `KEY=VALUE` lines:

```
# Generated by scripts/gen-openiddict-keys.sh — keep secret, never commit.
OpenIddict__EncryptionKey__Key=<base64 of a 32-byte key>
OpenIddict__ApiClientSecret=<48 hex chars>
OpenIddict__SigningKey__RsaPrivateKeyXml=<base64 of RSA.ToXmlString(true), 2048-bit>
```

Manual equivalents for the two simple ones (if you cannot run the script):

```bash
openssl rand -base64 32        # OpenIddict__EncryptionKey__Key  (256-bit symmetric key)
openssl rand -hex 24           # OpenIddict__ApiClientSecret     (48 chars, ≥ the 32 minimum)
```

`RsaPrivateKeyXml` is base64 of .NET's `RSA.ToXmlString(true)` for a ≥2048-bit key — the script does
the PEM→.NET-XML conversion (it needs `openssl` + `python3`); reproduce it with the script rather than
by hand.

### TLS / ingress certificate

- **Real environment:** a publicly-trusted cert on the **ingress** (managed cert / Let's Encrypt). The
  app containers validate the ingress's cert against the **OS trust store** — no app config, no mount.
- **Local prod-parity:** `scripts/init-prod-https.sh` mints a local CA + a `*.lotro.test` leaf for
  Caddy; `.docker/trust-ca-entrypoint.sh` installs the CA into the tms-api/frontend OS store so their
  OIDC back-channel to `https://auth.lotro.test` is trusted (.NET ignores `SSL_CERT_FILE`).
- **Local dev (host Kestrels):** the apps run on the host (ADR-0006, amended by #190 / M6-14) and serve
  HTTPS with the **native** ASP.NET Core dev cert — run `dotnet dev-certs https --trust` once. No PFX,
  no mount; the dev `compose.yaml` is infra-only and runs no app containers.

### Databases

The system uses **two** databases: `lotro_translation` (TMS) and `lotro_auth` (Auth). A managed
Postgres typically provisions one — create the second:

```sql
CREATE DATABASE "lotro_auth";
```

`scripts/init-postgres.sh` does this automatically for the self-hosted (compose) Postgres on first
boot. The connection-string password is a **secret** — keep it out of git.

### Admin seed (optional)

Set all three to seed one usable admin login into the auth DB on first boot; leave blank to skip:

```
AUTH_ADMIN_USERNAME=…
AUTH_ADMIN_EMAIL=…
AUTH_ADMIN_PASSWORD=…        # secret
```

## Consistency rules that bite

The cross-service settings that are individually valid but break the system when they disagree. Most
"works locally, 401/redirect error on staging" failures are one of these.

1. **Issuer must equal the token `iss`.** auth-api stamps `OpenIddict__Issuer` into every token's
   `iss`; tms-api validates it against `Auth__Issuer` (`ValidIssuer`). The two MUST be byte-identical
   and MUST be the **public auth origin the browser uses**. Mismatch → tms-api `401`s every request.
   Outside Development the value may not contain `localhost` (the validator rejects it).

2. **Authority is the back-channel address, not the issuer.** `Auth__Authority` (tms-api) and
   `AuthSystem__Authority` (frontend) are where the app **fetches OIDC metadata + JWKS**; the issuer
   is what it **validates `iss` against**. They can legitimately differ when metadata is on an internal
   address while tokens carry the browser-facing URL — but in this repo they currently coincide in
   every environment: dev leaves `Auth__Authority` unset so it falls back to `Auth__Issuer`
   (`https://localhost:5003`, the host auth Kestrel), and the containerized prod-parity stack sets both
   to the proxy origin (`https://auth.lotro.test`) because Production **OpenIddict rejects plain HTTP**,
   so the internal `http://auth-api:8080` cannot serve as Authority there. Two production traps: (a) in Production **OpenIddict
   rejects plain HTTP**, so the Authority MUST be `https` — use the public/ingress origin, not
   `http://auth-api:8080`; (b) that host MUST be reachable from inside the container and its cert
   trusted by the container's OS store. Unset `Auth__Authority` falls back to `Auth__Issuer`.

3. **Frontend redirect URIs must be registered at the auth server.** The frontend sends
   `redirect_uri = <its public origin> + AuthSystem__CallbackPath` (and post-logout = origin +
   `SignedOutCallbackPath`). Those exact absolute URLs MUST appear in auth's
   `OpenIddict__WebClient__RedirectUris` / `…PostLogoutRedirectUris`. Any difference (scheme, host,
   trailing slash) → OIDC `invalid redirect_uri` at login. `AuthSystem__ClientId` must equal the
   registered web-client id (`lotrokoniecdev-web`).

4. **CORS origin = the frontend's public URL, as a bare origin.** auth/tms `Cors__AllowedOrigins__0`
   MUST be the browser app's exact origin — **lowercase scheme+host, no port if default, no userinfo,
   no path, no query, no trailing slash** (the validator rejects anything else at boot). It is the
   same value as a redirect URI's origin part.

5. **Behind a TLS-terminating ingress, forwarded headers are load-bearing.** The ingress MUST send
   `X-Forwarded-Proto` (and Host); all three apps read them (`UseForwardedHeaders`, M6-02) to
   reconstruct the `https` scheme used for `iss`, `redirect_uri`, and `Secure` cookies. The containers
   trust **all** upstream proxies (`KnownProxies`/`KnownIPNetworks` cleared) — safe **only** because
   they are never reachable except through the ingress, so **do not expose `:8080` publicly**. In prod
   the apps serve HTTP only; the proxy owns TLS.

6. **The Data Protection keyring must be persistent and shared.** auth-api + frontend need
   `DataProtection__KeyRingPath` pointing at a persistent, replica-shared volume (`/keys`). An
   ephemeral keyring → every deploy/scale-out logs everyone out and breaks antiforgery +
   password-reset/email-confirmation links. Fails fast at boot if unset outside Development.

## Bringing the stack up

### Locally, development (infra + host Kestrels)

The day-to-day inner loop (ADR-0006, amended by #190 / M6-14). Boot the infra-only stack, then run the
three apps on the host:

```bash
scripts/up.sh                       # PowerShell: scripts/up.ps1 — boots postgres + migrator + mailpit + aspire
dotnet dev-certs https --trust      # one-time, so the host Kestrels serve trusted HTTPS
```

Then start the three host Kestrels — all at once via the Rider compound **`TMS dev (all hosts)`**, or
each in its own terminal:

```bash
dotnet run --project src/AuthSystem/LotroKoniecDev.AuthSystem.API                 # → https://localhost:5003
dotnet run --project src/TranslationSystem/LotroKoniecDev.TranslationSystem.API   # → https://localhost:5002
dotnet run --project src/Frontend/LotroKoniecDev.Frontend                         # → https://localhost:7017
```

Verify: open `https://localhost:7017`, log in via OIDC, and confirm an authenticated TMS call succeeds.
An app-code change is picked up by re-running that one project (or hot reload) — no image rebuild.

### Locally, production-parity (the rehearsal)

The fastest way to exercise the real topology — real keys, forwarded headers, DP volumes,
containerized frontend, TLS proxy — on a laptop (ADR-0008 §4):

```bash
scripts/up-prod.sh --build          # PowerShell: scripts/up-prod.ps1 --build
```

It bootstraps `.env.prod` (with freshly generated OpenIddict secrets), the local CA + certs, and the
`*.lotro.test` hosts mapping, then runs `docker compose -f compose.prod.yaml up`. Verify:

```bash
curl --cacert .docker/prod-https/rootCA.crt https://auth.lotro.test/health/ready
curl --cacert .docker/prod-https/rootCA.crt https://tms.lotro.test/health/ready
# browser OIDC login: https://app.lotro.test
```

For an all-local run (no external SMTP/OTLP), add the profiles so the auth `/health/ready` SMTP probe
passes and traces are viewable, and set `Email__Host=mailpit` / `Email__Port=1025` / `Email__Mode=None`
+ `OTEL_EXPORTER_OTLP_ENDPOINT=http://aspire-dashboard:18889` in `.env.prod`:

```bash
docker compose -f compose.prod.yaml --env-file .env.prod --profile local-smtp --profile local-otel up --build
```

### A real environment (staging / production)

Provider-neutral sequence — anything that runs an OCI image behind a TLS ingress (platform
requirements + the Azure⇄AWS service mapping are in [`target-requirements.md`](target-requirements.md);
the provider-specific walkthrough is deferred until the provider is chosen):

1. **Provision** Postgres (two databases — see [Generating secrets](#generating-secrets)) and a TLS
   ingress holding a publicly-trusted cert.
2. **Configure** every service per the [matrix](#environment-variable-matrix): plain values in the
   platform's app config, secrets in its secret store. Then re-check the
   [consistency rules](#consistency-rules-that-bite) — issuer / redirect / authority / CORS — **across**
   services.
3. **Migrate**: run the migrator image to completion against both databases
   ([Database migrations](#database-migrations)). A non-zero exit blocks the rollout.
4. **Roll out** auth-api, tms-api, frontend on the **same image tag** as the migrator. Mount the DP
   keyring volumes; point the ingress at each app's `:8080`.
5. **Verify**: `/health/ready` green on both APIs, a full browser OIDC login, and the
   [post-deploy smoke test](#post-deploy-smoke-test) (one command — health + auth token + token
   acceptance + file distribution).

> The **manual** sequence above is the provider-neutral fallback / first bring-up. **Ongoing deploys
> to the live Azure environment are automated** — see [Continuous deployment (CI/CD)](#continuous-deployment-cicd).

## Continuous deployment (CI/CD)

Ongoing delivery to the live Azure environment is automated (ADR-0012). Three workflows:

| Workflow | Trigger | What it does |
|---|---|---|
| `cd.yml` → `build-and-push` | push to main, `v*` tag | builds + pushes the 4 images to GHCR (`:sha-<short>`, `:latest` on main, semver on tags) |
| `cd.yml` → `deploy-prod` | push to main / dispatch, **behind the `production` approval gate** | OIDC login → run migrator to success (**gate**) → `az containerapp update` the 3 apps to `:sha-<short>` → readiness wait → smoke |
| `infra.yml` | PR / push to `iac/**`, dispatch | `plan` on PRs (preview in run summary); **gated** `apply` on main |

**The release control.** Every merge to main builds, then **waits at the `production` environment**
for a human to approve (GitHub → the run → *Review deployments* → *Approve*). Nothing reaches prod
until you click — the safeguard for QA testing on prod. Approve when QA is free.

**Deploy a specific build on demand.** Actions → *CD* → *Run workflow* → optional `image_tag`
(`sha-<short>` or `vX.Y.Z`); empty = the chosen ref's commit. Still gated.

**Roll back.** Re-run *CD* via dispatch with `image_tag` = the previous good `sha-<short>` (GHCR
images are immutable). Approve. DB migrations are forward-only — roll the schema forward, not back
(ADR-0008 §6); snapshot the DBs before a risky release.

### One-time operator setup (your Azure + GitHub)

Keyless via OIDC federation — no client secret is stored. Run once.

**1. Entra app + federated credentials** (the `subject` strings must match exactly):

```bash
APP_ID=$(az ad app create --display-name "github-lotrotms-cd" --query appId -o tsv)
az ad sp create --id "$APP_ID"
# gated jobs (deploy-prod + terraform apply both run under environment: production)
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name":"gh-env-production","issuer":"https://token.actions.githubusercontent.com",
  "subject":"repo:koniecdev/LotroKoniecDev:environment:production",
  "audiences":["api://AzureADTokenExchange"]}'
# infra PR plan job (no environment)
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name":"gh-pull-request","issuer":"https://token.actions.githubusercontent.com",
  "subject":"repo:koniecdev/LotroKoniecDev:pull_request",
  "audiences":["api://AzureADTokenExchange"]}'
```

**2. RBAC** — Contributor on both RGs (roll apps + manage infra + read/write tfstate):

```bash
SP_OBJ=$(az ad sp show --id "$APP_ID" --query id -o tsv)
SUB=$(az account show --query id -o tsv)
for RG in rg-lotrotms-prod-polc-001 rg-lotrotms-tfstate; do
  az role assignment create --assignee-object-id "$SP_OBJ" --assignee-principal-type ServicePrincipal \
    --role Contributor --scope "/subscriptions/$SUB/resourceGroups/$RG"
done
```

**3. GitHub `production` environment** with **you as a required reviewer** (repo Settings →
Environments → New → `production` → *Required reviewers* → add yourself). This block IS the gate.

**4. Seed the app secrets into Azure Key Vault (ADR-0013).** The 8 app secrets are the single source
of truth in Key Vault (`lotrotms-kv-prod`), read at runtime by the `lotrotms-aca-prod` managed
identity — they are **not** GitHub Secrets and **not** Terraform inputs (no plaintext on disk, in the
TF state, or in CI). The idempotent seed script (also the rotation tool) ensures the Vault, the
identity, its `Key Vault Secrets User` grant, and the 8 secrets. It needs **Owner / User Access
Administrator** once — it creates a role assignment, which is why CI never does and stays Contributor:

```bash
az login   # an Owner / User Access Administrator on the subscription
export SEED_CONNECTION_STRING_TRANSLATION='…'   # Supabase TMS connection string
export SEED_CONNECTION_STRING_AUTH='…'          # Supabase Auth connection string
export SEED_OPENIDDICT_SIGNING_KEY='…'          # base64 RSA xml  (scripts/gen-openiddict-keys.sh)
export SEED_OPENIDDICT_ENCRYPTION_KEY='…'       # base64 32-byte key      (same generator)
export SEED_OPENIDDICT_API_CLIENT_SECRET='…'    # >= 32 chars  (== SMOKE_CLIENT_SECRET below)
export SEED_SMTP_USERNAME='…' SEED_SMTP_PASSWORD='…'   # Brevo SMTP credentials
export SEED_ADMIN_PASSWORD='…'                  # seeded admin password
scripts/seed-keyvault.sh                        # PowerShell twin: scripts/seed-keyvault.ps1
```

Rotation later = re-run the script with new `SEED_*` values; the versionless Key Vault URIs make the
next deployment pick them up with no Terraform change.

**5. GitHub repo Secrets** (Settings → Secrets and variables → Actions → *Secrets*) — **infra/OIDC
only**, no app secrets (those live in Key Vault, step 4):

| Secret | Value |
|---|---|
| `AZURE_CLIENT_ID` | the Entra app id (`$APP_ID`) |
| `AZURE_TENANT_ID` | `az account show --query tenantId -o tsv` |
| `AZURE_SUBSCRIPTION_ID` | the subscription id |
| `SMOKE_CLIENT_SECRET` | `OpenIddict__ApiClientSecret` (== the seeded `openiddict-api-client-secret`) |

**6. GitHub repo Variables** (non-secret): `SMTP_SENDER_EMAIL`, `ADMIN_USERNAME`, `ADMIN_EMAIL`.

**7. Flip the activation switch — repo Variable `CD_ENABLED` = `true`.** This is the master switch:
the Azure-touching jobs (`deploy-prod`, `infra plan`/`apply`) carry `if: vars.CD_ENABLED == 'true'`,
so until you set it they are **skipped** — merging is inert and `iac/**` PRs stay green before steps
1–6 exist. Set it last, once 1–6 are done; unset it to pause all deployment without touching code.

## Database migrations

### Strategy (ADR-0008 §6)

Schema changes apply as a **pre-deploy job** — a one-shot container that runs to completion *before*
the APIs serve traffic — never from inside the application at startup. The rules:

- **Two write contexts, one job.** The Translation Management System (`ApplicationWriteDbContext`,
  schema `translation`, database `lotro_translation`) and the Auth server (`AuthDbContext`, schema
  `auth`, database `lotro_auth`) each have their own migration history. The job applies Translation
  first, then Auth.
- **The artifact is the migrator image** `ghcr.io/koniecdev/lotrokoniecdev-migrator` — built by
  `Dockerfile.migrator.prod` and published by CD (M6-09). It bakes two **self-contained**
  `dotnet ef migrations bundle` executables (one per context) onto a lean `runtime-deps` base — no
  SDK, no `dotnet-ef` tool, no source, no separate .NET runtime. It is ~10× smaller than the dev SDK
  migrator and carries everything it needs to apply migrations against a connection string.
- **Idempotent.** Each bundle applies only the migrations missing from that context's
  `__EFMigrationsHistory` table, so re-running the job is a safe no-op.
- **Fail-fast = no half-migrated serving.** Any failure (unreachable DB, bad migration, missing
  connection string) exits the job non-zero. Wire the APIs to depend on the job's **success** so a
  failed migration blocks API startup (compose: `depends_on: condition:
  service_completed_successfully`; ACA: a Job the revision waits on; ECS: an `essential` init task /
  a gated pipeline step).
- **Forward-only.** There is no automated rollback step. EF down-migrations exist but are not run by
  this job; a bad migration is rolled forward with a new migration (the repo has zero production
  users — breaking changes are free; ADR-0002). Take a database snapshot before applying in a real
  environment.

### Inputs (environment variables)

The migrator reads exactly two variables — Npgsql connection strings, one per context:

| Variable | Context | Example |
|---|---|---|
| `ConnectionStrings__TranslationDatabase` | TMS write context | `Host=db;Port=5432;Database=lotro_translation;Username=app;Password=…;Ssl Mode=Require;Trust Server Certificate=true` |
| `ConnectionStrings__AuthDatabase` | Auth context | `Host=db;Port=5432;Database=lotro_auth;Username=app;Password=…;Ssl Mode=Require;Trust Server Certificate=true` |

Both databases must already exist (a managed Postgres typically provisions one DB; create the second
with `CREATE DATABASE "lotro_auth";` — `scripts/init-postgres.sh` does this for the self-hosted
parity Postgres).

### Running migrations

**Local production-parity stack (`compose.prod.yaml`).** Automatic — the `migrator` service runs the
bundle image to completion and `auth-api` / `tms-api` wait on its success:

```bash
scripts/up-prod.sh --build          # PowerShell: scripts/up-prod.ps1 --build
docker compose -f compose.prod.yaml --env-file .env.prod logs migrator   # watch it apply
```

**A real environment (cloud pre-deploy job, run by an operator).** Pull the published image and run
it once against the target database, before rolling out the new API revision:

```bash
docker run --rm \
  -e ConnectionStrings__TranslationDatabase="Host=…;Database=lotro_translation;Username=…;Password=…;Ssl Mode=Require;Trust Server Certificate=true" \
  -e ConnectionStrings__AuthDatabase="Host=…;Database=lotro_auth;Username=…;Password=…;Ssl Mode=Require;Trust Server Certificate=true" \
  ghcr.io/koniecdev/lotrokoniecdev-migrator:<tag>
```

Use the **same image tag** (commit SHA or `vX.Y.Z`) you are about to deploy for the APIs, so schema
and code move together. Expected tail on success:

```
== TRANSLATION MIGRATOR DONE ==
== AUTH MIGRATOR DONE ==
== MIGRATOR COMPLETE ==
```

A non-zero exit means migrations did **not** fully apply — do not roll out the APIs; read the log,
fix forward, re-run.

- **Azure Container Apps:** run the image as a manual/scheduled **Container Apps Job** (or an
  `az containerapp job start`) gating the revision rollout.
- **AWS ECS:** run it as a one-off **RunTask** (or a CodePipeline/CodeBuild step) that the service
  update waits on.

### Authoring a new migration (developer, not operator)

Migrations are generated with the SDK + `dotnet-ef` against the source — see the CLAUDE.md "TMS — EF
Core migrations" command block for the exact `dotnet ef migrations add …` invocations (TMS resolves
through its Persistence project; Auth through its API project — only those two carry EF Core Design).
Once committed, the next CD build bakes them into the migrator image automatically; operators never
generate migrations.

### Verifying

```bash
# applied Translation migrations
psql "$ConnectionStrings__TranslationDatabase" -c 'SELECT "MigrationId" FROM translation."__EFMigrationsHistory" ORDER BY "MigrationId";'
# applied Auth migrations
psql "$ConnectionStrings__AuthDatabase"        -c 'SELECT "MigrationId" FROM auth."__EFMigrationsHistory" ORDER BY "MigrationId";'
```

## Post-deploy smoke test

One command that gives a green/red signal that a deployed environment came up correctly, without
manual clicking — run it as the final [bring-up](#bringing-the-stack-up) step (after migrations + a
browser login) and after every subsequent deploy. `scripts/smoke.sh` (with a `scripts/smoke.ps1`
twin per repo convention) takes the three base URLs + the OpenIddict API client secret and exercises
the four legs that actually break on a deploy:

| # | Check | Pass condition |
|---|---|---|
| 1 | **Health** | `GET {auth}/health/ready` = 200, `GET {tms}/health/ready` = 200, `GET {frontend}/` = 2xx/3xx |
| 2 | **OIDC token** | `POST {auth}/connect/token` (client_credentials) = 200 + an `access_token` |
| 3 | **Token accepted by tms** | anonymous `GET {tms}/api/v1/game-versions` = **401**; the same call **with** the bearer token is **NOT 401** |
| 4 | **File distribution** | `GET {tms}/api/v1/translation-files/{lang}` = 200 + `ETag`, then a re-GET with `If-None-Match` = 304 |

It prints a `✓`/`✗`/`⚠` per check and **exits non-zero (1) on any failure** (a usage/config problem
exits 2); CI consumers and `&&` chains can rely on the exit code. Two behaviours are deliberate and
worth knowing before you read a result:

- **Leg 3 expects 403, and that is success.** The only non-interactive OIDC grant in a deployed
  environment is **client-credentials** (the web client needs a browser; the password-flow client is
  seeded only in `Testing`). A client-credentials token carries **no user role**, and every TMS
  endpoint is role-gated — so a *validated* token is **403 Forbidden**, not 200. The check therefore
  proves the token is **accepted** (got past authentication), pairing it with an anonymous 401 to
  prove the endpoint is genuinely protected. A **401 with a valid token is the real red flag**: it
  means tms rejected it — almost always an issuer / audience / JWKS mismatch (see
  [Consistency rules that bite](#consistency-rules-that-bite), rules #1–#2), the classic
  "works locally, 401 on staging" failure this leg exists to catch.
- **Leg 4 warns (does not fail) on 404.** A freshly deployed but not-yet-imported environment has no
  translation artifact, so the endpoint returns 404 — the endpoint is up, there is just nothing to
  distribute yet. That is a `⚠` warning, not a failure (the run can still pass); a green 200 + 304
  appears once an import/seed has run.

### Running it

```bash
# A real environment (publicly-trusted ingress cert — no --insecure needed):
SMOKE_CLIENT_SECRET="$OPENIDDICT_API_CLIENT_SECRET" scripts/smoke.sh \
  --auth-url     https://auth.lotro.koniec.dev \
  --tms-url      https://tms.lotro.koniec.dev \
  --frontend-url https://lotro.koniec.dev
# PowerShell twin: $env:SMOKE_CLIENT_SECRET='…'; scripts/smoke.ps1 -AuthUrl … -TmsUrl … -FrontendUrl …

# The local prod-parity stack (compose.prod.yaml): client secret is the generated value in .env.prod;
# certs are the local CA, so add --insecure (or trust .docker/prod-https/rootCA.crt):
scripts/smoke.sh --insecure \
  --auth-url https://auth.lotro.test --tms-url https://tms.lotro.test --frontend-url https://app.lotro.test \
  --client-secret "$(grep '^OpenIddict__ApiClientSecret=' .env.prod | cut -d= -f2-)"

# The local dev stack (host Kestrels + untrusted dev cert): the dev API client secret is the well-known
# appsettings.Development.json value:
scripts/smoke.sh --insecure \
  --auth-url https://localhost:5003 --tms-url https://localhost:5002 --frontend-url https://localhost:7017 \
  --client-secret dev-api-secret-min-32-characters-long
```

Each flag has a `SMOKE_*` environment fallback (`--auth-url`/`SMOKE_AUTH_URL`,
`--tms-url`/`SMOKE_TMS_URL`, `--frontend-url`/`SMOKE_FRONTEND_URL`,
`--client-secret`/`SMOKE_CLIENT_SECRET`, `--client-id`/`SMOKE_CLIENT_ID` (default
`lotrokoniecdev-api`), `--scope`/`SMOKE_SCOPE` (default `service`), `--lang`/`SMOKE_LANG` (default
`pl`), `--timeout`/`SMOKE_TIMEOUT` (default 15), `--insecure`/`SMOKE_INSECURE=1`). The `--client-secret`
is the auth server's `OpenIddict__ApiClientSecret` (see [Generating secrets](#generating-secrets));
`bash scripts/smoke.sh --help` prints the full reference.

### In CI

The [`Smoke test`](../../.github/workflows/smoke.yml) workflow is **`workflow_call`-able and is
called automatically by `cd.yml`'s `deploy-prod` after every prod rollout** (ADR-0012) — a deploy
that comes up wrong fails loudly. It also stays runnable **on demand** (`workflow_dispatch` — enter
the three URLs); the secret comes from the repository secret `SMOKE_CLIENT_SECRET`. See
[Continuous deployment (CI/CD)](#continuous-deployment-cicd).

## See also

- [`.env.example`](../../.env.example) — the dev-compose env template (`scripts/up.sh` bootstraps `.env` from it).
- [`.env.prod.example`](../../.env.prod.example) — the production-parity env template: secrets + the
  managed-DB swap point (`scripts/up-prod.sh` bootstraps `.env.prod` from it, generating the OpenIddict secrets).
- [`compose.yaml`](../../compose.yaml) / [`compose.prod.yaml`](../../compose.prod.yaml) — the dev and
  production-parity stacks; the literal env→container wiring this matrix abstracts.
- [ADR-0008](../adr/0008-cloud-agnostic-deployment-and-environment-strategy.md) — the cloud-agnostic
  deployment & environment strategy this runbook operationalizes.
- [`target-requirements.md`](target-requirements.md) — platform requirements + Azure⇄AWS service
  mapping (M6-12).
