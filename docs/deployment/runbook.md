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
- [Continuous deployment (CI/CD)](#continuous-deployment-cicd) — build-once → auto staging → gated prod promotion, and the one-time operator setup
- [Database migrations](#database-migrations) — strategy, running them, and recovering from a bad migration (Neon PITR + the MIGR-04 auto-snapshot)
- [Post-deploy smoke test](#post-deploy-smoke-test) — one command verifies a deployed environment end-to-end
- [Observability](#observability) — where cloud traces + logs land, and the metrics caveat
- [Monitoring & alerting](#monitoring--alerting) — the Azure Monitor alerts and what each one means
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
  **production placeholders**; substitute `lotro-translator.pl` with your environment's domain (e.g. a
  `*.staging.lotro-translator.pl` for staging). The local production-parity stack
  (`compose.prod.yaml`) wires the very same keys with `*.lotro.test` hostnames.

Purely optional tuning knobs with safe defaults are omitted (e.g. `OpenIddict:AccessTokenLifetimeMinutes`
= 60, `OpenIddict:RefreshTokenLifetimeDays` = 14, `Import:*`, `TranslationFileRebuild:DebounceWindow`
= 2 s (ADR-0021), `Email:TimeoutSeconds`/`MaxSendAttempts`, `AllowedHosts` = `*`).

> ⚠️ **Live prod domain is `lotro-translator.pl`** — auth → `https://auth.lotro-translator.pl`,
> tms → `https://tms.lotro-translator.pl`, frontend → `https://lotro-translator.pl`; live RG
> `rg-lotrotms-prod-polc-001`. The tables below show these live production values directly; for
> staging substitute your environment's own domain.

### auth-api

| Variable | local-dev | Staging / Production (placeholder) | Required? | Source | Notes |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` | ✅ all | plain | Gates fail-fast validation, ephemeral-vs-real keys, the CORS policy. |
| `ASPNETCORE_URLS` | `https://localhost:5003` (launchSettings) | `http://+:8080` | ✅ all | plain | Host dev Kestrel (native dev cert); prod serves HTTP only (the ingress owns TLS). |
| `ConnectionStrings__AuthDatabase` | `…;Database=lotro_auth;…;Password=changeme` (appsettings.Development) | `Host=…;Database=lotro_auth;Username=…;Password=…;Ssl Mode=Require;Trust Server Certificate=true` | ✅ all | **secret** | Carries the DB password. Host dev hits the compose Postgres on `localhost:5432`. Managed DB: keep `Ssl Mode=Require`, drop `Trust Server Certificate`. |
| `OpenIddict__Issuer` | `https://localhost:5003` | `https://auth.lotro-translator.pl` | ✅ non-dev | plain | **THE token `iss`.** Absolute http(s), no `localhost`. Must equal tms `Auth__Issuer`. |
| `OpenIddict__SigningKey__RsaPrivateKeyXml` | — (ephemeral) | base64 of RSA XML (≥2048-bit) | ✅ non-dev | **secret** | Generate with `gen-openiddict-keys`. |
| `OpenIddict__EncryptionKey__Key` | — (ephemeral) | base64 of a ≥32-byte key | ✅ non-dev | **secret** | Generate with `gen-openiddict-keys`. |
| `OpenIddict__ApiClientSecret` | `dev-api-secret-min-32-characters-long` | ≥32-char random secret | ✅ non-dev | **secret** | Generate with `gen-openiddict-keys`. Shared with the service client. |
| `OpenIddict__WebClient__RedirectUris__0` | `https://localhost:7017/callback` | `https://lotro-translator.pl/callback` | ✅ non-dev | plain | MUST equal the Frontend callback (its public origin + `AuthSystem__CallbackPath`). |
| `OpenIddict__WebClient__PostLogoutRedirectUris__0` | `https://localhost:7017` | `https://lotro-translator.pl` | ✅ non-dev | plain | Frontend post-logout return URL. |
| `Cors__AllowedOrigins__0` | — (AllowAnyOrigin) | `https://lotro-translator.pl` | ✅ non-dev | plain | Bare origin = Frontend public URL. Lowercase, no port-if-default, no path/slash. |
| `DataProtection__KeyRingPath` | — (host default) | `/keys` | ✅ non-dev | plain | Persistent, replica-shared volume; else logins/antiforgery/reset links break on deploy/scale. |
| `Email__Host` | `mailpit` | `smtp.sendgrid.net` | ✅ all | plain | SMTP host. Validated on start (every environment). |
| `Email__Port` | `1025` | `587` | ✅ all | plain | 1–65535. |
| `Email__Mode` | `None` | `StartTls` | ✅ all | plain | One of `None` / `StartTls` / `TLS`. |
| `Email__SenderEmail` | `noreply@lotro-translator.pl` | `no-reply@lotro-translator.pl` | ✅ all | plain | Must be a valid email. |
| `Email__Sender` | `lotro-translator.pl` | `LOTRO PL` | ✅ all | plain | Display name. |
| `Email__Username` / `Email__Password` | — | provider credentials | optional¹ | **secret** (Password) | ¹If `Username` is set, `Password` is required. |
| `AdminUser__Username` / `AdminUser__Email` / `AdminUser__Password` | from `AUTH_ADMIN_*` | from `AUTH_ADMIN_*` | optional | **secret** (Password) | Seeds one admin on first boot; leave blank to skip. Username must match `^[a-zA-Z0-9]+$` (ADR-0022) or auth-api fails at startup; the admin logs in **by e-mail**. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://aspire-dashboard:18889` | OTLP collector URL | optional | plain | Empty = telemetry export disabled. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` | `grpc` / `http/protobuf` | optional | plain | Defaults to `grpc`. |

### tms-api

| Variable | local-dev | Staging / Production (placeholder) | Required? | Source | Notes |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` | ✅ all | plain | Gates fail-fast validation + the CORS policy. |
| `ASPNETCORE_URLS` | `https://localhost:5002` (launchSettings) | `http://+:8080` | ✅ all | plain | Host dev Kestrel (native dev cert); prod serves HTTP only (ingress owns TLS). |
| `ConnectionStrings__TranslationDatabase` | `…;Database=lotro_translation;…;Password=changeme` (appsettings.Development) | `Host=…;Database=lotro_translation;Username=…;Password=…;Ssl Mode=Require;Trust Server Certificate=true` | ✅ all | **secret** | TMS write context. Host dev hits the compose Postgres on `localhost:5432`. Managed DB swap = change just this value. |
| `Auth__Issuer` | `https://localhost:5003` | `https://auth.lotro-translator.pl` | ✅ all | plain | MUST equal auth `OpenIddict__Issuer` (the token `iss`); tokens are rejected otherwise. |
| `Auth__Authority` | — (unset → falls back to `Issuer`, `https://localhost:5003`) | `https://auth.lotro-translator.pl` | optional² | plain | Back-channel for OIDC metadata + JWKS. ²Unset → falls back to `Issuer`; the dev host run relies on that fallback to reach the host auth Kestrel. Prod: must be `https` (OpenIddict rejects plain HTTP) and reachable from the container. |
| `Auth__Audience` | `lotrokoniecdev-api` | `lotrokoniecdev-api` | ✅ all | plain | Default in base `appsettings.json`. |
| `Cors__AllowedOrigins__0` | — (AllowAnyOrigin) | `https://lotro-translator.pl` | ✅ non-dev | plain | Bare origin = Frontend public URL. |
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
| `AuthSystem__Authority` | `https://localhost:5003` | `https://auth.lotro-translator.pl` | ✅ all | plain | Drives the `/authorize` redirect (front-channel) **and** discovery/token (back-channel). |
| `AuthSystem__BaseUrl` | `https://localhost:5003/` | `https://auth.lotro-translator.pl/` | ✅ all | plain | Auth origin (trailing slash). |
| `AuthSystem__ClientId` | `lotrokoniecdev-web` | `lotrokoniecdev-web` | ✅ all | plain | Must match the OpenIddict web-client id. |
| `AuthSystem__CallbackPath` | `/callback` | `/callback` | ✅ all | plain | Origin + this MUST be registered in auth `RedirectUris`. |
| `AuthSystem__SignedOutCallbackPath` | `/signout-callback-oidc` | `/signout-callback-oidc` | ✅ all | plain | Origin + this MUST be in auth `PostLogoutRedirectUris`. |
| `AuthSystem__Scopes` | `openid,email,profile,roles,api,offline_access` | same | ✅ all | plain | At least one scope. |
| `TranslationSystem__BaseUrl` | `https://localhost:5002/` | `https://tms.lotro-translator.pl/` | ✅ all | plain | TMS API origin (trailing slash). |
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
AUTH_ADMIN_USERNAME=…        # letters + digits only (^[a-zA-Z0-9]+$) — ADR-0022
AUTH_ADMIN_EMAIL=…
AUTH_ADMIN_PASSWORD=…        # secret
```

The username is a display-only handle; the seeded admin **logs in by e-mail + password**. A
username containing `-`, `.`, `_`, spaces or diacritics fails Identity's
`AllowedUserNameCharacters` and crashes auth-api at startup (loud, by design) — re-set any such
deployed `AUTH_ADMIN_USERNAME` value to alphanumeric before rolling this version out.

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

### Per-environment Terraform (state key + staging)

The `iac/` root is a single parametrized module; an environment is **one var-file + one state blob**
(ADR-0017). The `azurerm` backend is **partial** — the state key is chosen at `init`, and prod runs
purely on the `vars.tf` defaults:

```bash
cd iac
# prod (default — no var-file; vars.tf defaults are already prod-correct):
terraform init -reconfigure -backend-config=backend-config/prod.hcl
terraform apply                                    # CI does this behind the production gate

# staging (separate state blob, separate Key Vault + Neon project — see prerequisites):
terraform init -reconfigure -backend-config=backend-config/staging.hcl
terraform apply -var-file=env/staging.tfvars
```

`prod.terraform.tfstate` and `staging.terraform.tfstate` are separate blobs in the one backend
container, so a botched staging apply can never corrupt prod state. `var.public_base_domain` derives
every OIDC issuer / redirect / CORS / base URL (`iac/locals.tf`) and `var.env_id` derives the resource
group and every resource name — so the only env-specific edits live in `env/staging.tfvars`.

**Before the first staging apply** (out-of-band — they are Terraform *data sources*, ADR-0017 §7):
seed a **separate** `lotrotms-kv-staging` Key Vault with freshly generated secrets (never the prod
vault — audit §C5), a `lotrotms-aca-staging` identity, and a separate staging Neon project (audit §H13); the
secret-free required inputs (`subscription_id`, `smtp_sender_email`, `admin_username`, `admin_email`)
arrive as `TF_VAR_*`. See [Staging bring-up](#staging-bring-up) for the full ordered first-time sequence.

#### Staging bring-up

First-time sequence for the `staging` environment. Prerequisite: complete the [one-time operator setup](#one-time-operator-setup-your-azure--github) steps 1–7 (federated credential `gh-env-staging`, `Contributor` on `rg-lotrotms-staging-polc-001`, GitHub `staging` environment with env-scoped variables).

> ⚠️ **Staging shares the production ACA Environment** (ADR-0018). The "Azure for Students" subscription
> permits only **one Container Apps Environment in the whole subscription**, so `env/staging.tfvars` sets
> `aca_environment_name = "lotrotmsenvprod"` / `aca_environment_resource_group = "rg-lotrotms-prod-polc-001"`
> and the staging apply creates **only** the staging apps + migrator + DP-keyring storage + env-storage
> links **inside the prod environment** — it does NOT create an environment, Log Analytics, App Insights,
> the OTel agent or alerts (those are `count = 0` for the shared case). Everything else stays separate
> (apps, the Neon project, KV + identity, custom domains). The shared env's default domain and
> `customDomainVerificationId` are the **prod** ones (the same token for all three subdomains).

**1 — Seed the staging Key Vault** (a *separate* vault from prod — audit §C5):

```bash
az login   # Owner / User Access Administrator on the subscription
export KV_RESOURCE_GROUP=rg-lotrotms-staging-polc-001
export KV_NAME=lotrotms-kv-staging
export KV_IDENTITY_NAME=lotrotms-aca-staging

# Generate fresh staging OpenIddict keys and capture them as SEED_* variables:
eval "$(scripts/gen-openiddict-keys.sh | sed 's/^/SEED_/; s/^SEED_#/#/')"
export SEED_OPENIDDICT_SIGNING_KEY SEED_OPENIDDICT_ENCRYPTION_KEY SEED_OPENIDDICT_API_CLIENT_SECRET

# Staging uses a SEPARATE Neon project — different connection strings from prod:
export SEED_CONNECTION_STRING_TRANSLATION='<neon staging lotro_translation connection string>'
export SEED_CONNECTION_STRING_AUTH='<neon staging lotro_auth connection string>'
export SEED_SMTP_USERNAME='<brevo smtp username>'
export SEED_SMTP_PASSWORD='<brevo smtp password>'
export SEED_ADMIN_PASSWORD='<pick a password>'
scripts/seed-keyvault.sh
```

> ⚠️ The `SEED_OPENIDDICT_API_CLIENT_SECRET` captured above is the staging `SMOKE_CLIENT_SECRET`. Note it down — set it as the `SMOKE_CLIENT_SECRET` secret on the GitHub `staging` environment (Settings → Environments → `staging` → Secrets) before enabling CI.

**2 — Apply Terraform for staging:**

```bash
cd iac
terraform init -reconfigure -backend-config=backend-config/staging.hcl
export TF_VAR_subscription_id='<sub>'
export TF_VAR_smtp_sender_email='koniecdev@gmail.com'
export TF_VAR_admin_username='<username>'
export TF_VAR_admin_email='<email>'
terraform apply -var-file=env/staging.tfvars
```

**3 — Bind custom domains + managed certs.** Once the staging Container Apps exist, for each of the three apps run:

```bash
# Repeat for each app (lotrotms-auth-api-staging, lotrotms-tms-api-staging, lotrotms-frontend-staging):
az containerapp hostname add \
  -n <app-name> -g rg-lotrotms-staging-polc-001 \
  --hostname <subdomain>.staging.lotro-translator.pl
az containerapp hostname bind \
  -n <app-name> -g rg-lotrotms-staging-polc-001 \
  --hostname <subdomain>.staging.lotro-translator.pl --validation-method CNAME
```

The `bind` command outputs the **domain validation token** needed for the TXT record in step 4. Collect all three tokens before adding DNS records.

**4 — Add DNS records at the registrar.** The two subdomains are CNAMEs to their app FQDN; the apex-of-the-tier
`staging.lotro-translator.pl` is an **A record to the environment's static IP** (it has children
`auth.staging` / `tms.staging`, so it cannot be a CNAME), exactly like the prod apex. The `asuid` TXT is the
env's `customDomainVerificationId` — **one value, the same for all three** (shared env). The app FQDN is
`<app>.<prod-env-default-domain>` (the shared prod env), the env IP is its `staticIp`
(`az containerapp env show -n lotrotmsenvprod -g rg-lotrotms-prod-polc-001 --query "{ip:properties.staticIp,domain:properties.defaultDomain}"`):

| Host | Record | Value |
|---|---|---|
| `auth.staging.lotro-translator.pl` | CNAME | `lotrotms-auth-api-staging.<prod-env-default-domain>` |
| `tms.staging.lotro-translator.pl` | CNAME | `lotrotms-tms-api-staging.<prod-env-default-domain>` |
| `staging.lotro-translator.pl` | A | `<env staticIp>` |
| `asuid.{,auth.,tms.}staging.lotro-translator.pl` | TXT (×3) | `<customDomainVerificationId>` |

Bind the apex with `--validation-method HTTP` (A record), the two subdomains with `--validation-method CNAME`.

**5 — Verify staging** with the smoke test:

```bash
SMOKE_CLIENT_SECRET="$SEED_OPENIDDICT_API_CLIENT_SECRET" scripts/smoke.sh \
  --auth-url     https://auth.staging.lotro-translator.pl \
  --tms-url      https://tms.staging.lotro-translator.pl \
  --frontend-url https://staging.lotro-translator.pl
```

**6 — Enable the CI pipeline.** Set both repo Variables — `CD_ENABLED=true` and `STAGING_ENABLED=true` — to activate the two-stage promotion. From this point every merge to `main` auto-deploys to staging; the prod gate waits for your approval.

> Audit 0001 §H10 (staging as the production-gate predecessor) is addressed by this epic. Cross-reference: ADR-0018 (staging environment + two-stage promotion model — being authored in a sibling PR).

## Continuous deployment (CI/CD)

Ongoing delivery to the live Azure environment is automated (ADR-0012). Four workflows (with `STAGING_ENABLED=true`):

| Workflow | Trigger | What it does |
|---|---|---|
| `cd.yml` → `build-and-push` | push to main, `v*` tag | builds the 4 images, **scans each with Trivy (fails on a fixable HIGH/CRITICAL)**, then pushes to GHCR **signed (cosign keyless) + attested (SLSA provenance + SBOM)** — `:sha-<short>`, `:latest` on main, semver on tags (audit 0001 H9 + H1) |
| `cd.yml` → `deploy-staging` | push to main / dispatch, `STAGING_ENABLED == true`, **auto** (no approval) | identical health-gated rollout as `deploy-prod` targeting the `staging` environment; runs automatically after `build-and-push` (audit 0001 H10, ADR-0018) |
| `cd.yml` → `deploy-prod` | push to main / dispatch, `STAGING_ENABLED == true`, **behind the `production` approval gate**, `needs: deploy-staging` | the whole health-gated rollout in ONE job (every Azure step shares the approved environment's OIDC identity): **pin every image tag to its immutable digest, then verify the digest's signed provenance** (`gh attestation verify`, fail-closed; every subsequent `az` step ships the digest, so a tag moved mid-rollout changes nothing) → OIDC login → migrator to success (**gate**) → deploy each app as a **candidate at 0% traffic** (`--revision-suffix`, labelled `cd-candidate`) → readiness (incl. frontend) + warm the auth origin → **smoke the candidate** inline (`scripts/smoke.sh`) → **promote** 100% traffic + deactivate the previous revision → **smoke production** → **roll back on any failure** (restore traffic to the previous revision, deactivate the candidate) — audit 0001 H7 |
| `infra.yml` | PR / push to `iac/**`, dispatch | **`plan`** (prod + staging matrix) on PRs (preview in run summary); `apply-staging` (**auto**) → `apply-prod` (**gated**) on main |

**The two-stage promotion model.** When `STAGING_ENABLED=true`, every merge to main builds once and **automatically deploys to staging** (no approval needed — `deploy-staging` is auto). The same `sha-<short>` then **waits at the `production` environment** for a human to approve. Clicking *Approve* (GitHub → the run → *Review deployments* → *Approve*) **is** the staging→prod promotion: the identical image that passed staging is what production receives. Test on staging first; approve when ready.

**The `STAGING_ENABLED` master switch** (repo Variable): `false` (or unset) = staging jobs are skipped and `deploy-prod` reverts to a direct single gate (the pre-staging behavior); `true` = staging auto-deploys and prod is gated behind a green staging (`needs: deploy-staging`). Pairs with `CD_ENABLED`: both must be `true` for the full two-stage flow; `CD_ENABLED=false` stops all Azure-touching jobs regardless of `STAGING_ENABLED`.

**Deploy a specific build on demand.** Actions → *CD* → *Run workflow* → optional `image_tag`
(`sha-<short>` or `vX.Y.Z`); empty = the chosen ref's commit. Still gated. The image must carry a
build-provenance attestation from our CD or the **verify gate fails closed** (audit 0001 H9) — any
image built since that change has one; an older pre-H9 tag must be rebuilt from its commit.

**Roll back.** A *failed* rollout rolls back **automatically** (audit 0001 H7): the health-gated
pipeline shifts traffic only after the candidate passes smoke, and on any failure the rollback step
(in `deploy-prod`) restores 100% of traffic to the previous revision and deactivates the candidate —
so a bad release never serves users. To revert a release *after* it was promoted, either re-run *CD* via dispatch with
`image_tag` = the previous good `sha-<short>` (the rollout re-pins it to its digest and re-verifies
its provenance) and approve, or — for an
instant manual revert — shift traffic back to the still-present previous revision:
`az containerapp ingress traffic set -n <app> -g <rg> --revision-weight <prev>=100 <candidate>=0`.
DB migrations are forward-only — roll the schema forward, not back (ADR-0023); a bad migration is
recovered by a Neon restore — the deploy's pre-migration auto-snapshot or PITR, see
[Restore from the auto-snapshot](#restore-from-the-auto-snapshot-migr-04).

### One-time operator setup (your Azure + GitHub)

Keyless via OIDC federation — no client secret is stored. Run once.

**1. Entra app + federated credentials** (the `subject` strings must match exactly):

```bash
APP_ID=$(az ad app create --display-name "github-lotrotms-cd" --query appId -o tsv)
az ad sp create --id "$APP_ID"
# gated jobs (deploy-prod + terraform apply-prod both run under environment: production)
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name":"gh-env-production","issuer":"https://token.actions.githubusercontent.com",
  "subject":"repo:koniecdev/LotroKoniecDev:environment:production",
  "audiences":["api://AzureADTokenExchange"]}'
# auto job (deploy-staging runs under environment: staging)
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name":"gh-env-staging","issuer":"https://token.actions.githubusercontent.com",
  "subject":"repo:koniecdev/LotroKoniecDev:environment:staging",
  "audiences":["api://AzureADTokenExchange"]}'
# infra PR plan job (no environment)
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name":"gh-pull-request","issuer":"https://token.actions.githubusercontent.com",
  "subject":"repo:koniecdev/LotroKoniecDev:pull_request",
  "audiences":["api://AzureADTokenExchange"]}'
```

**2. RBAC** — Contributor on all target RGs (roll apps + manage infra + read/write tfstate):

```bash
SP_OBJ=$(az ad sp show --id "$APP_ID" --query id -o tsv)
SUB=$(az account show --query id -o tsv)
for RG in rg-lotrotms-prod-polc-001 rg-lotrotms-staging-polc-001 rg-lotrotms-tfstate; do
  az role assignment create --assignee-object-id "$SP_OBJ" --assignee-principal-type ServicePrincipal \
    --role Contributor --scope "/subscriptions/$SUB/resourceGroups/$RG"
done
```

**3. GitHub environments** — two environments, different protection:

- **`staging`** (auto — no required reviewers): repo Settings → Environments → New → `staging` — leave *Required reviewers* empty.
- **`production`** (gated): repo Settings → Environments → New → `production` → *Required reviewers* → add yourself. **This is the gate** — nothing reaches prod until you click *Approve*.

**4. Seed the app secrets into Azure Key Vault (ADR-0013).** The 8 app secrets are the single source
of truth in Key Vault (`lotrotms-kv-prod`), read at runtime by the `lotrotms-aca-prod` managed
identity — they are **not** GitHub Secrets and **not** Terraform inputs (no plaintext on disk, in the
TF state, or in CI). The idempotent seed script (also the rotation tool) ensures the Vault, the
identity, its `Key Vault Secrets User` grant, and the 8 secrets. It needs **Owner / User Access
Administrator** once — it creates a role assignment, which is why CI never does and stays Contributor:

```bash
az login   # an Owner / User Access Administrator on the subscription
export SEED_CONNECTION_STRING_TRANSLATION='…'   # Neon TMS connection string
export SEED_CONNECTION_STRING_AUTH='…'          # Neon Auth connection string
export SEED_OPENIDDICT_SIGNING_KEY='…'          # base64 RSA xml  (scripts/gen-openiddict-keys.sh)
export SEED_OPENIDDICT_ENCRYPTION_KEY='…'       # base64 32-byte key      (same generator)
export SEED_OPENIDDICT_API_CLIENT_SECRET='…'    # >= 32 chars  (== prod SMOKE_CLIENT_SECRET)
export SEED_SMTP_USERNAME='…' SEED_SMTP_PASSWORD='…'   # Brevo SMTP credentials
export SEED_ADMIN_PASSWORD='…'                  # seeded admin password
scripts/seed-keyvault.sh                        # PowerShell twin: scripts/seed-keyvault.ps1
```

Rotation later = re-run the script with new `SEED_*` values; the versionless Key Vault URIs make the
next deployment pick them up with no Terraform change.

The block above seeds the **production** Key Vault (`lotrotms-kv-prod`). The staging Key Vault (`lotrotms-kv-staging`) is seeded separately with its own freshly generated secrets — see [Staging bring-up](#staging-bring-up).

**5. GitHub repo Secrets** (Settings → Secrets and variables → Actions → *Secrets*) — **OIDC infra
only**, no app secrets (those live in Key Vault, step 4):

| Secret | Value |
|---|---|
| `AZURE_CLIENT_ID` | the Entra app id (`$APP_ID`) |
| `AZURE_TENANT_ID` | `az account show --query tenantId -o tsv` |
| `AZURE_SUBSCRIPTION_ID` | the subscription id |

**6. GitHub environment Variables and Secrets** (Settings → Environments → `<environment>` → Variables / Secrets). Set these on **both** the `staging` and `production` environments:

| Variable | `staging` | `production` |
|---|---|---|
| `RESOURCE_GROUP` | `rg-lotrotms-staging-polc-001` | `rg-lotrotms-prod-polc-001` |
| `AUTH_APP` | `lotrotms-auth-api-staging` | `lotrotms-auth-api-prod` |
| `TMS_APP` | `lotrotms-tms-api-staging` | `lotrotms-tms-api-prod` |
| `FRONTEND_APP` | `lotrotms-frontend-staging` | `lotrotms-frontend-prod` |
| `MIGRATOR_JOB` | `lotrotms-migrator-staging` | `lotrotms-migrator-prod` |
| `NEON_PROJECT_ID` *(optional — MIGR-04 snapshot leg)* | `holy-mode-18368797` | `empty-voice-65414159` |
| `AUTH_URL` | `https://auth.staging.lotro-translator.pl` | `https://auth.lotro-translator.pl` |
| `TMS_URL` | `https://tms.staging.lotro-translator.pl` | `https://tms.lotro-translator.pl` |
| `FRONTEND_URL` | `https://staging.lotro-translator.pl` | `https://lotro-translator.pl` |

And the env-scoped **secrets** on each environment:

- `SMOKE_CLIENT_SECRET` — that environment's `SEED_OPENIDDICT_API_CLIENT_SECRET` (the value seeded
  into the environment's Key Vault in step 4 / [Staging bring-up](#staging-bring-up)).
- `NEON_API_KEY` *(optional — MIGR-04 snapshot leg)* — a **project-scoped** organization API key
  for that environment's Neon project (least privilege: single project, member-level, no org
  actions). Mint it via the Neon API — the response's `key` field is shown once; the account-level
  key used below is any org API key with permission to create keys:

  ```bash
  curl -sf -X POST -H "Authorization: Bearer $NEON_ACCOUNT_API_KEY" -H "Content-Type: application/json" \
    "https://console.neon.tech/api/v2/organizations/<org_id>/api_keys" \
    -d '{"key_name": "ci-neon-snapshot-<env>", "project_id": "<that env's NEON_PROJECT_ID>"}'
  ```

  Leave `NEON_API_KEY`/`NEON_PROJECT_ID` unset and the deploy skips the snapshot leg cleanly — see
  [Restore from the auto-snapshot](#restore-from-the-auto-snapshot-migr-04).

**7. GitHub repo Variables** (non-secret): `SMTP_SENDER_EMAIL`, `ADMIN_USERNAME`, `ADMIN_EMAIL`.

**8. Flip the activation switches** — two repo Variables:

- **`CD_ENABLED` = `true`**: the master switch. The Azure-touching jobs (`deploy-staging`, `deploy-prod`, `infra plan`/`apply`) carry `if: vars.CD_ENABLED == 'true'`, so until you set it they are **skipped** — merging is inert and `iac/**` PRs stay green before steps 1–7 exist. Set last, once 1–7 are done; unset to pause all deployment without touching code.
- **`STAGING_ENABLED` = `true`**: enables the two-stage promotion (auto staging + gated prod). Set after the staging environment is provisioned and the `SMOKE_CLIENT_SECRET` is wired on the `staging` GitHub environment (see [Staging bring-up](#staging-bring-up)). Can be set at the same time as `CD_ENABLED`.

## Database migrations

### Strategy (ADR-0008 §6)

Schema changes apply as a **pre-deploy job** — a one-shot container that runs to completion *before*
the APIs serve traffic — never from inside the application at startup. The rules:

- **Two write contexts, one job.** The Translation Management System (`ApplicationWriteDbContext`,
  schema `translation`, database `lotro_translation`) and the Auth server (`AuthDbContext`, schema
  `authsystem`, database `lotro_auth`) each have their own migration history. The job applies Translation
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
- **Forward-only (ADR-0023).** There is no automated rollback step. EF down-migrations exist but are
  not run by this job; a bad migration is rolled forward with a new migration (the repo has zero
  production users — breaking changes are free; ADR-0002). The recovery valve for a logically-bad
  migration is a Neon restore — the deploy's pre-migration auto-snapshot (MIGR-04) or a
  point-in-time restore — see
  [Recover from a bad migration (Neon PITR)](#recover-from-a-bad-migration-neon-pitr).

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
psql "$ConnectionStrings__AuthDatabase"        -c 'SELECT "MigrationId" FROM authsystem."__EFMigrationsHistory" ORDER BY "MigrationId";'
```

### Recover from a bad migration (Neon PITR)

Forward-only (ADR-0023) means a logically-bad or data-corrupting migration is **never** undone by a
down-migration in a real environment: the deploy's migrator gate commits the schema *before* any
traffic moves, and the pipeline's failure path rolls back **app code only**. The real safety valve
is the database's own history: both real environments run on Neon (prod — ADR-0014; staging — a
separate Neon project, ADR-0018), and Neon keeps continuous page history that supports an instant
point-in-time restore of a branch to any moment inside the retention window.

The topology below was read live via the Neon API on **2026-07-05** (`GET /projects`,
`GET /projects/{id}/branches` — auth: `Authorization: Bearer $NEON_API_KEY`, key minted in the Neon
console under *Account → API keys*; list calls need `?org_id=…`):

| Environment | Neon project | Branch (single, default) | History retention |
|---|---|---|---|
| production | `lotro-translator-prod` (`empty-voice-65414159`) | `production` (`br-jolly-river-as0a1b99`) | **6 h** (21600 s) |
| staging | `lotro-translator-staging` (`holy-mode-18368797`) | `production` (`br-sweet-band-as9xg1ut`) | **6 h** (21600 s) |

- **6 hours is the Free-plan ceiling** — a longer window requires a paid Neon plan (Launch: up to
  7 days; Scale: up to 30 days — at the time of writing). Re-verify the live value anytime:

  ```bash
  curl -s -H "Authorization: Bearer $NEON_API_KEY" \
    https://console.neon.tech/api/v2/projects/<project_id> | jq .project.history_retention_seconds
  ```

- **A branch restore is branch-wide.** `lotro_translation` and `lotro_auth` live on the same branch,
  so they always rewind **together** — which is exactly right for "undo the migrator run", since the
  one migrator job migrates both contexts.
- **Executor: the maintainer.** Single-operator project — needs a Neon API key (or the Neon console
  UI) plus `az` CLI access to the environment's resource group for the traffic steps.

#### Risk boundary — the accepted backup posture (MIGR-01, 2026-07)

**Neon-PITR-only; no off-platform logical backup is scheduled.** Consequence, stated plainly:
**a bad migration (or any data corruption) noticed more than 6 hours after the fact is
unrecoverable.** The blast radius today is a DB whose content is re-creatable by hand (re-import
`exported.txt`, re-seed the admin — zero production users, ADR-0002), which is why the window is
accepted. Revisit trigger: **the first real translators start contributing** (their edits are
*not* re-creatable) → add a nightly `pg_dump` (encrypted — this repo is public, so world-readable
GitHub artifacts are out — to a private Azure Blob container), or raise the Neon plan.

**MIGR-04 (#339) is in place** (2026-07): every configured deploy auto-branches Neon right before
the migrator runs, which caps the **bad-migration** case regardless of the 6 h window — see
[Restore from the auto-snapshot](#restore-from-the-auto-snapshot-migr-04). General data corruption
(not tied to a migrator run) is still bound by the 6 h window above.

#### Procedure

Scenario: the migrator gate ran a migration that is *executionally* fine but *logically* wrong —
dropped or corrupted data, or broke the serving revision past what N-1 compatibility (ADR-0023)
guarantees. Time matters: **the restore point must still be inside the 6 h retention window.**

**0. Find the restore point.** The deploy run's step summary prints a **"DB restore point
(pre-migration)"** table — the UTC timestamp captured immediately *before* the migrator job started,
plus the target migration per context. Since MIGR-04 the same table also names the **auto-snapshot
branch**; when it shows one, prefer
[Restore from the auto-snapshot](#restore-from-the-auto-snapshot-migr-04) — no timestamp math, no
6 h pressure. Fallback for older runs: the log timestamp of the
"Run migrations (gate …)" step in the GitHub Actions run.

**1. Park traffic on the last-good app revision first.** After the restore the schema is
pre-migration again, and only the previous release's code is guaranteed against it (N-1 holds one
step back — *new* code on the *old* schema is exactly the combination nothing proves). If the
rollout already promoted the candidate — the bad migration usually surfaces *after* a green
deploy — steer 100% of traffic back to the previously-serving revision, per app
(`lotrotms-auth-api-prod`, `lotrotms-tms-api-prod`, `lotrotms-frontend-prod`; the staging twins
end in `-staging` — Terraform names every app `lotrotms-…-<env_id>`):

```bash
az containerapp revision list -n <app> -g <resource-group> \
  --query "[].{name:name,active:properties.active,traffic:properties.trafficWeight,created:properties.createdTime}" -o table
# Promote deactivates the previous revision (min-replicas reclaim) — re-activate it BEFORE steering traffic:
az containerapp revision activate -n <app> -g <resource-group> --revision <previous-revision>
az containerapp ingress traffic set -n <app> -g <resource-group> --revision-weight <previous-revision>=100
az containerapp revision deactivate -n <app> -g <resource-group> --revision <bad-revision>
```

(When the rollout fails by itself, the pipeline's "Roll back on failure" step already does this —
then only steps 2–4 remain.)

**2. Restore the Neon branch to just before the migrator ran.** (If step 0 surfaced an
auto-snapshot branch, [restore from it](#restore-from-the-auto-snapshot-migr-04) instead of from
history.) Substitute the project + branch IDs
from the table above and the step-0 timestamp:

```bash
curl -sf -X POST -H "Authorization: Bearer $NEON_API_KEY" -H "Content-Type: application/json" \
  "https://console.neon.tech/api/v2/projects/<project_id>/branches/<branch_id>/restore" \
  -d '{
    "source_branch_id": "<branch_id>",
    "source_timestamp": "<step-0 restore point, RFC 3339, e.g. 2026-07-05T14:03:00Z>",
    "preserve_under_name": "pre-restore-<yyyymmdd-hhmm>"
  }'
```

- `source_branch_id` is the **same branch** (a restore into its own history); in that mode
  `preserve_under_name` is **mandatory** and is also the undo: Neon parks the current
  (post-migration) head as a branch under that name, so the restore itself is reversible and any
  post-migration writes stay salvageable from it.
- Console-UI alternative: project → *Branches* → `production` → **Restore** → *From this branch's
  history* → pick the timestamp (the preserve branch is created automatically).
- The restore is near-instant (copy-on-write). Active connections drop once; the apps' Npgsql
  pools reconnect on the next request.

**3. Verify.** Run the [Verifying](#verifying) queries — the bad migration's row must be **gone**
from that context's `__EFMigrationsHistory` (the restore rewinds schema, data and history together,
so they can never drift apart) — then smoke the environment:

```bash
SMOKE_CLIENT_SECRET="<that environment's OpenIddict API client secret>" scripts/smoke.sh \
  --auth-url <auth-url> --tms-url <tms-url> --frontend-url <frontend-url>
```

**4. Roll forward.** Fix the migration in a new commit and let the normal pipeline redeploy — the
migrator gate re-applies from the rewound history. Once confident, delete the `pre-restore-*`
safety branch — its id is in the restore call's response, or in `GET /projects/<project_id>/branches`
(Free-plan projects cap the branch count — 10 at the time of writing):

```bash
curl -sf -X DELETE -H "Authorization: Bearer $NEON_API_KEY" \
  "https://console.neon.tech/api/v2/projects/<project_id>/branches/<preserved_branch_id>"
```

#### Restore from the auto-snapshot (MIGR-04)

Since MIGR-04 (#339), every deploy with the Neon leg configured creates a **pre-migration snapshot
branch** in that environment's Neon project — right after pinning the migrator image, right before
starting the migrator job:

- **Shape:** `migr04-pre-<short-sha>-<utc-ts>` (e.g. `migr04-pre-eb1363e-20260705T160102Z`),
  branched from the project's **default branch head** (the create-branch API's documented default
  when `parent_id` is omitted), with **no compute endpoint** — it costs no compute, only pinned
  history/storage.
- **Where it is recorded:** the run summary's "DB restore point (pre-migration)" table — name + id.
- **Why it exists next to PITR:** a branch head never expires. PITR history lasts 6 h on the Free
  plan; the snapshot stays restorable however late the bad migration is noticed.
- **Configuration (per GitHub environment; optional):** env-scoped secret `NEON_API_KEY`
  (project-scoped key) + variable `NEON_PROJECT_ID` — see
  [operator setup step 6](#one-time-operator-setup-your-azure--github). When either is missing the
  deploy logs `Neon snapshot skipped (not configured)` and proceeds; a Neon API error logs a
  warning and proceeds too. **The snapshot is a net, not a gate** (ADR-0023 does not make it
  mandatory; the ambient MIGR-01 PITR net still applies) — the deploy never fails because of it.

**Retention — decided with MIGR-04: at most ONE snapshot branch per project.** Right before
creating the new snapshot, the deploy deletes every older `migr04-pre-*` branch in that project.
Rationale: both Free-plan projects sit at ~75 % of the 0.5 GB storage cap (plus a 10-branch cap),
every branch pins history, and by the time deploy N+1 runs, deploy N's migration has already
proven itself in service — its snapshot is dead weight. Consequence, stated plainly: **the
snapshot protects the latest deploy only**; an older bad migration falls back to
[PITR](#recover-from-a-bad-migration-neon-pitr) (≤ 6 h) or the accepted risk boundary above.

**Restore procedure** — identical to the [PITR procedure](#procedure) except step 2: restore the
default branch **from the snapshot branch's head** instead of from its own history. Semantics
verified against the Neon Branch Restore API: `source_timestamp`/`source_lsn` omitted ⇒ the source
is *"restored to head"*, which for the snapshot **is** the pre-migration state; and
`preserve_under_name` is **required** here because the restored branch has children (the snapshot
itself is one):

```bash
curl -sf -X POST -H "Authorization: Bearer $NEON_API_KEY" -H "Content-Type: application/json" \
  "https://console.neon.tech/api/v2/projects/<project_id>/branches/<default_branch_id>/restore" \
  -d '{
    "source_branch_id": "<snapshot branch id from the run summary, br-…>",
    "preserve_under_name": "pre-restore-<yyyymmdd-hhmm>"
  }'
```

- The current (post-migration) head is parked as the `pre-restore-*` branch — the undo — and the
  branch's existing children (including the snapshot) are re-parented under it.
- Steps 0–1 (find the restore point, park traffic on the last-good app revision) and 3–4 (verify,
  roll forward) are unchanged from the [PITR procedure](#procedure).
- Cleanup order matters: delete the `migr04-pre-*` branch **before** the `pre-restore-*` branch —
  Neon refuses to delete a branch that still has children. If a later deploy's retention sweep
  warns it could not delete an old snapshot, finish this cleanup by hand.

#### Rehearsal (staging drill)

Steps 2–4 can be rehearsed on staging at any time, without a deploy: restore the staging branch to
five minutes ago (a data no-op while staging is idle), check the [Verifying](#verifying) output is
unchanged, then delete the preserved branch. Do **not** run it while manual QA is in progress —
the restore drops connections and rewinds anything written after the restore point.

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
  --auth-url     https://auth.lotro-translator.pl \
  --tms-url      https://tms.lotro-translator.pl \
  --frontend-url https://lotro-translator.pl
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

`cd.yml`'s `deploy-prod` runs `scripts/smoke.sh` **inline twice per release** (ADR-0012, amended audit
0001 H7): once against the **0%-traffic candidate** (its private `…---cd-candidate.<env-domain>` FQDN)
**before** any traffic shift — a red smoke here skips promotion and the candidate is rolled back, so a
broken build never serves a user — and once against the **real production origins** after promotion. It
runs inline (not as a separate job) because the Azure OIDC federated credential trusts only the
`production` environment, so every step sharing that identity must live in the one approved job. The
same [`Smoke test`](../../.github/workflows/smoke.yml) reusable workflow stays runnable **on demand**
(`workflow_dispatch` — enter the three URLs); the secret comes from the repository secret
`SMOKE_CLIENT_SECRET`. See [Continuous deployment (CI/CD)](#continuous-deployment-cicd).

## Observability

In the cloud, telemetry flows through the **Container Apps managed OpenTelemetry agent** (enabled at
the environment level in `iac/observability.tf`; ADR-0016) into **workspace-based Application
Insights** (`lotrotmsappinsights<env_id>`, backed by the existing Log Analytics workspace). The agent
injects `OTEL_EXPORTER_OTLP_ENDPOINT` into every container automatically, so the apps' existing OTLP
pipeline ships **distributed traces + logs** without any app or env-var change. Dev is unaffected — it
keeps using the aspire-dashboard from `compose.yaml`.

**Where to look** (Azure Portal → the `lotrotmsappinsights<env_id>` resource):

- **Application Map** — live service topology (frontend → tms-api → auth-api → Postgres) with
  per-edge latency and failure rates.
- **Transaction search** / **End-to-end transaction details** — individual request traces across the
  three apps (drill into a single OIDC login or an import call).
- **Logs (KQL)** — the `requests`, `dependencies`, `traces` and `exceptions` tables, queryable in
  Application Insights or directly in the linked Log Analytics workspace.

**Metrics caveat.** The managed agent forwards **traces + logs only** to Application Insights — it
does **not** deliver OTel *metrics* there (the agent's metrics path targets only OTLP/Datadog sinks).
So there are no OpenTelemetry metric series (runtime, EF, Npgsql counters) in App Insights. For
metric-shaped signal rely on **ACA platform metrics** (CPU/memory/replica/request counts — the sibling
audit 0001 C3 alerting ticket) plus the request/dependency metrics App Insights **reconstructs from
the traces**. Adding a metrics destination is deferred until there is a real need (YAGNI).

## Monitoring & alerting

Defined as code in [`iac/monitoring.tf`](../../iac/monitoring.tf) (audit 0001 §C3; the availability
SLO reworked in [ADR-0019](../adr/0019-symptom-based-external-slo-probe.md)). Every alert
**fires by email** to `var.admin_email` through a single Azure Monitor action group
(`lotrotmsag<env_id>`) — **email only, no SMS**. Most alerts stand on signals that already exist (ACA
platform metrics + the Log Analytics workspace the apps stream console logs to); the availability SLO
and the auth latency alert additionally read **Application Insights** (synthetic web tests + the
OTel-reconstructed request metrics). Everything is parametrized by `var.env_id`, so a future staging
inherits the same alerting.

| Alert | Source | Fires when | Severity |
|---|---|---|---|
| **Availability SLO — auth** | AI standard web test (public origin) | `https://auth.<domain>/health/ready` is unreachable from **≥2 of 3 regions** — auth is the token-issuance SPOF, so this is the platform's SLO | 0 Critical |
| **Availability — tms / frontend** | AI standard web tests | `tms/health/ready` or the frontend home is unreachable from **≥2 of 3 regions** | 1 Error |
| **Replica restart** | metric `RestartCount` | any of the three apps restarts a replica (crash-loop signal: OOM, failed readiness, unhandled crash). Sensitive by design (`> 0`); stays active for the affected revision and auto-mitigates on a clean revision | 1 Error |
| **HTTP 5xx spike** | metric `Requests` (`statusCodeCategory = 5xx`) | an app returns more than 5 server errors in 5 minutes. 4xx is intentionally not alerted (auth 401/400 are normal control flow) | 1 Error |
| **Key Vault availability** | metric `Microsoft.KeyVault/vaults` `Availability` | the vault's availability drops below 99% over 15 minutes — KV is the secret-resolution SPOF (every revision resolves its secrets at start) | 1 Error |
| **Log error spike** | LAW `ContainerAppConsoleLogs_CL` | an app logs more than 10 Serilog `Error`/`Fatal` entries in 5 minutes — catches logged failures that never surface as a 5xx or restart | 2 Warning |
| **Memory saturation** | metric `WorkingSetBytes` | an app sustains >80% of its 0.5 GiB limit for 15 minutes (OOM precursor) | 2 Warning |
| **Auth latency** | AI `requests/duration` (auth role) | auth server response time averages **>2 s over 15 minutes** — a leading indicator before readiness fails | 2 Warning |
| **LAW daily cap reached** | LAW `Operation` table | the workspace's daily ingestion cap (`daily_quota_gb` in `azure-law.tf`) is hit and **log collection has stopped** — a blind spot exactly during an error storm | 2 Warning |
| **CPU saturation** | metric `UsageNanoCores` | an app sustains >80% of its 0.25 vCPU limit for 15 minutes (capacity signal; no horizontal headroom at min=max=1 replica) | 3 Informational |

Notes for the operator:

- **The availability SLO is deploy-safe by construction.** The standard web tests probe each app's
  **public origin** — which only ever resolves to the revision serving 100% of traffic. A health-gated
  rollout's candidate revision takes 0% traffic and is reachable only via its private
  `<app>---cd-candidate` label FQDN, so **a deploy cannot trip these**. This is the fix for the old
  log-based `auth_availability`, which paged Sev0 on every release (it matched the app name, not the
  serving revision). Durability is geographic — **≥2 of 3 locations** (West Europe, North Europe,
  France Central) must fail — so a single-region blip is rejected without delaying a real outage.
- The metric alerts are fanned out **per app** (a `for_each` over auth-api / tms-api / frontend),
  one single-scope rule each — the universally-supported shape (Azure does not allow multi-resource
  metric alerts for Container Apps). The alert name carries the app key, e.g.
  `lotrotms-alert-replica-restart-auth-api-<env_id>`.
- The two log (scheduled-query) alerts set `skip_query_validation = true`, because the
  `*_CL` tables only exist once the apps have emitted those records — a fresh environment can
  provision the alerts before any logs flow.
- **Confirm-at-apply, not at plan.** The web-test geo codes, the auth alert's `cloud/roleName`
  (= the app's OTel `service.name` = its entry-assembly name `LotroKoniecDev.AuthSystem.API`), and
  the App Insights / KV metric names are only validated server-side. If an availability or latency
  alert shows **no data**, verify those against live App Insights after the first apply.

**Responding to an alert:**

| Alert | First moves |
|---|---|
| Availability SLO (auth/tms/frontend) | Hit the public origin yourself: `curl -sS -o /dev/null -w '%{http_code}' https://auth.<domain>/health/ready`. If it's down, check ACA revision health + the traffic split (`az containerapp ingress traffic show`) — a bad revision live means the rollout's auto-rollback (`deploy.yml`) did not fire; steer traffic back with `az containerapp ingress traffic set --revision-weight <prev>=100`. If the origin is up, suspect DNS/cert/regional network. |
| Key Vault availability | Portal → the vault → check for throttling / an Azure KV incident; confirm the `lotrotms-aca-<env_id>` identity still has *Key Vault Secrets User*; new revisions cannot boot without secret resolution. |
| Auth latency | App Insights → Performance for the auth role; check the DB (Neon) latency and the sibling CPU/memory saturation alerts. Leading indicator — investigate before it becomes a 5xx / readiness failure. |
| Replica restart / 5xx / log error spike | App Insights logs + `az containerapp logs show` for the named app; correlate with a recent deploy. |
| CPU / memory saturation | Capacity signal at min=max=1 replica: inspect the workload, then raise the request or replica ceiling in `azure-container-apps.tf`. |

**Silencing during planned disruptive work.** Routine deploys need **no** suppression (the availability
SLO is deploy-safe by construction). For genuinely disruptive planned work (e.g. a migration with
downtime), add a temporary **alert processing rule** rather than editing Terraform — scope it to the
resource group for the maintenance window, then delete it:

```bash
az monitor alert-processing-rule create \
  --name lotrotms-maint-suppress --resource-group rg-lotrotms-prod-polc-001 \
  --scopes "$(az group show -n rg-lotrotms-prod-polc-001 --query id -o tsv)" \
  --rule-type RemoveAllActionGroups --enabled true \
  --description "Planned maintenance — suppress alert emails"
# … do the work, then:
az monitor alert-processing-rule delete --name lotrotms-maint-suppress -g rg-lotrotms-prod-polc-001
```

## See also

- [`.env.example`](../../.env.example) — the dev-compose env template (`scripts/up.sh` bootstraps `.env` from it).
- [`.env.prod.example`](../../.env.prod.example) — the production-parity env template: secrets + the
  managed-DB swap point (`scripts/up-prod.sh` bootstraps `.env.prod` from it, generating the OpenIddict secrets).
- [`compose.yaml`](../../compose.yaml) / [`compose.prod.yaml`](../../compose.prod.yaml) — the dev and
  production-parity stacks; the literal env→container wiring this matrix abstracts.
- [ADR-0008](../adr/0008-cloud-agnostic-deployment-and-environment-strategy.md) — the cloud-agnostic
  deployment & environment strategy this runbook operationalizes.
- [ADR-0016](../adr/0016-cloud-telemetry-via-aca-managed-otel-agent.md) — cloud telemetry: the ACA
  managed OpenTelemetry agent → Application Insights (the [Observability](#observability) section).
- [`target-requirements.md`](target-requirements.md) — platform requirements + Azure⇄AWS service
  mapping (M6-12).
