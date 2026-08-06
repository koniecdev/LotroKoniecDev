# Deployment runbook

> Operator manual for running LotroKoniecDev in a real environment. The platform is a **Hetzner VPS
> running `docker compose` behind Caddy** ([ADR-0034](../adr/0034-hetzner-vps-instead-of-azure-container-apps.md));
> the database is **Neon** (ADR-0014), images live in **GHCR**, and CD deploys **over ssh**.
>
> **Scope:** the full configuration surface and operation of the four service images across
> environments — the topology, the environment-variable matrix, secrets and their rotation, the
> consistency rules that bite, bring-up, continuous deployment, database migrations, the smoke test,
> and disaster recovery.
>
> The app contract itself is **provider-neutral** (ADR-0008): every app is a 12-factor OCI image
> behind a TLS ingress, so nothing below is Hetzner-specific except the box sections. The stack ran
> on Terraform-provisioned **Azure Container Apps** until 2026-07-12 — see
> [History — the Azure era](#history--the-azure-era).

## Contents

- [Topology](#topology) — the fleet, the boxes, the filesystem layout
- [Services & the container contract](#services--the-container-contract)
- [Environment variable matrix](#environment-variable-matrix) — the single source of truth, per service × environment
- [Secrets](#secrets) — where they live, how to generate them, how to rotate them
- [Consistency rules that bite](#consistency-rules-that-bite) — issuer / redirect / authority / CORS
- [Bringing the stack up](#bringing-the-stack-up) — dev, prod-parity, (re)provisioning a box, and the one-time network cutover
- [Continuous deployment (CD over ssh)](#continuous-deployment-cd-over-ssh) — build-once → auto staging → gated prod promotion
- [Database migrations](#database-migrations) — strategy, running them, and recovering from a bad migration (Neon PITR + the MIGR-04 auto-snapshot)
- [Message broker (RabbitMQ)](#message-broker-rabbitmq) — the in-stack broker, the dead-letter parking lot, replay & rotation traps
- [Post-deploy smoke test](#post-deploy-smoke-test) — one command verifies a deployed environment end-to-end
- [Observability & monitoring](#observability--monitoring) — what exists today, and the gap the migration left
- [Disaster recovery](#disaster-recovery)
- [Gotchas](#gotchas)
- [History — the Azure era](#history--the-azure-era)
- [See also](#see-also)

## Topology

Two Hetzner boxes, each hosting **both projects** (LotroKoniecDev + TheKittySaver) for **one**
environment. One box = one environment; the box's single `/opt/lotro/.env` is what makes it prod or
staging.

| Fact | Value |
|---|---|
| Fleet | `lotro-prod` **167.233.159.221** (both prods) · `lotro-staging` **91.98.74.228** (both stagings) — ssh aliases of the same names in the owner's `~/.ssh/config` |
| Provider / model | Hetzner Cloud **CX23** × 2 (2 vCPU, 4 GB RAM, amd64) — tight for 4 app containers + Caddy per box; watch memory before adding services. ADR-0034 §1 argued for one CX32; the fleet is two CX23 (location constraints at purchase, owner decision 2026-07-12) |
| Location | Nuremberg (nbg1), Germany |
| OS | Ubuntu 26.04 LTS (resolute); `bootstrap.sh` is verified on 24.04 and 26.04 |
| Backups | Hetzner backups on **lotro-prod only** (~20% surcharge); staging is disposable |
| Users | `root` (key-only) · `deploy` (docker group, key-only, locked password — CD and day-2 ops run as this user) |
| Firewall | ufw: deny incoming except **22/80/443**, allow outgoing |
| Intrusion / patching | fail2ban (sshd jail, systemd backend) · unattended-upgrades |
| Container runtime | Docker Engine + compose plugin (the box's own — bootstrap **adopts**, never swaps: see [Gotchas](#gotchas)) |
| Registry | `deploy` is `docker login`-ed to **ghcr.io** with a **read-only** (`read:packages`) PAT |
| DB | none on the box — **Neon** is the database for prod AND staging (ADR-0034 §2) |
| Ingress | **Caddy**, one vhost per app, automatic Let's Encrypt certificates |
| Networks | **Two segregated Docker networks per box** (#506): `${project}_default` (`10.60.0.0/24`, our stack) and `${project}_tks` (`10.61.0.0/24`, the guest TheKittySaver stack). **Caddy is the only container on both** (static IPs `10.60.0.100` / `10.61.0.100`); nothing else crosses. The apps trust `X-Forwarded-*` from Caddy's `10.60.0.100/32` only — keep that /32 in lockstep with Caddy's `ipv4_address`. |

Images are `linux/amd64` only (no multi-arch buildx) — the box is amd64 on purpose; never "fix" a
pull error by enabling emulation.

### Filesystem layout

| Path | Contents | Owner |
|---|---|---|
| `/opt/lotro` | LotroKoniecDev stack: `compose.hetzner.yaml`, `.docker/hetzner/Caddyfile`, `.env` (`chmod 600`, never committed — template: `.env.hetzner.example`), `deploy.sh`, `.previous/` (the last-good config snapshot) | `deploy` |
| `/opt/tks` | TheKittySaver stack (its own epic; joins the same Caddy via parametrized vhosts) | `deploy` |

## Services & the container contract

Four OCI images (built by the four multi-stage Dockerfiles, published to GHCR by CD) behind one
TLS-terminating ingress:

| Service | Image (`ghcr.io/koniecdev/…`) | Listens | Health | Persists |
|---|---|---|---|---|
| **auth-api** | `lotrokoniecdev-auth-api` | `:8080` (HTTP) | `/health` (deep: DB + SMTP + broker), `/health/live`, `/health/ready` (probe — runs no checks, ADR-0025) | Data Protection keyring → `/keys` |
| **tms-api** | `lotrokoniecdev-tms-api` | `:8080` (HTTP) | `/health` (deep: DB), `/health/live`, `/health/ready` (probe — runs no checks, ADR-0025) | translation artifacts (read-only mount) |
| **frontend** | `lotrokoniecdev-frontend` | `:8080` (HTTP) | — | Data Protection keyring → `/keys` |
| **migrator** | `lotrokoniecdev-migrator` | one-shot (exits 0) | exit code | — |
| _ingress_ | **Caddy** (`caddy:2-alpine`) | `:80`, `:443` | — | ACME certs + config volumes |
| _broker_ | **RabbitMQ** (`rabbitmq:4.3.4-management-alpine` — pinned, see the compose comment) | `:5672` in-stack (AMQP; auth-api only) | `rabbitmq-diagnostics ping` (container healthcheck) + the `rabbitmq` leg of auth's deep `/health` | broker state (users, quorum queues, parked dead letters) → `rabbitmq-data` volume |

Container contract (ADR-0008 §2): each app serves **plain HTTP on `:8080`** and expects a
TLS-terminating ingress in front; runs **non-root**; logs **structured JSON to stdout**; takes **all
runtime configuration from environment variables**. Only Caddy publishes internet-facing ports; the
apps use `expose:` and are reachable **only** through the proxy (the broker's management UI is the
one loopback exception — `127.0.0.1:15672`, reachable exclusively over an ssh tunnel, see
[Message broker](#message-broker-rabbitmq)). The migrator runs to completion *before* the
APIs serve traffic, so there is never half-migrated serving.

## Environment variable matrix

The single source of truth: every deployment-relevant setting, per service, per environment. One
table per service.

**`compose.hetzner.yaml` is the authoritative list** of what the deployed stack actually consumes — a
variable that does not appear there does nothing, whatever this table says.

**Reading the tables:**

- **Key form.** Keys are shown in the env-var (double-underscore) form ASP.NET Core binds —
  `Section__Sub__Leaf`, e.g. `OpenIddict__WebClient__RedirectUris__0`. The config/appsettings form is
  the same with `:` (`OpenIddict:WebClient:RedirectUris:0`); error messages use the `:` form.
- **Required?** = whether boot **fails fast** without it in that environment. `✅ all` = required in
  every environment; `✅ non-dev` = required in Staging/Production only (Development supplies a
  default or skips the guard); `optional` = safe default.
- **Source.** **secret** = lives in the box's `chmod 600` `/opt/lotro/.env`, never committed;
  **plain** = non-sensitive, fine in plain app config / `appsettings.*`.
- **local-dev** column = how the value is set in dev. Since ADR-0006 (amended by #190 / M6-14) the
  dev `compose.yaml` is **infra-only** and all three apps run on the **host** via `dotnet run` — so
  for the apps the dev column is `appsettings.Development.json` + `launchSettings.json`; only
  postgres / migrator / mailpit / aspire are set by `compose.yaml`.
- **Staging / Production** are structurally identical — same required set, same sources; they differ
  only in **hostnames** (the environment's own domain) and **secret values**. This column shows
  **production** values; for staging substitute `staging.lotro-translator.pl`. The local
  production-parity stack (`compose.prod.yaml`) wires the very same keys with `*.lotro.test`
  hostnames.

Purely optional tuning knobs with safe defaults are omitted (e.g. `OpenIddict:AccessTokenLifetimeMinutes`
= 60, `OpenIddict:RefreshTokenLifetimeDays` = 14, `Import:*`, `TranslationFileRebuild:DebounceWindow`
= 2 s (ADR-0021), `Email:TimeoutSeconds`/`MaxSendAttempts`, `RabbitMq:Port` = 5672,
`RabbitMq:VirtualHost` = `/`, `AllowedHosts` = `*`).

> ⚠️ **Live prod domain is `lotro-translator.pl`** — auth → `https://auth.lotro-translator.pl`,
> tms → `https://tms.lotro-translator.pl`, frontend → `https://lotro-translator.pl`. The three
> hostnames are Caddy vhosts on the prod box; staging has the same trio under
> `*.staging.lotro-translator.pl`.

### auth-api

| Variable | local-dev | Staging / Production | Required? | Source | Notes |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` | ✅ all | plain | Gates fail-fast validation, ephemeral-vs-real keys, the CORS policy. |
| `ASPNETCORE_URLS` | `https://localhost:5003` (launchSettings) | `http://+:8080` | ✅ all | plain | Host dev Kestrel (native dev cert); prod serves HTTP only (Caddy owns TLS). |
| `ConnectionStrings__AuthDatabase` | `…;Database=lotro_auth;…;Password=changeme` (appsettings.Development) | Neon: `Host=…;Database=lotro_auth;Username=…;Password=…;Ssl Mode=Require;Timeout=60` | ✅ all | **secret** | Carries the DB password. **Keyword format only** — Npgsql does not parse `postgres://` URIs. `Timeout=60` rides out Neon's ~31 s scale-to-zero resume. |
| `OpenIddict__Issuer` | `https://localhost:5003` | `https://auth.lotro-translator.pl` | ✅ non-dev | plain | **THE token `iss`.** Absolute http(s), no `localhost`. Must equal tms `Auth__Issuer`. |
| `OpenIddict__SigningKey__RsaPrivateKeyXml` | — (ephemeral) | base64 of RSA XML (≥2048-bit) | ✅ non-dev | **secret** | Generate with `gen-openiddict-keys`. |
| `OpenIddict__EncryptionKey__Key` | — (ephemeral) | base64 of a ≥32-byte key | ✅ non-dev | **secret** | Generate with `gen-openiddict-keys`. |
| `OpenIddict__ApiClientSecret` | `dev-api-secret-min-32-characters-long` | ≥32-char random secret | ✅ non-dev | **secret** | Generate with `gen-openiddict-keys`. **Seeds a DB row** — rotating it needs the [reseed](#reseed-traps--the-auth-seeder-is-create-if-missing). Must equal that environment's `SMOKE_CLIENT_SECRET`. |
| `OpenIddict__WebClient__RedirectUris__0` | `https://localhost:7017/callback` | `https://lotro-translator.pl/callback` | ✅ non-dev | plain | MUST equal the Frontend callback (its public origin + `AuthSystem__CallbackPath`). Written to the DB **only at client creation** — see the reseed traps. |
| `OpenIddict__WebClient__PostLogoutRedirectUris__0` | `https://localhost:7017` | `https://lotro-translator.pl` | ✅ non-dev | plain | Frontend post-logout return URL. |
| `Cors__AllowedOrigins__0` | — (AllowAnyOrigin) | `https://lotro-translator.pl` | ✅ non-dev | plain | Bare origin = Frontend public URL. Lowercase, no port-if-default, no path/slash. |
| `ForwardedHeaders__KnownNetworks__0` | — (dev skips `UseForwardedHeaders`) | `10.60.0.100/32` (Caddy's pinned static IP) | optional | plain | Restricts `X-Forwarded-*` trust to Caddy's exact host address, not the whole subnet (#399, narrowed to a /32 by #506). Keep in lockstep with Caddy's `ipv4_address` on the default network in `compose.hetzner.yaml`. Both deployed stacks set it. Malformed CIDR aborts boot. |
| `DataProtection__KeyRingPath` | — (host default) | `/keys` | ✅ non-dev | plain | Persistent volume (`auth-keys`); else logins/antiforgery/reset links break on every deploy. |
| `Email__Host` | `localhost` (the compose mailpit, published on `:1025`) | `smtp-relay.brevo.com` | ✅ all | plain | SMTP host. Validated on start (every environment). |
| `Email__Port` | `1025` | `587` | ✅ all | plain | 1–65535. |
| `Email__Mode` | `None` | `StartTls` | ✅ all | plain | One of `None` / `StartTls` / `TLS`. |
| `Email__SenderEmail` | `noreply@lotro-translator.pl` | a Brevo-**authorised** sender (today `koniecdev@gmail.com`) | ✅ all | plain | ⚠️ Not a free-text label — an unauthorised sender is accepted by the relay and **silently dropped** by the receiver. See [E-mail deliverability](#e-mail-deliverability--the-sender-must-be-one-brevo-is-authorised-to-send-as). |
| `Email__Sender` | `lotro-translator.pl` | `LOTRO PL` | ✅ all | plain | Display name. |
| `Email__Username` / `Email__Password` | — | Brevo SMTP login + key | optional¹ | **secret** (Password) | ¹If `Username` is set, `Password` is required. The login is shaped `<id>@smtp-brevo.com` — **not** the Brevo account e-mail (that fails with `535`). |
| `RabbitMq__Host` | `localhost` (the compose broker, published on `:5672`) | `rabbitmq` (the in-stack broker service) | ✅ all | plain | Validated on start (every environment) — but auth-api **boots and serves with the broker down**: outbox rows wait, the consumer retries. See [Message broker](#message-broker-rabbitmq). |
| `RabbitMq__Username` | `rabbitmq` | `rabbitmq` | ✅ all | plain | Matches the `RABBITMQ_DEFAULT_USER` literal in every compose file. |
| `RabbitMq__Password` | `changeme` (appsettings.Development + the dev compose `RABBITMQ_PASSWORD`) | from `RABBITMQ_PASSWORD` | ✅ all | **secret** | ⚠️ The broker applies `RABBITMQ_DEFAULT_PASS` on **first boot only** — rotation is a lockstep dance, see the [secrets table](#secret-material--source-of-truth-and-how-to-rotate). |
| `AdminUser__Username` / `AdminUser__Email` / `AdminUser__Password` | from `AUTH_ADMIN_*` | from `AUTH_ADMIN_*` | optional | **secret** (Password) | Seeds one admin **only when missing**; leave blank to skip. Username must match `^[a-zA-Z0-9]+$` (ADR-0022) or auth-api fails at startup; the admin logs in **by e-mail**. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://localhost:4317` (launchSettings → the compose aspire-dashboard) | — (empty: no sink today, ADR-0034) | optional | plain | Empty = telemetry export disabled. See [Observability](#observability--monitoring). |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` | `grpc` / `http/protobuf` | optional | plain | Defaults to `grpc`. |

### tms-api

| Variable | local-dev | Staging / Production | Required? | Source | Notes |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` | ✅ all | plain | Gates fail-fast validation + the CORS policy. |
| `ASPNETCORE_URLS` | `https://localhost:5002` (launchSettings) | `http://+:8080` | ✅ all | plain | Host dev Kestrel (native dev cert); prod serves HTTP only (Caddy owns TLS). |
| `ConnectionStrings__TranslationDatabase` | `…;Database=lotro_translation;…;Password=changeme` (appsettings.Development) | Neon: `Host=…;Database=lotro_translation;Username=…;Password=…;Ssl Mode=Require;Timeout=60` | ✅ all | **secret** | TMS write context. Same Neon format rules as the auth string. |
| `Auth__Issuer` | `https://localhost:5003` | `https://auth.lotro-translator.pl` | ✅ all | plain | MUST equal auth `OpenIddict__Issuer` (the token `iss`); tokens are rejected otherwise. |
| `Auth__Authority` | — (unset → falls back to `Issuer`) | `https://auth.lotro-translator.pl` | optional² | plain | Back-channel for OIDC metadata + JWKS. ²Unset → falls back to `Issuer`; the dev host run relies on that fallback. Prod: must be `https` (OpenIddict rejects plain HTTP) and reachable from the container — i.e. the **public Caddy origin**, never `http://auth-api:8080`. |
| `Auth__Audience` | `lotrokoniecdev-api` | `lotrokoniecdev-api` | ✅ all | plain | Default in base `appsettings.json`. |
| `Cors__AllowedOrigins__0` | — (AllowAnyOrigin) | `https://lotro-translator.pl` | ✅ non-dev | plain | Bare origin = Frontend public URL. |
| `ForwardedHeaders__KnownNetworks__0` | — (dev skips `UseForwardedHeaders`) | `10.60.0.100/32` (Caddy's pinned static IP) | optional | plain | See the auth-api row. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` / `OTEL_EXPORTER_OTLP_PROTOCOL` | `http://localhost:4317` / `grpc` (launchSettings) | — (empty: no sink today) | optional | plain | Empty endpoint = export disabled. |
| `Bootstrap__Enabled` | `false` | `false` | optional | plain | One-time DB seed of the first export (spec 0001). Off by default. |
| `Bootstrap__GameVersion` / `Bootstrap__ExportedTextPath` / `Bootstrap__PolishTextPath` | — / — / `/app/translations/polish.txt` | as needed | optional | plain | Only consulted when `Bootstrap__Enabled=true`. |

### frontend

Runs on the **host** in dev (`dotnet run`, ADR-0006 — and since #190 / M6-14 so do auth-api + tms-api) —
its dev column is `appsettings.Development.json` + `launchSettings.json`; it is containerized only in
Staging/Production.

| Variable | local-dev | Staging / Production | Required? | Source | Notes |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` | ✅ all | plain | Gates the DP keyring guard. |
| `ASPNETCORE_URLS` | `https://localhost:7017` (launchSettings) | `http://+:8080` | ✅ all | plain | Prod serves HTTP only (Caddy owns TLS). |
| `AuthSystem__Authority` | `https://localhost:5003` | `https://auth.lotro-translator.pl` | ✅ all | plain | Drives the `/authorize` redirect (front-channel) **and** discovery/token (back-channel). |
| `AuthSystem__BaseUrl` | `https://localhost:5003/` | `https://auth.lotro-translator.pl/` | ✅ all | plain | Auth origin (trailing slash). |
| `AuthSystem__ClientId` | `lotrokoniecdev-web` | `lotrokoniecdev-web` | ✅ all | plain | Must match the OpenIddict web-client id. |
| `AuthSystem__CallbackPath` | `/callback` | `/callback` | ✅ all | plain | Origin + this MUST be registered in auth `RedirectUris`. |
| `AuthSystem__SignedOutCallbackPath` | `/signout-callback-oidc` | `/signout-callback-oidc` | ✅ all | plain | Origin + this MUST be in auth `PostLogoutRedirectUris`. |
| `AuthSystem__Scopes` | `openid,email,profile,roles,api,offline_access` | same | ✅ all | plain | At least one scope. |
| `TranslationSystem__BaseUrl` | `https://localhost:5002/` | `https://tms.lotro-translator.pl/` | ✅ all | plain | TMS API origin (trailing slash). |
| `DataProtection__KeyRingPath` | — (host default) | `/keys` | ✅ non-dev | plain | Persistent volume (`frontend-keys`, ADR-0005); else antiforgery + auth cookies break on deploy. |
| `ForwardedHeaders__KnownNetworks__0` | — (dev skips `UseForwardedHeaders`) | `10.60.0.100/32` (Caddy's pinned static IP) | optional | plain | See the auth-api row. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` / `OTEL_EXPORTER_OTLP_PROTOCOL` | `http://localhost:4317` / `grpc` (launchSettings) | — (empty: no sink today) | optional | plain | Empty endpoint = export disabled. |

### migrator

A one-shot job — reads exactly the two connection strings (see [Database migrations](#database-migrations)
for the full strategy):

| Variable | local-dev | Staging / Production | Required? | Source | Notes |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` | ✅ all | plain | |
| `ConnectionStrings__TranslationDatabase` | from `POSTGRES_PASSWORD` | the Neon TMS string | ✅ all | **secret** | TMS write context (`lotro_translation`). |
| `ConnectionStrings__AuthDatabase` | from `POSTGRES_PASSWORD` | the Neon Auth string | ✅ all | **secret** | Auth context (`lotro_auth`). |

### Box-identity variables (not secrets)

These decide **which environment a box is** — full list with placeholders in `.env.hetzner.example`:
`COMPOSE_PROJECT_NAME` (`lotro-prod` | `lotro-staging`), `IMAGE_NAMESPACE`, `IMAGE_TAG`,
`DOMAIN_APP` / `DOMAIN_AUTH` / `DOMAIN_TMS`, `ACME_EMAIL`, `TKS_DOMAIN_*` (the guest TheKittySaver
vhosts), `XROBOTS` (SEO crawler control, #531 — Caddy stamps it as `X-Robots-Tag` on every LOTRO
vhost response; prod leaves it unset → `all` (explicit no-op), the **staging box sets
`XROBOTS="noindex, nofollow"`** so the deliberately-public staging trio never gets indexed as
duplicate content of prod. The TKS vhosts are excluded on purpose — TKS stamps the header
app-side, koniecdev/TheKittySaver#390).

## Secrets

### Where they live

The live values sit in **`/opt/lotro/.env`** on each box (`chmod 600`, owner `deploy`): **one file
per box**, because one box = one environment. Compose picks it up automatically — no `--env-file`.
The tracked template carrying every key with placeholder values is **`.env.hetzner.example`**.

Two pieces of credential-ish material live **outside** `.env` and outside the DB: the `auth-keys`
and `frontend-keys` **Data Protection keyring volumes**. They are not minted from anywhere — losing
a volume simply invalidates every auth cookie and OIDC correlation state (everyone re-logs in),
which is why they are named volumes and not bind mounts.

### Secret material — source of truth and how to rotate

| Variable | Consumed by | Source of truth | (Re)mint / rotate |
|---|---|---|---|
| `ConnectionStrings__AuthDatabase` | migrator, auth-api | **Neon** — the env's project (prod: `lotro-translator-prod`; staging has its own, ADR-0018), DB `lotro_auth` | Neon console → Roles → *Reset password* → rebuild the string by hand. **Keyword format only** — Npgsql does not parse `postgres://` URIs. Must carry `Ssl Mode=Require` and `Timeout=60` (rides out Neon's ~31 s scale-to-zero resume). |
| `ConnectionStrings__TranslationDatabase` | migrator, tms-api | same Neon project, DB `lotro_translation` | same |
| `OpenIddict__SigningKey__RsaPrivateKeyXml` | auth-api | **regenerate** — no canonical copy exists anywhere | `scripts/gen-openiddict-keys.sh` prints all three OpenIddict values as `KEY=VALUE` lines; append to the box `.env`. Pure config (never stored in the DB), so rotating just invalidates issued tokens → everyone re-logs in. Free pre-launch. |
| `OpenIddict__EncryptionKey__Key` | auth-api | regenerate | same script, same blast radius. |
| `OpenIddict__ApiClientSecret` | auth-api (**seeds** the `lotrokoniecdev-api` client row) | regenerate | same script — **but the DB row wins on restart; rotation needs the [reseed](#reseed-traps--the-auth-seeder-is-create-if-missing).** Must stay equal to the `SMOKE_CLIENT_SECRET` GitHub secret of the same environment. |
| `Email__Username` | auth-api | **Brevo** dashboard → SMTP & API → SMTP keys | The SMTP **login**, shaped `<id>@smtp-brevo.com` — **not** the Brevo account e-mail, which fails the handshake with `535`. |
| `Email__Password` | auth-api | **Brevo** (owner pastes) | Generate a new SMTP key in Brevo. Shown **once** and never readable back — the copies in the box `.env` and in GitHub secrets are both write-only, so a lost key is re-generated, never recovered. |
| `AUTH_ADMIN_PASSWORD` (+ `AUTH_ADMIN_USERNAME`, `AUTH_ADMIN_EMAIL`) | auth-api → `AdminUser__*` seeder | **owner-chosen** | Seeded **only when missing**, so editing `.env` never rotates a live admin — see the reseed traps. `AUTH_ADMIN_USERNAME` must match `^[a-zA-Z0-9]+$` (ADR-0022) or auth-api fails at startup. |
| `RABBITMQ_PASSWORD` (→ auth-api `RabbitMq__Password` **and** the broker's `RABBITMQ_DEFAULT_PASS`) | rabbitmq, auth-api | **box-local** — generate: `openssl rand -base64 24` | ⚠️ Same create-if-missing shape as the admin seed: the broker applies `RABBITMQ_DEFAULT_PASS` on **first boot only** (empty data volume), so editing `.env` later rotates what auth-api presents but **not** what the broker expects. Rotate in lockstep: `docker compose -f compose.hetzner.yaml exec rabbitmq rabbitmqctl change_password rabbitmq '<new>'` → update `.env` → `docker compose -f compose.hetzner.yaml up -d auth-api`. |
| `SMOKE_CLIENT_SECRET` *(GitHub secret, per environment — **not** a box var)* | `scripts/smoke.sh`, CD | == `OpenIddict__ApiClientSecret` of that env | `gh secret set SMOKE_CLIENT_SECRET --env <staging\|production> --body "$VALUE"` — **never `--body -`**: gh takes `-` literally and the smoke leg then 401s. |
| GHCR pull token *(not an env var — `docker login` state in `/home/deploy/.docker/config.json`)* | `docker compose pull` | **GitHub PAT**, scope `read:packages` **only** | Re-run `scripts/hetzner/bootstrap.sh` (its login leg prompts for user + PAT on a TTY). |
| `HETZNER_SSH_KEY` *(GitHub secret, per environment — **not** a box var)* | CD over ssh (`deploy.yml`) | generated for CD — one key per box | The `deploy` user's key, never `root`'s. Mint + install + pin the host key: [One-time setup per environment](#one-time-setup-per-environment). |

### OpenIddict production keys (auth-api)

The three OpenIddict secrets — generate all at once and append to the box `.env` (or `.env.prod` for
the parity stack):

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

### E-mail deliverability — the sender must be one Brevo is authorised to send as

`Email__SenderEmail` is not a free-text label. It must be either Brevo's verified **single sender**
(today: `koniecdev@gmail.com`) or an address on a domain **authenticated in Brevo** — i.e. one whose
DKIM + SPF records are published in DNS. Any other value fails in the one way no alarm can see:

1. Brevo's relay **accepts** the message, so `IEmailService.SendAsync` returns success.
2. The deep auth `/health` SMTP check stays **green** — it only proves connect + authenticate.
3. The receiver (Gmail, etc.) sees mail claiming a domain with no SPF and no aligned DKIM, and
   **silently drops it** — it does not even reach spam.
4. `RegisterUser` auto-confirms a user only when the send **fails** (`RegisterUser.cs`, the
   `emailResult.IsFailure` branch). A send that "succeeded" skips that net, so every new account is
   stranded at *"potwierdź adres e-mail"* and **nobody can log in**.

This shipped to **both** prods during the Hetzner cutover (#491): `.env.hetzner.example` seeded
`no-reply@lotro-translator.pl`, and `lotro-translator.pl` has **no SPF, DKIM or DMARC records at
all**. Fixed by pointing both boxes at the verified single sender.

**The only check that works is an end-to-end one** — register a real account and read a real inbox;
`smoke.sh` cannot cover this, and neither can a health probe:

```bash
dig +short TXT lotro-translator.pl            # SPF/DKIM/DMARC present at all? (today: nothing)
# then actually register on the env and confirm the mail lands (Gmail plus-addressing works:
# koniecdev+check@gmail.com), because only delivery proves delivery.
```

To move off the personal-gmail sender, authenticate the domain in Brevo (Senders → Domains → add
`lotro-translator.pl`, publish the DKIM + SPF records it prints, add a DMARC record), *then* switch
`Email__SenderEmail` to `no-reply@lotro-translator.pl` — and re-run the registration check above.

### Reseed traps — the auth seeder is create-if-missing

`SeedAuthDatabaseAsync` (`AuthSystem.API/Extensions/DatabaseSeederExtensions.cs`) creates only what
is absent: the admin user is skipped when its e-mail **or** username already exists, and each
OpenIddict client is skipped when its `client_id` already exists. The Neon DBs long outlive any box,
so this is the normal case — and it means **editing `.env` and restarting silently changes nothing**:

| You changed in `.env` | What silently keeps the OLD value | Symptom |
|---|---|---|
| `OpenIddict__ApiClientSecret` | the `lotrokoniecdev-api` client row | `client_credentials` with the new secret → **401**; smoke's token leg fails |
| `AUTH_ADMIN_PASSWORD` | the admin `Users` row | the old password still logs in; the new one never works |
| `DOMAIN_APP` | the `lotrokoniecdev-web` client's redirect + post-logout URIs (written **only at creation**) | login bounces with `invalid_redirect_uri` |

Fix = delete the rows and let the seeder rebuild them from the current `.env`. Schema is
**`authsystem`** and the Identity tables are **renamed** (`authsystem."Users"`, *not* `AspNetUsers`).
Delete in FK order:

```sql
DELETE FROM authsystem."OpenIddictTokens";
DELETE FROM authsystem."OpenIddictAuthorizations";
DELETE FROM authsystem."OpenIddictApplications";
-- only when rotating AUTH_ADMIN_* (UserRoles cascades with the user):
DELETE FROM authsystem."Users" WHERE "Email" = '<admin e-mail>';
```

```bash
docker compose -f compose.hetzner.yaml restart auth-api   # seeder runs at startup, recreates the rows
```

Every logged-in user is signed out by this (their tokens are gone) — free pre-launch, a real outage
after launch.

### Admin seed (optional)

Set all three to seed one usable admin login into the auth DB on first boot; leave blank to skip:

```
AUTH_ADMIN_USERNAME=…        # letters + digits only (^[a-zA-Z0-9]+$) — ADR-0022
AUTH_ADMIN_EMAIL=…
AUTH_ADMIN_PASSWORD=…        # secret
```

The username is a display-only handle; the seeded admin **logs in by e-mail + password**. A username
containing `-`, `.`, `_`, spaces or diacritics fails Identity's `AllowedUserNameCharacters` and
crashes auth-api at startup (loud, by design).

### TLS certificates

- **Real environment:** **Caddy** obtains and renews Let's Encrypt certificates automatically, one
  per vhost (`ACME_EMAIL` in the box `.env`). The app containers validate nothing — they speak plain
  HTTP behind the proxy. ACME needs the DNS A record to resolve **before** first bring-up.
- **Local prod-parity:** `scripts/init-prod-https.sh` mints a local CA + a `*.lotro.test` leaf for
  Caddy; `.docker/trust-ca-entrypoint.sh` installs the CA into the tms-api/frontend OS store so their
  OIDC back-channel to `https://auth.lotro.test` is trusted (.NET ignores `SSL_CERT_FILE`).
- **Local dev (host Kestrels):** the apps serve HTTPS with the **native** ASP.NET Core dev cert — run
  `dotnet dev-certs https --trust` once. No PFX, no mount.

### Databases

The system uses **two** databases: `lotro_translation` (TMS) and `lotro_auth` (Auth). Both live in
the environment's **Neon** project (one project per environment, ADR-0018) on the same branch.
A managed Postgres typically provisions one database — create the second:

```sql
CREATE DATABASE "lotro_auth";
```

`scripts/init-postgres.sh` does this automatically for the self-hosted (compose) Postgres on first
boot.

### Hygiene

- The box `.env` is the **only** live copy of the set → the owner keeps one encrypted off-box copy
  (password manager). Losing it costs a re-mint plus the reseed above — never data.
- `.gitignore` ignores `.env`, `.env.*` and `*.env`, whitelisting only the placeholder examples, so a
  filled env file cannot be committed by accident; the required **GitGuardian** check on `main` is
  the second net. Values travel to a box over ssh only — never through an issue, PR, or commit.

## Consistency rules that bite

The cross-service settings that are individually valid but break the system when they disagree. Most
"works locally, 401/redirect error on staging" failures are one of these.

1. **Issuer must equal the token `iss`.** auth-api stamps `OpenIddict__Issuer` into every token's
   `iss`; tms-api validates it against `Auth__Issuer` (`ValidIssuer`). The two MUST be byte-identical
   and MUST be the **public auth origin the browser uses**. Mismatch → tms-api `401`s every request.
   Outside Development the value may not contain `localhost` (the validator rejects it).

2. **Authority is the back-channel address, not the issuer.** `Auth__Authority` (tms-api) and
   `AuthSystem__Authority` (frontend) are where the app **fetches OIDC metadata + JWKS**; the issuer
   is what it **validates `iss` against**. They can legitimately differ — but in this repo they
   coincide in every environment: dev leaves `Auth__Authority` unset so it falls back to
   `Auth__Issuer` (`https://localhost:5003`, the host auth Kestrel), and both containerized stacks
   (Hetzner and the prod-parity rehearsal) set both to the **public proxy origin**. Two production
   traps: (a) in Production **OpenIddict rejects plain HTTP**, so the Authority MUST be `https` — use
   the public Caddy origin, never `http://auth-api:8080`; (b) that host MUST be reachable from inside
   the container and its cert trusted by the container's OS store (on the boxes it is a real Let's
   Encrypt cert, so this is free; only the local parity stack needs the CA shim).

3. **Frontend redirect URIs must be registered at the auth server.** The frontend sends
   `redirect_uri = <its public origin> + AuthSystem__CallbackPath` (and post-logout = origin +
   `SignedOutCallbackPath`). Those exact absolute URLs MUST appear in auth's
   `OpenIddict__WebClient__RedirectUris` / `…PostLogoutRedirectUris`. Any difference (scheme, host,
   trailing slash) → OIDC `invalid redirect_uri` at login. `AuthSystem__ClientId` must equal the
   registered web-client id (`lotrokoniecdev-web`). ⚠️ These URIs are written to the DB **only when
   the client row is created** — changing `DOMAIN_APP` later needs the
   [reseed](#reseed-traps--the-auth-seeder-is-create-if-missing).

4. **CORS origin = the frontend's public URL, as a bare origin.** auth/tms `Cors__AllowedOrigins__0`
   MUST be the browser app's exact origin — **lowercase scheme+host, no port if default, no userinfo,
   no path, no query, no trailing slash** (the validator rejects anything else at boot). It is the
   same value as a redirect URI's origin part.

5. **Behind the TLS-terminating proxy, forwarded headers are load-bearing.** Caddy MUST send
   `X-Forwarded-Proto` (and Host); all three apps read them (`UseForwardedHeaders`) to reconstruct the
   `https` scheme used for `iss`, `redirect_uri`, and `Secure` cookies. Trust is scoped to Caddy's
   exact host address (#399, narrowed from the `/24` to a `/32` by #506):
   `ForwardedHeaders__KnownNetworks__0` = `10.60.0.100/32` in `compose.hetzner.yaml`
   (`compose.prod.yaml` keeps the `/24` — it has no co-tenant stack to fence off), set for all three
   apps; a malformed CIDR aborts boot. That value MUST stay in lockstep with Caddy's `ipv4_address`
   on the default network (see [Topology](#topology)); moving Caddy's pinned IP without updating all
   three apps silently drops forwarded-header trust and breaks `https` reconstruction. Leaving it
   unset trusts every upstream (with an explicit `ForwardLimit = 1`), which is safe **only** while the
   container port is unreachable except through the proxy — so **never publish `:8080`**. In prod the
   apps serve HTTP only; Caddy owns TLS.

6. **The Data Protection keyring must be persistent.** auth-api + frontend need
   `DataProtection__KeyRingPath` pointing at a persistent volume (`/keys`). An ephemeral keyring →
   every deploy logs everyone out and breaks antiforgery + password-reset/email-confirmation links.
   Fails fast at boot if unset outside Development.

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
containerized frontend, Caddy TLS — on a laptop (ADR-0008 §4). It runs the **same four images and the
same proxy shape** as the box, so prod-only breakage surfaces before staging:

```bash
scripts/up-prod.sh --build          # PowerShell: scripts/up-prod.ps1 --build
```

It bootstraps `.env.prod` (with freshly generated OpenIddict secrets), the local CA + certs, and the
`*.lotro.test` hosts mapping, then runs `docker compose -f compose.prod.yaml up`. Verify:

```bash
curl --cacert .docker/prod-https/rootCA.crt https://auth.lotro.test/health
curl --cacert .docker/prod-https/rootCA.crt https://tms.lotro.test/health
# browser OIDC login: https://app.lotro.test
```

For an all-local run (no external SMTP/OTLP), add the profiles so the SMTP leg of the deep auth
`/health` passes and traces are viewable, and set `Email__Host=mailpit` / `Email__Port=1025` / `Email__Mode=None`
+ `OTEL_EXPORTER_OTLP_ENDPOINT=http://aspire-dashboard:18889` in `.env.prod`:

```bash
docker compose -f compose.prod.yaml --env-file .env.prod --profile local-smtp --profile local-otel up --build
```

### (Re)provisioning a box

1. **Owner:** Hetzner console → create the box (Ubuntu LTS, backups on for prod) with the owner's ssh
   **public key** → note the IP.
2. **DNS first** (ACME needs it resolving before certs can issue): registrar panel → A records for
   every public hostname → the new IP, TTL 300. Hostname list = the
   [env matrix](#environment-variable-matrix). Prod trio → prod IP, staging trio → staging IP.
3. **Bootstrap** (idempotent — re-running changes nothing). Preferred form (`-t` gives the GHCR prompt
   a TTY, so the PAT is typed at a hidden prompt and never lands in shell history):

   ```bash
   scp scripts/hetzner/bootstrap.sh root@<ip>:/root/
   ssh -t root@<ip> bash /root/bootstrap.sh          # prompts for GHCR user + read-only PAT
   ```

   The PAT is a GitHub token with **only `read:packages`**. For automation you *can* pass it via env
   (`ssh root@<ip> 'GHCR_USER=<u> GHCR_TOKEN=<pat> bash -s' < scripts/hetzner/bootstrap.sh`), but the
   inline assignment **lands in your local shell history** — prefer the prompt for hand runs. Without
   env vars and without a TTY the script skips the GHCR login with a warning and the rest still
   completes.

   **The GHCR login is optional today** — the four images are **public** packages, so `deploy` pulls
   them anonymously. It stays in the script because a *private* package would need it.

   **Bootstrap overwrites `/etc/ssh/sshd_config.d/00-hardening.conf` and `/etc/fail2ban/jail.local`**
   (it converges them to the repo version), so fold any hand-tuned directive into the script first.

   **The Docker leg adopts the box's engine, it does not install one** — see [Gotchas](#gotchas).
4. **Stack files:** copy `compose.hetzner.yaml` + `.docker/hetzner/` to `/opt/lotro/` (as `deploy`),
   assemble the box's `.env` from `.env.hetzner.example` ([Secrets](#secrets) has every value's source
   of truth and rotation command), `chmod 600` it.
5. **Bring-up (staging box first as the rehearsal):** as `deploy`, on each box:

   ```bash
   cd /opt/lotro     # .env is picked up automatically; COMPOSE_PROJECT_NAME comes from it
   docker compose -f compose.hetzner.yaml pull
   docker compose -f compose.hetzner.yaml up -d
   docker compose -f compose.hetzner.yaml logs -f migrator caddy   # one-shot migration + ACME
   ```

   Then re-check the [consistency rules](#consistency-rules-that-bite) across services, and run the
   [smoke test](#post-deploy-smoke-test) against the public origins from an external network —
   remember `GET / -> 200` proves nothing; smoke's fingerprint leg is the real check.
6. **Add swap on the prod box** and re-pin the CD host key (`ssh-keyscan`) — see
   [Gotchas](#gotchas) and [One-time setup per environment](#one-time-setup-per-environment).

### One-time: cutting a live box over to the two segregated networks (#506)

Both stacks are already running on these boxes, so #506 is a **migration of a live box**, not a fresh
bring-up. The lotro deploy recreates our containers on the new topology and creates `${project}_tks`;
the TKS containers keep running meanwhile, but on the old network and **without** the Caddy aliases
they need — so they must be recreated straight after (compose cannot move a running container between
networks). Do the whole sequence on **staging first**, prod only once staging is verified. A short TKS
outage is expected and acceptable pre-launch.

**Pre-flight** — record what you are changing, as `deploy` (`lotro-prod` shown; staging is
`lotro-staging`):

```bash
docker network ls                                     # today: ONE shared network, both stacks on it
docker network inspect lotro-prod_default \
  --format '{{range .Containers}}{{.Name}} {{.IPv4Address}}{{"\n"}}{{end}}'
```

Everything that is **not** ours in that list (every `tks-*` container) is exactly what this change
fences off. If Caddy already sits at `10.60.0.100` and a `lotro-prod_tks` network exists, the box has
been cut over already — stop here.

1. **Deploy lotro.** CD does it on merge; by hand: `IMAGE_TAG=<tag> bash /opt/lotro/deploy.sh`.
2. **Verify the boundary before touching TKS** — Caddy must hold both pinned addresses, and nothing
   but our four containers may remain on `10.60.0.0/24`:

   ```bash
   docker inspect lotro-prod-caddy-1 \
     --format '{{range $n,$c := .NetworkSettings.Networks}}{{$n}} {{$c.IPAddress}}{{"\n"}}{{end}}'
   docker network inspect lotro-prod_default \
     --format '{{range .Containers}}{{.Name}}{{"\n"}}{{end}}'
   ```

3. **Recreate the TKS stack onto `${project}_tks`** (its compose change is the twin ticket,
   koniecdev/TheKittySaver#295). Ship its new `compose.hetzner.yaml`, point `LOTRO_NETWORK` at THIS
   box's network, then recreate — compose cannot move a running container between networks:

   ```bash
   # from a TheKittySaver checkout on main:
   scp compose.hetzner.yaml lotro-prod:/opt/tks/compose.hetzner.yaml

   ssh lotro-prod
   sed -i 's|^LOTRO_NETWORK=.*|LOTRO_NETWORK=lotro-prod_tks|' /opt/tks/.env   # staging: lotro-staging_tks
   cd /opt/tks
   # -f is NOT optional: the file is compose.hetzner.yaml, so a bare `docker compose` dies with
   # "no configuration file provided: not found".
   docker compose -f compose.hetzner.yaml pull    # the new images carry the Caddy-only trust wiring
   docker compose -f compose.hetzner.yaml down    # NEVER -v: that would drop its DB/keyring volumes
   docker compose -f compose.hetzner.yaml up -d
   ```

4. **Verify both sites from outside the box** — ours with the [smoke test](#post-deploy-smoke-test),
   TKS by loading its public origin. A 502 on TKS means its containers did not land on
   `${project}_tks` (or lost their `tks-` service-key prefix) — Caddy itself is fine; re-check step 3.

## Continuous deployment (CD over ssh)

Merging to `main` deploys. The chain (ADR-0012 pipeline, ADR-0018 two-stage promotion, ADR-0034
transport):

```
CI green on main → cd.yml gate (pins the tested commit)
                 → build + Trivy-scan + cosign-sign + attest the 4 images → GHCR :sha-<short>
                 → deploy-staging   (AUTOMATIC — no gate)                 → lotro-staging box
                 → deploy-prod      (required reviewer — the promotion click) → lotro-prod box
```

| Workflow | Trigger | What it does |
|---|---|---|
| `cd.yml` → `gate` | CI finishing on main (`workflow_run`), `v*` tag, dispatch | the **CI-success gate**: CD fires when CI completes, proceeds only on a green CI, and pins the exact tested commit (`head_sha`) for build + rollout. Docs-only pushes run no CI, so they trigger no CD. |
| `cd.yml` → `build-and-push` | after a passing `gate` | builds the 4 images, **scans each with Trivy** (fails on a fixable HIGH/CRITICAL), then pushes to GHCR **signed (cosign keyless) + attested (SLSA provenance + SBOM)** — `:sha-<short>`, `:latest` on main, semver on tags |
| `cd.yml` → `deploy-staging` | after `build-and-push`, `STAGING_ENABLED == true`, **auto** | calls the reusable [`deploy.yml`](../../.github/workflows/deploy.yml) under `environment: staging` |
| `cd.yml` → `deploy-prod` | after `deploy-staging`, **behind the `production` approval gate** | the SAME reusable `deploy.yml` under `environment: production` |

**The two-stage promotion model.** Every merge to main with a green CI builds once and
**automatically deploys to staging**. The same `sha-<short>` then **waits at the `production`
environment** for a human. Clicking *Approve* (GitHub → the run → *Review deployments*) **is** the
staging→prod promotion: the identical image that passed staging is what production receives.

**The switches** (repo Variables): `CD_ENABLED=true` arms the deploy jobs at all; `STAGING_ENABLED=true`
arms the staging leg and makes prod wait on it (ADR-0018).

**Stale candidates expire after 24 h.** The gate pins the tested commit, so approving a days-old
run would deploy a days-old SHA. [`cd-janitor.yml`](../../.github/workflows/cd-janitor.yml) (#527)
cancels any CD run left `waiting` at the `production` gate for more than 24 hours (nightly cron,
04:25 UTC; on demand: `gh workflow run cd-janitor.yml`). Nothing is lost — every newer green-CI
merge produces a fresh candidate, and any older commit can still be deployed explicitly via
`cd.yml`'s `workflow_dispatch`.

**Cancelling a `waiting` run by hand:** the UI *Cancel* button and `gh run cancel` do **not** work
on approval-gated runs — they only file a cancellation request no runner will ever pick up, so the
run stays `waiting` ("requested to cancel" forever, #592). Use the same call the janitor makes:
`gh api -X POST repos/koniecdev/LotroKoniecDev/actions/runs/<run-id>/force-cancel`.

### What `deploy.yml` does

**Prod leg only, first (#534 — the N-1 promotion gate):** promotion is batched, so the sha the
approval replaces is the last *deployed* one, not the last *merged* one. The job resolves the sha
actually serving (last successful `production` deployment via the API, cross-checked against the
box-pinned `IMAGE_TAG`, which also stands in when the API cannot answer) and, when the span since it
touches `Migrations/`, re-runs the ADR-0024 proof against that baseline. Two different red verdicts,
two different fixes:

- **`RED` — the batch's schema breaks the release prod is running right now. Do not retry:** approve
  a candidate containing only the expand, let it serve, then promote the contract.
- **`COULD NOT RUN` — the proof itself failed to build, generate or start, so the batch is
  UNJUDGED,** not proven bad. Fix the infra failure named in the log and re-run the job. Do **not**
  split the batch, and do not reach for the `image_tag` dispatch: it skips the gate entirely, so
  nothing would be proven at all.

Migration-free promotions skip in seconds. An unresolvable baseline fails closed. The
nothing-serving-yet skip needs **two agreeing signals** — no `production` deployment on record ever
reached `success`, *and* the box pins no `IMAGE_TAG` — because it is the one verdict that lets a batch
through unproven; a silent box the API cannot corroborate therefore blocks instead of skipping (check
`/opt/lotro/.env` and `docker compose ps`; if it truly is the first deploy here, dispatch with an
explicit `image_tag`, which bypasses the gate by design). A manual `image_tag` dispatch bypasses the
gate with a warning — that path is yours to verify, and note that the deployment record it leaves
behind names the *run's* sha rather than the tag you rolled, which is exactly what the box
cross-check exists to catch on the promotion after it.

On the runner it resolves the tag to **digests**, verifies each image's build provenance (fails
closed), cuts the Neon pre-migration snapshot branch (MIGR-04) and publishes the restore point. Then
it ssh'es to **that environment's box** (`vars.HETZNER_HOST` — one box IS one environment), snapshots
the live config into `/opt/lotro/.previous/`, scp's `compose.hetzner.yaml`, the Caddyfile and
`scripts/hetzner/deploy.sh` into `/opt/lotro`, and runs the script:

1. validates the composed config **and the Caddyfile** — a typo aborts here, while the running Caddy
   is still serving its current config;
2. `pull`, then asserts the pulled digests are the ones whose provenance CD just verified (GHCR tags
   are mutable — this closes the gap between the gate and what actually starts);
3. **the migration gate:** runs the migrator as its own one-off container (`compose run --rm --no-deps
   migrator`) and proceeds only on exit 0. A bad migration therefore aborts the roll with the
   **previous release still serving** — it is a no-op for the live site;
4. `up -d` — only now are the app containers recreated, onto an already-migrated schema;
5. reloads Caddy (its Caddyfile is a bind mount — compose does not notice content changes);
6. pins `IMAGE_TAG` in the box `.env` **last, on success only**, so the file always describes what is
   actually running.

Finally the runner waits for the public origins to answer, then smokes them. Red smoke ⇒ the box is
rolled back automatically — **images and config** (from `.previous/`) — and the job fails.

> ⚠️ **Never fold the migration gate back into `up -d`.** `compose.hetzner.yaml` does declare
> `depends_on: { migrator: { condition: service_completed_successfully } }`, and it is tempting to let
> that be the gate. It is not: the condition gates the **start** of the apps, not their **creation**.
> `compose up` recreates every changed service first (and every deploy changes the image tag), so the
> old app containers are destroyed *before* the migrator runs — a failed migration then leaves the
> apps `created`-but-never-started and the site **down**, serving 502s through the surviving Caddy.
> Verified against Docker 29.6.1 (#490). Running the migrator as its own container first is what makes
> the "old release keeps serving" property real.

**There is no blue/green.** A 4 GB box cannot hold a second live set of three apps, so containers are
recreated in place: a deploy costs a few seconds of downtime per app, and the smoke necessarily runs
after the new containers are live (the retired ACA rollout could smoke a 0%-traffic candidate first).
The migration gate + the automatic rollback are what replace it. Recreating Caddy also blips
TheKittySaver, which it proxies.

**A rollback reverts code, never schema** (ADR-0023). Re-deploying the previous tag re-runs the
*older* migrator, which only applies pending migrations — it never reverts one — so the old app comes
back up against the **new** schema. That is safe precisely because every migration is N-1
backward-compatible; it is also why a bad *migration* is not a rollback case at all but a roll-forward
or a restore ([Database migrations](#database-migrations)).

### One-time setup per environment

**Precondition — the box needs a `deploy` user that owns the stack.** CD connects as `deploy` and
scp's over `/opt/lotro`, so a box brought up by hand as root is not deployable: there is no `deploy`
to log in as, and even once created it cannot read the root-owned `0600` `.env` nor overwrite the
root-owned stack files. `bootstrap.sh` provisions the user *and* converges the ownership of
`/opt/lotro` + `/opt/tks` recursively — that is the fix, not a `chown` by hand.

CD fails closed with an explicit "not wired for ssh deploys" error until each environment (`staging`,
`production`) carries these:

| Kind | Name | Value |
|---|---|---|
| var | `HETZNER_HOST` | the box IP — staging `91.98.74.228`, production `167.233.159.221` |
| var | `HETZNER_SSH_KNOWN_HOSTS` | `ssh-keyscan` output for that IP (host-key pinning — see below) |
| var | `HETZNER_USER` | optional; defaults to `deploy` |
| var | `AUTH_URL` / `TMS_URL` / `FRONTEND_URL` | the environment's public origins (smoke targets) |
| var | `NEON_PROJECT_ID` | that environment's Neon project (MIGR-04 snapshot; unset ⇒ the snapshot leg skips) |
| secret | `HETZNER_SSH_KEY` | the CD deploy **private** key for that box (below) |
| secret | `SMOKE_CLIENT_SECRET` | `= OpenIddict__ApiClientSecret` from that box's `.env` |
| secret | `NEON_API_KEY` | project-scoped Neon key (MIGR-04) |

> ⚠️ **A stale `SMOKE_CLIENT_SECRET` looks exactly like a broken deploy.** It is the one value here
> that lives in *two* places — the box `.env` and the GitHub secret — and nothing reconciles them. Roll
> the OpenIddict keys on a box without re-setting the secret and CD still deploys perfectly: images
> pull, the migration gate passes, the containers come up — and then smoke's `client_credentials` leg
> **401s**, CD judges the rollout red and **automatically rolls it back**. The log accuses the release;
> the fault is the secret. Whenever you rotate `OpenIddict__ApiClientSecret`, re-set this in the same
> breath — and set it **per environment**, because an env with no `SMOKE_CLIENT_SECRET` silently falls
> back to the repo-level one.

**Mint a CD deploy key per box** — one key per environment, so a staging compromise cannot touch prod.
It is the `deploy` user's key, never `root`'s:

```bash
ssh-keygen -t ed25519 -N '' -C 'github-cd-staging' -f ./cd-staging      # no passphrase: CD is non-interactive
ssh-copy-id -i ./cd-staging.pub deploy@91.98.74.228                     # or append the .pub to /home/deploy/.ssh/authorized_keys
gh secret set HETZNER_SSH_KEY --env staging < ./cd-staging              # the PRIVATE key
gh variable set HETZNER_HOST --env staging --body '91.98.74.228'
gh variable set HETZNER_SSH_KNOWN_HOSTS --env staging --body "$(ssh-keyscan -t ed25519 91.98.74.228)"
rm ./cd-staging ./cd-staging.pub                                        # GitHub + the box now hold the only copies
```

Repeat for `production` with the prod IP and `--env production`. Then delete the local private key —
losing it costs one re-mint, and a copy lying around is a spare key to prod.

**Host-key pinning is not optional.** `HETZNER_SSH_KNOWN_HOSTS` is why CD runs with
`StrictHostKeyChecking=yes`; without it the deploy would trust whoever answers on that IP and hand
them the deploy key. Re-provisioning a box changes its host key — re-run the `ssh-keyscan` line above
or every deploy fails at the ssh step (that failure is the pin working, not a bug).

### Deploy or roll back by hand

The same script CD runs, so a hand-run and a CD run leave the box in identical states:

```bash
ssh deploy@<box>
IMAGE_TAG=sha-1a2b3c4 bash /opt/lotro/deploy.sh    # roll to a specific commit's images
IMAGE_TAG=latest      bash /opt/lotro/deploy.sh    # roll to the tip of main
```

Rolling back = deploying the previous tag. The tag currently live is the `IMAGE_TAG` line in
`/opt/lotro/.env` (written only on a successful roll, so it never lies), and every CD run prints the
one it replaced (`PREVIOUS_IMAGE_TAG=…` in the job log, plus the deployed tag in the run summary).
GHCR keeps every `sha-<short>`, so any commit that ever built is still deployable. The config that was
live before the last deploy sits in `/opt/lotro/.previous/` — restore those three files first if you
are undoing a compose/Caddyfile change, not just an image change.

Two edges worth knowing. `deploy.sh` prunes unused images older than 14 days
(`PRUNE_UNUSED_OLDER_THAN`), so a tag older than that may need a fresh `docker pull` on the box; GHCR
still has it. And re-running CD for a commit (**Actions → CD → Run workflow**, optionally with an
explicit `image_tag`) still passes the staging→prod gate.

## Database migrations

### Strategy (ADR-0008 §6, ADR-0023)

Schema changes apply as a **pre-deploy step** — a one-shot container that runs to completion *before*
the APIs serve traffic — never from inside the application at startup. The rules:

- **Two write contexts, one job.** The Translation Management System (`ApplicationWriteDbContext`,
  schema `translation`, database `lotro_translation`) and the Auth server (`AuthDbContext`, schema
  `authsystem`, database `lotro_auth`) each have their own migration history. The job applies
  Translation first, then Auth.
- **The artifact is the migrator image** `ghcr.io/koniecdev/lotrokoniecdev-migrator` — built by
  `Dockerfile.migrator.prod` and published by CD. It bakes two **self-contained**
  `dotnet ef migrations bundle` executables (one per context) onto a lean `runtime-deps` base — no
  SDK, no `dotnet-ef` tool, no source.
- **Idempotent.** Each bundle applies only the migrations missing from that context's
  `__EFMigrationsHistory` table, so re-running it is a safe no-op.
- **The bundle RID follows the build platform** (#594). Because the bundles are self-contained, their
  architecture must match the `runtime-deps` base — which is a manifest list and resolves to whatever
  is being built for. The Dockerfile derives the RID from BuildKit's `TARGETARCH`
  (`amd64` → `linux-x64`, `arm64` → `linux-arm64`), so CD's amd64 runners keep producing the
  `linux-x64` image the Hetzner boxes run, and a local prod-parity build on Apple Silicon gets arm64
  bundles with no build-arg. Override with `--build-arg TARGET_RUNTIME=<rid>`; an architecture with no
  mapping fails the build rather than shipping a wrong-arch image.
- **Fail-fast = no half-migrated serving.** Any failure (unreachable DB, bad migration, missing
  connection string) exits non-zero. On the box, CD runs the migrator as **its own container before
  `up -d`** — see the warning in [What `deploy.yml` does](#what-deployyml-does).
- **Forward-only (ADR-0023).** There is no automated rollback step. EF down-migrations exist but are
  never run in a real environment; a bad migration is rolled forward with a new migration. The
  recovery valve for a logically-bad migration is a Neon restore — the deploy's pre-migration
  auto-snapshot (MIGR-04) or a point-in-time restore.

### Inputs (environment variables)

The migrator reads exactly two variables — Npgsql connection strings, one per context:

| Variable | Context |
|---|---|
| `ConnectionStrings__TranslationDatabase` | TMS write context (`lotro_translation`) |
| `ConnectionStrings__AuthDatabase` | Auth context (`lotro_auth`) |

Both databases must already exist (see [Databases](#databases)).

### Running migrations

**On a box** — CD does this for you (the migration gate). By hand, as `deploy`:

```bash
cd /opt/lotro
docker compose -f compose.hetzner.yaml run --rm --no-deps migrator
```

**Local production-parity stack (`compose.prod.yaml`).** Automatic — the `migrator` service runs to
completion and `auth-api` / `tms-api` wait on its success:

```bash
scripts/up-prod.sh --build
docker compose -f compose.prod.yaml --env-file .env.prod logs migrator   # watch it apply
```

**Anywhere else** — pull the published image and run it once against the target databases, using the
**same image tag** you are about to deploy for the APIs, so schema and code move together:

```bash
docker run --rm \
  -e ConnectionStrings__TranslationDatabase="Host=…;Database=lotro_translation;Username=…;Password=…;Ssl Mode=Require;Timeout=60" \
  -e ConnectionStrings__AuthDatabase="Host=…;Database=lotro_auth;Username=…;Password=…;Ssl Mode=Require;Timeout=60" \
  ghcr.io/koniecdev/lotrokoniecdev-migrator:<tag>
```

Expected tail on success:

```
== TRANSLATION MIGRATOR DONE ==
== AUTH MIGRATOR DONE ==
== MIGRATOR COMPLETE ==
```

A non-zero exit means migrations did **not** fully apply — do not roll out the APIs; read the log, fix
forward, re-run.

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
traffic moves, and the pipeline's failure path rolls back **app code only**. The real safety valve is
the database's own history: both real environments run on Neon (prod — ADR-0014; staging — a separate
Neon project, ADR-0018), and Neon keeps continuous page history supporting an instant point-in-time
restore of a branch to any moment inside the retention window.

The topology below was read live via the Neon API on **2026-07-05**:

| Environment | Neon project | Branch (single, default) | History retention |
|---|---|---|---|
| production | `lotro-translator-prod` (`empty-voice-65414159`) | `production` (`br-jolly-river-as0a1b99`) | **6 h** (21600 s) |
| staging | `lotro-translator-staging` (`holy-mode-18368797`) | `production` (`br-sweet-band-as9xg1ut`) | **6 h** (21600 s) |

- **6 hours is the Free-plan ceiling.** Re-verify the live value anytime:

  ```bash
  curl -s -H "Authorization: Bearer $NEON_API_KEY" \
    https://console.neon.tech/api/v2/projects/<project_id> | jq .project.history_retention_seconds
  ```

- **A branch restore is branch-wide.** `lotro_translation` and `lotro_auth` live on the same branch,
  so they always rewind **together** — exactly right for "undo the migrator run", since the one
  migrator applies both contexts.

#### Risk boundary — the accepted backup posture (MIGR-01, 2026-07)

**Neon-PITR-only; no off-platform logical backup is scheduled.** Consequence, stated plainly: **a bad
migration (or any data corruption) noticed more than 6 hours after the fact is unrecoverable.** The
blast radius today is a DB whose content is re-creatable by hand (re-import `exported.txt`, re-seed
the admin — zero production users), which is why the window is accepted. Revisit trigger: **the first
real translators start contributing** (their edits are *not* re-creatable) → add a nightly encrypted
`pg_dump` off-platform, or raise the Neon plan.

**MIGR-04 (#339) is in place:** every configured deploy auto-branches Neon right before the migrator
runs, which caps the **bad-migration** case regardless of the 6 h window (below). General data
corruption (not tied to a migrator run) is still bound by the 6 h window.

#### Procedure

Scenario: the migrator gate ran a migration that is *executionally* fine but *logically* wrong —
dropped or corrupted data, or broke the serving release past what N-1 compatibility (ADR-0023)
guarantees. Time matters: **the restore point must still be inside the 6 h retention window** (unless
you have the auto-snapshot, which never expires).

**0. Find the restore point.** The deploy run's step summary prints a **"DB restore point
(pre-migration)"** table — the UTC timestamp captured immediately *before* the migrator ran, plus the
target migration per context, plus the **auto-snapshot branch**. When it names one, prefer
[Restore from the auto-snapshot](#restore-from-the-auto-snapshot-migr-04) — no timestamp math, no 6 h
pressure.

**1. Put the last-good release back first.** After the restore the schema is pre-migration again, and
only the previous release's code is guaranteed against it (N-1 holds one step back — *new* code on the
*old* schema is exactly the combination nothing proves). Redeploy the previous tag on the box:

```bash
ssh deploy@<box>
IMAGE_TAG=<previous-good-sha> bash /opt/lotro/deploy.sh
```

(When the rollout fails by itself, CD's automatic rollback already did this — then only steps 2–4
remain.)

**2. Restore the Neon branch to just before the migrator ran.** Substitute the project + branch IDs
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
  history* → pick the timestamp.
- The restore is near-instant (copy-on-write). Active connections drop once; the apps' Npgsql pools
  reconnect on the next request.

**3. Verify.** Run the [Verifying](#verifying) queries — the bad migration's row must be **gone** from
that context's `__EFMigrationsHistory` (the restore rewinds schema, data and history together) — then
[smoke](#post-deploy-smoke-test) the environment.

**4. Roll forward.** Fix the migration in a new commit and let the normal pipeline redeploy — the
migrator gate re-applies from the rewound history. Once confident, delete the `pre-restore-*` safety
branch (Free-plan projects cap the branch count at 10):

```bash
curl -sf -X DELETE -H "Authorization: Bearer $NEON_API_KEY" \
  "https://console.neon.tech/api/v2/projects/<project_id>/branches/<preserved_branch_id>"
```

#### Restore from the auto-snapshot (MIGR-04)

Every deploy with the Neon leg configured creates a **pre-migration snapshot branch** in that
environment's Neon project — right after pinning the migrator image, right before starting the
migrator:

- **Shape:** `migr04-pre-<short-sha>-<utc-ts>`, branched from the project's default branch head, with
  **no compute endpoint** — it costs no compute, only pinned history/storage.
- **Where it is recorded:** the run summary's "DB restore point (pre-migration)" table — name + id.
- **Why it exists next to PITR:** a branch head never expires. PITR history lasts 6 h on the Free
  plan; the snapshot stays restorable however late the bad migration is noticed.
- **Configuration (per GitHub environment; optional):** env-scoped secret `NEON_API_KEY`
  (project-scoped) + variable `NEON_PROJECT_ID`. When either is missing the deploy logs
  `Neon snapshot skipped (not configured)` and proceeds; a Neon API error logs a warning and proceeds
  too. **The snapshot is a net, not a gate** — the deploy never fails because of it.

**Retention: at most ONE snapshot branch per project.** Right before creating the new snapshot, the
deploy deletes every older `migr04-pre-*` branch (both Free-plan projects sit near the 0.5 GB storage
cap, every branch pins history, and by the time deploy N+1 runs, deploy N's migration has proven
itself). Consequence: **the snapshot protects the latest deploy only**; an older bad migration falls
back to PITR (≤ 6 h) or the accepted risk boundary above.

**Restore procedure** — identical to the [PITR procedure](#procedure) except step 2: restore the
default branch **from the snapshot branch's head** instead of from its own history. `source_timestamp`
omitted ⇒ the source is *"restored to head"*, which for the snapshot **is** the pre-migration state;
`preserve_under_name` is **required** here because the restored branch has children:

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
- Cleanup order matters: delete the `migr04-pre-*` branch **before** the `pre-restore-*` branch — Neon
  refuses to delete a branch that still has children.

#### Rehearsal (staging drill)

Steps 2–4 can be rehearsed on staging at any time, without a deploy: restore the staging branch to
five minutes ago (a data no-op while staging is idle), check the [Verifying](#verifying) output is
unchanged, then delete the preserved branch. Do **not** run it while manual QA is in progress — the
restore drops connections and rewinds anything written after the restore point.

## Message broker (RabbitMQ)

Since the outbox/broker work (ADRs 0035–0037) each box runs a **single-node RabbitMQ container** in
the stack (`rabbitmq` in `compose.hetzner.yaml` — pinned image, pinned `hostname`, named
`rabbitmq-data` volume). Only **auth-api** talks to it: the outbox relay publishes committed rows to
the `lotro.emails` exchange and the e-mail consumer consumes `emails.send`. Everything either side
needs — exchanges, quorum queues, bindings, the dead-letter wiring — is **declared idempotently by
the app on channel open**, so a fresh broker needs zero manual provisioning.

The broker is deliberately a **soft dependency**:

- **auth-api boots and serves with the broker down** — there is no `depends_on` edge on purpose. The
  publisher connects lazily, the consumer retries with escalating backoff, committed outbox rows
  wait (ADR-0035's safety sweep is the ceiling). An outage delays confirmation e-mails; it never
  blocks login or token issuance.
- It surfaces as the **`rabbitmq` check on auth's deep `/health`** (deliberately not on
  `/health/ready`, same reasoning as SMTP) — the daily health ping is what tells you the container
  died between deploys.

### Management UI — over an ssh tunnel only

The UI is published on the **box loopback** only, never the internet:

```bash
ssh -L 15672:localhost:15672 deploy@<box-ip>
# then http://localhost:15672 — user `rabbitmq`, password = RABBITMQ_PASSWORD from the box .env
```

### The dead-letter parking lot (`emails.send.dlq`)

Messages land there in exactly two ways (ADR-0036): **poison** (unreadable payload or unusable
message id — rejected on first sight) and **exhausted** (`x-delivery-limit` = 5 redeliveries spent,
e.g. an SMTP outage the backoff ladder could not outlast). Nothing consumes the queue; it waits for
a human — a parked message usually means a user never got a confirmation e-mail, so diagnose from
the payload's `IdentityUserId` plus the matching outbox row's error.

**Replay = ack-and-republish, NEVER reject-requeue** (ADR-0036 §5). The parking lot is a quorum
queue with the *default* delivery limit (20) and no DLX of its own, so a reject-requeue loop
**silently drops** the parked message. In the management UI:

1. **Inspect** without consuming: `emails.send.dlq` → *Get messages* with ack mode *Nack message
   requeue true* (a nack is an "explicit return" — it does not tick the delivery count; a reject
   does).
2. **Replay**: *Publish message* on the **`lotro.emails`** exchange with the message's preserved
   routing key (e.g. `email.confirmation`), its original **`message_id` property** and body. The
   inbox deduplicates on the message id (ADR-0037), so replaying an already-processed id is a safe
   no-op.
3. Only after the replay demonstrably processed (e-mail sent / inbox row present): remove the parked
   copy — *Get messages* with ack mode *Automatic ack*.

### Traps

- **The broker-carrying release needs the box `.env` updated FIRST**: append `RABBITMQ_PASSWORD`
  (`openssl rand -base64 24`) to `/opt/lotro/.env` on **both** boxes before merging it. Missing it
  is loud, not silent: compose interpolation fails (`${RABBITMQ_PASSWORD:?…}`) inside `deploy.sh`'s
  config-validation step, which aborts the deploy while the old release keeps serving.
- **`RABBITMQ_DEFAULT_PASS` applies on first boot only** (empty data volume). Editing the box `.env`
  later rotates what auth-api presents but **not** what the broker expects — rotate in lockstep via
  `rabbitmqctl change_password` (exact commands: the
  [secrets table](#secret-material--source-of-truth-and-how-to-rotate)).
- **Queue arguments are immutable.** Redeclaring `emails.send` with different `x-*` arguments fails
  the channel with `PRECONDITION_FAILED` (ADR-0036). Changing them on a live box means draining and
  deleting the queue first — treat it as a small migration, not a config tweak.
- **Never remove the `hostname:` pin** on the service. RabbitMQ keys its on-disk state by node name
  (`rabbit@<hostname>`); an unpinned recreation boots a fresh node beside the old data and orphans
  the volume's quorum queues — parked dead letters included.

## Post-deploy smoke test

One command that gives a green/red signal that a deployed environment came up correctly, without
manual clicking — run it as the final [bring-up](#bringing-the-stack-up) step and after every
subsequent deploy (CD runs it for you). `scripts/smoke.sh` (with a `scripts/smoke.ps1` twin) takes the
three base URLs + the OpenIddict API client secret and exercises the four legs that actually break on
a deploy:

| # | Check | Pass condition |
|---|---|---|
| 1 | **Health** | `GET {auth}/health/ready` = 200, `GET {tms}/health/ready` = 200, `GET {frontend}/` = 2xx/3xx |
| 2 | **OIDC token** | `POST {auth}/connect/token` (client_credentials) = 200 + an `access_token` |
| 3 | **Token accepted by tms** | anonymous `GET {tms}/api/v1/game-versions` = **401**; the same call **with** the bearer token is **NOT 401** |
| 4 | **File distribution** | `GET {tms}/api/v1/translation-files/{lang}` = 200 + `ETag`, then a re-GET with `If-None-Match` = 304 |

It prints a `✓`/`✗`/`⚠` per check and **exits non-zero (1) on any failure** (a usage/config problem
exits 2). Two behaviours are deliberate and worth knowing before you read a result:

- **Leg 3 expects 403, and that is success.** The only non-interactive OIDC grant in a deployed
  environment is **client-credentials**. Such a token carries **no user role**, and every TMS endpoint
  is role-gated — so a *validated* token is **403 Forbidden**, not 200. The check therefore proves the
  token is **accepted** (got past authentication), pairing it with an anonymous 401 to prove the
  endpoint is genuinely protected. A **401 with a valid token is the real red flag**: it means tms
  rejected it — almost always an issuer / audience / JWKS mismatch (see
  [Consistency rules that bite](#consistency-rules-that-bite), rules #1–#2), or a **stale
  `SMOKE_CLIENT_SECRET`**.
- **Leg 4 warns (does not fail) on 404.** A freshly deployed but not-yet-imported environment has no
  translation artifact, so the endpoint returns 404 — the endpoint is up, there is just nothing to
  distribute yet.

### Running it

```bash
# A real environment (publicly-trusted Let's Encrypt cert — no --insecure needed):
SMOKE_CLIENT_SECRET="$OPENIDDICT_API_CLIENT_SECRET" scripts/smoke.sh \
  --auth-url     https://auth.lotro-translator.pl \
  --tms-url      https://tms.lotro-translator.pl \
  --frontend-url https://lotro-translator.pl
# PowerShell twin: $env:SMOKE_CLIENT_SECRET='…'; scripts/smoke.ps1 -AuthUrl … -TmsUrl … -FrontendUrl …

# The local prod-parity stack (compose.prod.yaml): certs are the local CA, so add --insecure:
scripts/smoke.sh --insecure \
  --auth-url https://auth.lotro.test --tms-url https://tms.lotro.test --frontend-url https://app.lotro.test \
  --client-secret "$(grep '^OpenIddict__ApiClientSecret=' .env.prod | cut -d= -f2-)"

# The local dev stack (host Kestrels + untrusted dev cert):
scripts/smoke.sh --insecure \
  --auth-url https://localhost:5003 --tms-url https://localhost:5002 --frontend-url https://localhost:7017 \
  --client-secret dev-api-secret-min-32-characters-long
```

Each flag has a `SMOKE_*` environment fallback (`--auth-url`/`SMOKE_AUTH_URL`, `--tms-url`/`SMOKE_TMS_URL`,
`--frontend-url`/`SMOKE_FRONTEND_URL`, `--client-secret`/`SMOKE_CLIENT_SECRET`,
`--client-id`/`SMOKE_CLIENT_ID` (default `lotrokoniecdev-api`), `--scope`/`SMOKE_SCOPE` (default
`service`), `--lang`/`SMOKE_LANG` (default `pl`), `--timeout`/`SMOKE_TIMEOUT` (default 15),
`--insecure`/`SMOKE_INSECURE=1`). `bash scripts/smoke.sh --help` prints the full reference.

**In CI:** `deploy.yml` runs it against the environment's public origins after `up -d`; a red smoke
triggers the automatic rollback. The [`Smoke test`](../../.github/workflows/smoke.yml) reusable
workflow stays runnable **on demand** (`workflow_dispatch` — enter the three URLs).

> **`GET / -> 200` never proves the frontend works.** A `[StreamRendering]` page returns 200 with its
> spinner frame before it fetches anything. Smoke leg 1's frontend check is paired with the
> **fingerprint** assertion — `@Assets[]` renders `_framework/blazor.web.<hash>.js` only when
> `MapStaticAssets` resolved its manifest. That is the signature of a healthy image.

## Observability & monitoring

**What exists today:**

- **Structured logs** — every app logs JSON to stdout (Serilog). Read them on the box:
  `docker compose -f compose.hetzner.yaml logs -f <service>`.
- **Health endpoints** — deep `/health` (DB, + SMTP on auth), `/health/live`, `/health/ready`
  (DB-free by ADR-0025, so container probes cannot keep the scale-to-zero Neon compute awake — that
  ruling outlives Azure, because **Neon still suspends**).
- **The daily health ping** — [`.github/workflows/health-ping.yml`](../../.github/workflows/health-ping.yml)
  probes the prod origins once a day (06:40 UTC) on the **deep** `/health`, so it is the one check
  that proves the database is reachable. A failed run e-mails the last committer of that file.
  Trigger on demand: `gh workflow run health-ping.yml`.
- **Post-deploy smoke** — every CD rollout, with automatic rollback on red.

**The gap, stated plainly (ADR-0034 §Consequences).** The move off Azure **deleted the alerting
stack**: the Azure Monitor alert rules (replica restart, 5xx spike, memory/CPU saturation, log-error
spike), the Log Analytics workspace and Application Insights (traces, Application Map, KQL) are all
gone with the subscription. Today there is **no metric alerting and no trace backend** — a crash-loop
between two daily pings is invisible unless a user reports it.

`OTEL_EXPORTER_OTLP_ENDPOINT` stays wired in every app (empty ⇒ exporter off), so pointing the stack
at a collector is a one-variable change; the `aspire-dashboard` profile remains for local traces.
**A real telemetry sink is a later, deliberate decision** — ADR-0034 accepted the shrink on purpose
(pre-launch, zero users, cost-driven migration); revisit when real translators depend on the site.

## Disaster recovery

Each box is **fully disposable** — nothing on it is the source of truth for anything:

| What | Lives | Recovery |
|---|---|---|
| Databases | Neon (prod + staging, PITR + MIGR-04 snapshots) | nothing to do |
| Images | GHCR (built by `cd.yml`) | nothing to do |
| Config | this repo (`compose.hetzner.yaml`, Caddyfile) | scp again |
| Secrets | `/opt/lotro/.env` per box — owner's backup + every value re-mintable ([Secrets](#secrets)) | restore or re-mint |
| TLS certs | Caddy volume; Let's Encrypt re-issues | automatic on first bring-up |
| DP keyrings | `auth-keys` / `frontend-keys` volumes | losing them logs everyone out; nothing to restore |

Recovery = **new VPS → `bootstrap.sh` → scp stack files → restore/re-mint the box's `.env` →
`docker compose up -d` → re-point DNS A records → re-pin `HETZNER_SSH_KNOWN_HOSTS`**. Hetzner backups
(prod box) are a shortcut for the same outcome, not a dependency.

## Gotchas

- **`/opt/lotro` belongs to `deploy` — never write into it as `root`.** CD ssh's in as `deploy` and
  **overwrites** `compose.hetzner.yaml`, `.docker/hetzner/Caddyfile` and `deploy.sh` on every rollout.
  A file that a hand-run `scp` left **root-owned** is not writable by `deploy` (the directory being
  deploy-owned does not help — `scp` truncates the existing file, it does not unlink it), so the next
  CD run dies in *"Sync the stack files to the box"* and the rollout never starts. This is not
  theoretical: it failed the #507 production rollout on 2026-07-13. If you must stage files by hand,
  `scp` as `deploy` — or `chown -R deploy:deploy /opt/lotro` afterwards. The same applies to `deploy.sh`
  itself: run it **as `deploy`** (`sudo -u deploy env IMAGE_TAG=… bash /opt/lotro/deploy.sh`), because
  its final step rewrites `.env`, and a root-owned `.env` locks CD out of the box for good.
  (`/opt/tks` has no CD and is root-owned — that one is fine to touch as root.)
- **The bootstrap Docker leg adopts the box's engine, it does not install one.** The live pair runs
  **Ubuntu's** `docker.io` + `containerd` + `docker-compose-v2`, not Docker's `docker-ce` stack — and
  the two CONFLICT. An unguarded `apt-get install docker-ce containerd.io` on such a box is an engine
  **swap**: apt removes the running engine, dockerd restarts, and every container on the host goes
  with it — *both* stacks, `/opt/lotro` **and** `/opt/tks`. `bootstrap.sh` therefore probes for
  `docker` + `docker compose` and installs `docker-ce` only when the box has no engine at all (fixed
  2026-07-13, #502). Do not "simplify" that guard away; the engine's vendor is irrelevant to us, its
  presence is not.
- **Two segregated networks per box; Caddy is the only shared component (#506).** Our stack lives
  alone on `${project}_default` (`10.60.0.0/24`); the guest TheKittySaver stack lives alone on
  `${project}_tks` (`10.61.0.0/24`, which our `compose.hetzner.yaml` defines and TKS joins as
  `external` — its twin ticket koniecdev/TheKittySaver#295). Caddy sits on both at pinned static IPs
  (`10.60.0.100` / `10.61.0.100`) and is the ONLY container that crosses. This fences off the
  cross-stack pivot and the header-spoof that one shared network allowed (see the ADR-0034
  amendment). **Coordinated bring-up:** deploying this topology change **re-creates the lotro stack**,
  so the TKS containers keep running but lose the Caddy-side aliases on the old network — the TKS
  stack must be re-pointed to `${project}_tks` and re-upped (`down` + `up -d` in `/opt/tks`) **right
  after** the lotro deploy on each box (**staging first, then prod**; brief TKS downtime is fine
  pre-launch).
- **Compose service KEYS must still be globally unique per network.** Compose registers the service
  key itself as a Docker DNS alias on top of `container_name`. Before #506 both stacks shared one
  network, so a guest whose compose also said `frontend:`/`auth-api:` collided with ours and Caddy's
  `reverse_proxy frontend:8080` round-robined into the WRONG stack (2026-07-12 prod incident:
  lotro-translator.pl served uratujkota.pl). Segmentation removes that cross-stack collision, but the
  `tks-` prefix on every TKS service key stays the rule — Caddy resolves `reverse_proxy tks-frontend:8080`
  on the `tks` network, and the public-hostname aliases must remain unique there. Detection:
  `docker inspect -f '{{range $k,$v := .NetworkSettings.Networks}}{{$v.Aliases}}{{end}}' <ctr>` — no
  alias may appear on two containers of the same network.
- **Docker bypasses ufw for published ports** (it programs iptables directly). Our stacks publish only
  Caddy's 80/443 — which ufw allows anyway. Never publish another service's port "just to debug";
  exec into the network instead
  (`docker compose exec caddy wget -qO- http://tms-api:8080/health`).
- **sshd config precedence:** sshd honours the *first* occurrence of a keyword, and
  `/etc/ssh/sshd_config.d/` is included at the top in lexical order. Bootstrap's hardening lives in
  `00-hardening.conf` precisely so it wins over cloud-init's `50-cloud-init.conf` — don't rename it to
  a higher number.
- **ACME fails until DNS propagates** — that's retry-resolved, not an error to fix. Start DNS before
  bring-up; Caddy keeps retrying on its own.
- **First cold hit after Neon scale-to-zero** can race auth's 20 s connection-open against Neon's
  ~31 s resume (known pre-existing bug; connection strings carry `Timeout=60`). The always-on box
  shrinks the window but does not fix it.
- **4 GB box + ~9 containers → add swap on the prod box.** Hetzner images ship without swap. One-time,
  as root — safe on a running box (no restart), idempotent by the guards:

  ```bash
  swapon --show | grep -q . || { fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile; }
  grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
  ```

  Deliberately NOT in `bootstrap.sh`: swapfile creation + `/etc/fstab` edits can't be faithfully
  proven in the container idempotency harness.
- **Container log rotation is unbounded by default** (json-file driver, small VPS disk). Cap it at the
  compose level (`logging: { driver: json-file, options: { max-size: "10m", max-file: "3" } }` per
  service) — NOT via `/etc/docker/daemon.json` on the live boxes, since a daemon-config change
  restarts dockerd and bounces every running container.
- `.github/workflows/*` pushes need the koniecdev token.

## History — the Azure era

From M6 (2026-06) until **2026-07-12** the stack ran on **Azure Container Apps** (Terraform-provisioned,
Key Vault secrets, Log Analytics + Application Insights, scale-to-zero with a scheduled warm window).
The Azure for Students subscription was disabled that day — credits exhausted, renewal refused — and
both prods went dark. [ADR-0034](../adr/0034-hetzner-vps-instead-of-azure-container-apps.md) records
the decision to move; [`hetzner-migration-plan.md`](hetzner-migration-plan.md) is the executed
playbook; epic #486 tracked it. The ACA-specific workflow legs were removed from the repo by #492.
The Terraform root and the Key Vault seeders were taken out of the build but **kept as a read-only
tombstone** in [`azure-graveyard/`](azure-graveyard/README.md) — the ADRs of the Azure era argue
about those files line-by-line, so they stay readable. Nothing in there is runnable: the
subscription is disabled and the Terraform state died with it.

**Recorded so nobody retries it:** with the subscription disabled, Key Vault serves secret *metadata*
but refuses every *value* read (`az keyvault secret list` works and returns the 8 names;
`az keyvault secret show` → **Forbidden — the subscription associated with this vault has been
disabled**). **No secret value was recoverable.** Every secret on the Hetzner boxes was re-minted from
scratch (see [Secrets](#secrets)) — which is also why the auth DBs needed the
[reseed](#reseed-traps--the-auth-seeder-is-create-if-missing).

Two ADR rulings died with the platform and are marked obsolete-by-platform: ADR-0027 (scheduled warm
window — the boxes are always on) and ADR-0029 (single-active-revision sweep — there are no
revisions). ADR-0025 (DB-free readiness probes) **still binds**: Neon still scales to zero.

## See also

- [`compose.hetzner.yaml`](../../compose.hetzner.yaml) — the deployed stack; the authoritative list of
  what each container consumes.
- [`.env.hetzner.example`](../../.env.hetzner.example) — the box env template (every key, placeholder values).
- [`.env.example`](../../.env.example) / [`.env.prod.example`](../../.env.prod.example) — the dev-compose
  and production-parity env templates.
- [`compose.yaml`](../../compose.yaml) / [`compose.prod.yaml`](../../compose.prod.yaml) — the dev and
  production-parity stacks.
- [`scripts/hetzner/`](../../scripts/hetzner/) — `bootstrap.sh` (provision a box) + `deploy.sh` (the
  rollout script CD runs).
- [ADR-0034](../adr/0034-hetzner-vps-instead-of-azure-container-apps.md) — the Hetzner decision.
- [ADR-0008](../adr/0008-cloud-agnostic-deployment-and-environment-strategy.md) — the provider-neutral
  container contract this runbook operationalizes.
- [ADR-0023](../adr/0023-forward-only-n-1-backward-compatible-migrations.md) / [ADR-0024](../adr/0024-n1-backward-compat-ci-proof.md) — forward-only, N-1 compatible migrations.
