# Deployment runbook

> Operator manual for running LotroKoniecDev in a real environment. Two **Hetzner VPS** boxes run
> `docker compose` behind Caddy ([ADR-0034](../adr/0034-hetzner-vps-instead-of-azure-container-apps.md));
> the database is **Neon** (ADR-0014), images live in **GHCR**, and CD deploys **over ssh**.
>
> **Scope:** the topology, the environment-variable matrix, secrets and their rotation, the
> consistency rules that bite, bring-up, continuous deployment, database migrations, the message
> broker, the smoke test, observability and disaster recovery.
>
> The app contract is **provider-neutral** (ADR-0008): every app is an OCI image behind a TLS
> ingress, so only the box sections are Hetzner-specific. Until 2026-07-12 the stack ran on Azure
> Container Apps — see [History — the Azure era](#history--the-azure-era).

## Contents

- [Topology](#topology) — the fleet, the boxes, the filesystem layout
- [Services & the container contract](#services--the-container-contract) — incl. [who the kernel kills first](#memory-pressure-who-the-kernel-kills-first) when a box runs out of memory
- [Environment variable matrix](#environment-variable-matrix) — every setting, per service × environment
- [Secrets](#secrets) — where they live, how to generate them, how to rotate them
- [Consistency rules that bite](#consistency-rules-that-bite) — issuer / redirect / authority / CORS
- [Bringing the stack up](#bringing-the-stack-up) — dev, prod-parity, (re)provisioning a box, and the one-time network cutover
- [Continuous deployment (CD over ssh)](#continuous-deployment-cd-over-ssh) — build once → automatic staging → gated prod promotion
- [Database migrations](#database-migrations) — strategy, running them, and recovering from a bad migration (Neon PITR + the MIGR-04 auto-snapshot)
- [Database backups](#database-backups) — the Neon plan, the nightly encrypted dump to R2, and how to get damaged rows back (ADR-0071)
- [Message broker (RabbitMQ)](#message-broker-rabbitmq) — the in-stack broker, the dead-letter parking lots, replay and rotation traps
- [Post-deploy smoke test](#post-deploy-smoke-test) — one command checks a deployed environment end to end
- [Observability & monitoring](#observability--monitoring) — the Grafana stack, its alerts, and the prod agent
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
| Provider / model | Hetzner Cloud **CX23** × 2 (2 vCPU, 4 GB RAM, amd64). Tight for two app stacks plus Caddy: watch memory before adding services. ADR-0034 §1 argued for one CX32; it was not available at checkout, so the fleet is two CX23 (its 2026-07-12 amendment) |
| Location | Nuremberg (nbg1), Germany |
| OS | Ubuntu 26.04 LTS (resolute); `bootstrap.sh` is verified on 24.04 and 26.04 |
| Backups | Hetzner backups on **lotro-prod only** (~20% surcharge); staging is disposable |
| Memory | 4 GB RAM + a **2 GiB `/swapfile`** at `vm.swappiness = 10`, laid down by `bootstrap.sh` and persisted in `/etc/fstab` (#708). The Hetzner image ships no swap, which turns any spike into an OOM kill instead of a slowdown |
| Users | `root` (key-only) · `deploy` (docker group, key-only, locked password — CD and day-2 ops run as this user) |
| Firewall | ufw: deny incoming except **22/80/443**, allow outgoing · **`DOCKER-USER`**: from the public interface only **80/443** reach a container, everything else published is dropped. ufw cannot filter a published port (Docker's DNAT runs first), this chain can (#808). Rules script `/usr/local/sbin/docker-user-firewall`, re-applied by `docker-user-firewall.service` whenever Docker starts, both laid down by `bootstrap.sh` |
| Intrusion / patching | fail2ban (sshd jail, systemd backend) · unattended-upgrades, plus a reboot on the nights an update needs one: **staging 01:30 UTC, prod 02:30 UTC** (#1327, see [Gotchas](#gotchas)) |
| Container runtime | Docker Engine + compose plugin (the box's own — bootstrap **adopts** it, never swaps it: see [Gotchas](#gotchas)) |
| Registry | Our four images on **ghcr.io**, Caddy and RabbitMQ on Docker Hub: all **public**, pulled without a login. `bootstrap.sh` stores no registry login, and `deploy.sh` ignores `deploy`'s Docker config (#1330, see [Gotchas](#gotchas)) |
| DB | none on the box — **Neon** is the database for prod AND staging (ADR-0034 §2) |
| Ingress | **Caddy**, one vhost per app, automatic Let's Encrypt certificates |
| Networks | **Two segregated Docker networks per box** (#506): `${project}_default` (`10.60.0.0/24`, our stack) and `${project}_tks` (`10.61.0.0/24`, the guest TheKittySaver stack). **Caddy is the only container on both** (static IPs `10.60.0.100` / `10.61.0.100`). The apps trust `X-Forwarded-*` from Caddy's `10.60.0.100/32` only, so keep that /32 in step with Caddy's `ipv4_address`. A third network, `${project}_obs`, carries the hand-deployed observability project (ADR-0050 §2): Caddy is on it too, with no pinned address, only so the ingest vhost can reach the backend containers (#755) |

Images are `linux/amd64` only (no multi-arch buildx), because the boxes are amd64. Never "fix" a pull
error by enabling emulation.

### Filesystem layout

| Path | Contents | Owner |
|---|---|---|
| `/opt/lotro` | LotroKoniecDev stack: `compose.hetzner.yaml`, `.docker/hetzner/Caddyfile`, `.env` (`chmod 600`, never committed — template: `.env.hetzner.example`), `deploy.sh`, `.previous/` (the last-good config snapshot) | `deploy` |
| `/opt/tks` | TheKittySaver stack (its own epic; joins the same Caddy through its own vhosts) | `deploy` |
| `/opt/actions-runner` · `/home/ci-runner` | **Staging box only — a guest.** TheKittySaver's self-hosted GitHub Actions runner (koniecdev/TheKittySaver#831, its ADR-0047): user `ci-runner` (in `docker` — its integration suite uses Testcontainers), unit `tks-ci-runner.service`, the .NET SDK and NuGet cache under its home. Disk: 4.8 GB after its first job (2026-10-02), about 7–9 GB once a full Release build tree sits in `_work`. One job at a time; registered to the **private** TKS repo only — never register a runner on these boxes to this public repo. To uninstall, delete the runner on the TKS side first, then follow TheKittySaver `docs/deployment/runbook.md` → "CI runner on the staging box" → Uninstall | `ci-runner` (TKS) |
| `/opt/obs` | The observability stack: `compose.observability.yaml`, the Alloy/Loki/Prometheus/Tempo configs, Grafana provisioning, `.env` (`chmod 600` — template: `.env.observability.example`). Its **own** compose project, deployed by hand, never by CD (ADR-0050 §2) | `deploy` |

## Services & the container contract

Four OCI images (built by the four multi-stage Dockerfiles, published to GHCR by CD) behind one
TLS-terminating ingress:

| Service | Image (`ghcr.io/koniecdev/…`) | Listens | Health | Persists |
|---|---|---|---|---|
| **auth-api** | `lotrokoniecdev-auth-api` | `:8080` (HTTP) | `/health` (deep: DB + SMTP + broker; needs the health check key, else 404 — ADR-0058), `/health/live`, `/health/ready` (probes — run no checks, ADR-0025) | Data Protection keyring → `/keys` |
| **tms-api** | `lotrokoniecdev-tms-api` | `:8080` (HTTP) | `/health` (deep: DB; needs the health check key, else 404 — ADR-0058), `/health/live`, `/health/ready` (probes — run no checks, ADR-0025) | nothing on the box: the translation file is stored in the database. A throwaway copy of it sits in the container's `/tmp/lotro-translation-files` (about 82 MB at full size, written again when missing — ADR-0064) |
| **frontend** | `lotrokoniecdev-frontend` | `:8080` (HTTP) | `/health/live`, `/health/ready` (no deep check) | Data Protection keyring → `/keys` |
| **migrator** | `lotrokoniecdev-migrator` | one-shot (exits 0) | exit code | — |
| _ingress_ | **Caddy**, pinned by tag and digest in the `x-caddy-image` anchor of `compose.hetzner.yaml` (`compose.prod.yaml` mirrors it; the anchor's comment says how Dependabot moves both; never 2.11.6, it crashes — #988) | `:80`, `:443` | — | ACME certs + config volumes |
| _broker_ | **RabbitMQ** (`rabbitmq:4.3.4-management-alpine`, pinned — see the compose comment) | `:5672` in-stack (AMQP; auth-api, and tms-api for the account events of ADR-0065) | `rabbitmq-diagnostics ping` (container healthcheck) + the `rabbitmq` check of auth's deep `/health` | broker state (users, quorum queues, parked dead letters) → `rabbitmq-data` volume |

Container contract (ADR-0008 §2): each app serves **plain HTTP on `:8080`** behind a TLS-terminating
ingress, runs **non-root**, logs **structured JSON to stdout**, and takes **all runtime
configuration from environment variables**. Only Caddy publishes internet-facing ports. The apps use
`expose:` and are reachable **only** through the proxy. The one loopback exception is the broker's
management UI on `127.0.0.1:15672`, reached over an ssh tunnel (see
[Message broker](#message-broker-rabbitmq)). The migrator runs to completion *before* the apps are
recreated, so nothing ever serves on a half-migrated schema.

### Memory pressure: who the kernel kills first

When a **box** runs out of memory, the kernel picks its victim by `oom_score`. On these boxes the
whole RSS of a box is worth about **45 points** of it, while one unit of `oom_score_adj` is worth
**0.66** of a point, so the adjustment decides the order. Left at the default `0`, the fattest
container dies first: on the staging box that was `obs-alloy`, which carries every signal of both
stacks on that box (#786).

**The two boxes are ordered in opposite directions on purpose.** The observability backend lives
on the *staging* box, so that a prod outage does not take the evidence with it (ADR-0050 §1). On that
box the observability stack is the production-critical workload, and the applications serve one
developer. On the prod box the applications are the product, and the only
observability component is the agent, whose death costs telemetry, not the service.

| Container | staging | prod | Why |
|---|---|---|---|
| `auth-api`, `tms-api`, `frontend`, `rabbitmq` | **+500** | **0** | `${OOM_SCORE_ADJ_APP}` in `/opt/lotro/.env`. First to go on staging; on prod they sit on the host daemons' own line |
| `tks-adoption-api`, `tks-frontend` | **+500** | **0** | `${OOM_SCORE_ADJ_APP}` in `/opt/tks/.env` — same knob, same values, koniecdev/TheKittySaver#757 |
| `migrator`, `tks-migrator` | 0 | 0 | literal, on both boxes. The deploy gate is not a workload to sacrifice: the app knob would make a rollout the staging box's first victim for nothing |
| `caddy`, `caddy-validate` | −100 | −100 | literal. The only ingress on either box, and on staging it also serves the obs ingest vhost prod pushes through (ADR-0050 §4). −100 and not deeper, because it restarts in seconds and the prod agent retries connection errors and 5xx, so the worst case is a short ingest gap |
| `loki`, `prometheus`, `tempo`, `grafana` | −200 | *(staging only)* | literal — the `backend` profile pins them to one box, so their role never changes |
| `preflight` | −200 | −200 | literal. It has no profile, so unlike the four above it runs in **both** roles; it is a one-shot that exits in milliseconds |
| TheKittySaver CI build tree (`dotnet build`/`test`, Stryker, the test containers' clients) | **+1000** | *(staging only)* | set per job step by the runner from `PIPELINE_JOB_OOMSCOREADJ=1000` in `tks-ci-runner.service` (koniecdev/TheKittySaver#831). The first victim on the box, on purpose: a killed build is a red CI run, nothing else |
| TheKittySaver CI runner (`Runner.Listener` / `Runner.Worker`) | **+500** | *(staging only)* | `OOMScoreAdjust=500` on the unit — the staging apps' line, so after the build the kernel picks between the runner and the apps by size, and never reaches the observability stack first |
| `obs-alloy` | **−300** | **+300** | `${OBS_ALLOY_OOM_SCORE_ADJ}` in `/opt/obs/.env`. The only value in that project that inverts, because `alloy` has no profile and runs on both boxes from one definition |

**The agent.** Prod's telemetry does not go through the staging agent. The prod agent pushes
through the ingest vhost on Caddy straight into `loki` / `prometheus` / `tempo` (ADR-0050 §4), so on
the staging box Caddy, not the agent, is what prod's telemetry depends on. The staging agent still
gets the deepest protection on its box: it is the only OTLP receiver and log tailer for both
application stacks there, the only source of the host and container metrics the fleet rules read,
and the fattest container in its project, so an unguided kernel would pick it first.

**Why the agent has a knob and not a literal.** The host's own daemons (`fail2ban`,
`unattended-upgrades`, `networkd-dispatcher`, …) sit at `0` on both boxes, which puts them at about
672. The agent must be *below* that line on staging and *above* it on prod. A negative literal would
also fail on prod: with `mem_limit: 512m`, an agent at −200 tops out at an `oom_score` of about 590,
so the kernel would kill the host daemons one by one before it reached the agent. `preflight` refuses
to start the stack when the value's sign does not match the box's role, because a wrong sign is
silent everywhere else.

**Read it back from the kernel, never from the compose file.** `oom_score` is what decides, and it
moves with RSS:

```bash
for c in $(docker ps --format '{{.Names}}'); do
  pid=$(docker inspect -f '{{.State.Pid}}' "$c")
  printf '%-32s adj=%-6s score=%s\n' "$c" "$(cat /proc/$pid/oom_score_adj)" "$(cat /proc/$pid/oom_score)"
done | sort -t= -k3 -rn
```

Read back after the first rollout, 2026-09-10. Scores move by a few points as RSS moves, so treat
them as bands; the order is what the policy fixes:

| `lotro-staging` — first victim at the top | score | | `lotro-prod` | score |
|---|---|---|---|---|
| the six application containers, both projects | 1011–1026 | | **`obs-alloy`** | **~910** |
| `lotro-staging-caddy` | ~606 | | the six application containers, both projects | 678–700 |
| `obs-grafana`, `obs-prometheus`, `obs-tempo`, `obs-loki` | 550–566 | | `lotro-prod-caddy` | ~606 |
| **`obs-alloy`** | **~511** | | | |

On prod the agent ranks above even the host's own `user@.service` (734), which only a positive value
can achieve.

**A service added to either compose file gets its `oom_score_adj` in the same commit.** CI cannot see
a missing one (`scripts/ci/classify-changes.sh` treats compose files as inert), and on the prod box
the default `0` puts the new container in the **product** tier, better protected than the agent.

A change takes **two different deploys**: `/opt/lotro` is a CD artifact (it lands with the next
`deploy.sh`), and `/opt/obs` is deployed by hand (ADR-0050 §2). A changed value recreates the
container on `up -d`, so the observability half costs a short telemetry gap.

**Do not restore symmetry between the boxes because it looks tidier.** A "consistent" order means
one of the two boxes is ordered backwards.

**What this does not cover.** `oom_score_adj` decides only the **box-level** OOM. A container that
hits its own `mem_limit` is killed by the cgroup OOM killer, where the adjustment plays no part. That
is what #782 hit, and the fleet rule watches it at 85% of each ceiling. When the policy was set
(2026-09), neither box had had a box-level OOM, so it is cheap insurance, not a fix for something seen.

## Environment variable matrix

Every deployment-relevant setting, per service, per environment: one table per service.

**`compose.hetzner.yaml` is the authoritative list** of what the deployed stack actually reads. A
variable that does not appear there does nothing, whatever this table says.

**Reading the tables:**

- **Key form.** Keys are shown in the env-var form ASP.NET Core binds (double underscore):
  `Section__Sub__Leaf`, e.g. `OpenIddict__WebClient__RedirectUris__0`. The appsettings form uses `:`
  instead (`OpenIddict:WebClient:RedirectUris:0`), and error messages use the `:` form.
- **Required?** = whether boot **fails fast** without it in that environment. `✅ all` = required in
  every environment; `✅ non-dev` = required outside Development and Testing (so Staging too);
  `optional` = safe default.
- **Source.** **secret** = lives in the box's `chmod 600` `/opt/lotro/.env`, never committed;
  **plain** = not sensitive, fine in plain app config / `appsettings.*`.
- **local-dev** = how the value is set in dev. The dev `compose.yaml` runs only infrastructure, and
  the three apps run on the **host** via `dotnet run` (ADR-0006, amended by #190), so for the apps this
  column is `appsettings.Development.json` + `launchSettings.json`.
- **Staging / Production** are the same in shape: same required set, same sources. They differ only
  in **hostnames** and **secret values**. The column shows **production** values; for staging read
  `staging.lotro-translator.pl`. The local production-parity stack (`compose.prod.yaml`) wires the
  same keys with `*.lotro.test` hostnames.

Optional tuning knobs with safe defaults are left out: `Import:*`,
`TranslationFileRebuild:DebounceWindow` = 2 s (ADR-0021), `Email:TimeoutSeconds` = 10 /
`Email:MaxSendAttempts` = 3, `Gdpr:DeletionGracePeriod` = 14 days /
`Gdpr:DeletionFinalizationPollInterval` = 1 day, `RabbitMq:Port` = 5672, `RabbitMq:VirtualHost` = `/`,
`AllowedHosts` = `*`. The token lifetimes are listed below even though they have defaults, because
they are security parameters, not tuning knobs.

The telemetry variables are covered in [Observability & monitoring](#observability--monitoring). The
tables list only the export endpoint. One of the others is not a tuning knob:
`OTEL_DEPLOYMENT_ENVIRONMENT` is the only thing that separates staging from prod in Grafana, because
both boxes run `ASPNETCORE_ENVIRONMENT=Production`.

> ⚠️ **The live prod domain is `lotro-translator.pl`**: auth → `https://auth.lotro-translator.pl`,
> tms → `https://tms.lotro-translator.pl`, frontend → `https://lotro-translator.pl`. The three
> hostnames are Caddy vhosts on the prod box; staging has the same trio under
> `*.staging.lotro-translator.pl`.

### auth-api

| Variable | local-dev | Staging / Production | Required? | Source | Notes |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` | ✅ all | plain | Gates fail-fast validation, throwaway-vs-real keys, the CORS policy. |
| `ASPNETCORE_URLS` | `https://localhost:5003` (launchSettings) | `http://+:8080` | ✅ all | plain | Host dev Kestrel (native dev cert); deployed it serves HTTP only (Caddy owns TLS). |
| `ConnectionStrings__AuthDatabase` | `…;Database=lotro_auth;…;Password=changeme` (appsettings.Development) | Neon: `Host=…;Database=lotro_auth;Username=…;Password=…;Ssl Mode=Require;Timeout=20` | ✅ all | **secret** | Carries the DB password. **Keyword format only**: Npgsql does not parse `postgres://` URIs. `Timeout` is the limit for opening a connection. The auth API caps it at 20 s in code (#923), so an older box's `Timeout=60` still works there; only the migrator reads the value as written. Wake times: Gotchas → *First cold hit*. |
| `OpenIddict__Issuer` | `https://localhost:5003` | `https://auth.lotro-translator.pl` | ✅ non-dev | plain | **The token `iss`.** Absolute http(s), no `localhost`, no user name or password (#1108). Must equal tms `Auth__Issuer`. |
| `OpenIddict__SigningKey__RsaPrivateKeyXml` | — (throwaway) | base64 of RSA XML (≥2048-bit) | ✅ non-dev | **secret** | Generate with `gen-openiddict-keys`. |
| `OpenIddict__AccessTokenLifetimeMinutes` | `5` (appsettings) | `5` (appsettings) | optional | plain | **A security parameter.** The TMS validates JWTs locally with no clock skew, so this is the longest time a revoked session keeps working after a password change, an e-mail-change undo or a scheduled deletion (#933). Raising it lengthens that window everywhere: read ADR-0049 first. The service refuses to start below `2` in every environment, because a client that renews a token a minute before it runs out would renew on every page (#1025, ADR-0049 amendment of 2026-10-07). `compose.hetzner.yaml` does not pass it, so on the boxes it changes only with a code change. |
| `OpenIddict__RefreshTokenLifetimeMinutes` | `540` (appsettings) | `540` (appsettings) | optional | plain | **Tied to the frontend cookie.** The frontend keeps the refresh token in its session cookie, which ends after 8 idle hours, so a longer lifetime only keeps a token valid that the website has already lost. Never lower it below 8 hours plus a margin: a token that dies before its cookie shows "session expired" to a user who was idle for just under 8 hours. Every refresh issues a new token with the full lifetime, so a user who keeps working is never signed out by it (#1014). `compose.hetzner.yaml` does not pass it, so on the boxes it changes only with a code change. |
| `OpenIddict__EncryptionKey__Key` | — (throwaway) | base64 of a ≥32-byte key | ✅ non-dev | **secret** | Generate with `gen-openiddict-keys`. |
| `OpenIddict__ApiClientSecret` | `dev-api-secret-min-32-characters-long` | ≥32-char random secret | ✅ non-dev | **secret** | Generate with `gen-openiddict-keys`. **Seeds a DB row**: rotating it needs the [reseed](#reseed-traps--the-auth-seeder-is-create-if-missing). Must equal that environment's `SMOKE_CLIENT_SECRET`. |
| `OpenIddict__WebClient__RedirectUris__0` | `https://localhost:7017/callback` | `https://lotro-translator.pl/callback` | ✅ non-dev | plain | Must equal the Frontend callback (its public origin + `AuthSystem__CallbackPath`). No user name or password (#1108). Written to the DB **only when the client is created**: see the reseed traps. |
| `OpenIddict__WebClient__PostLogoutRedirectUris__0` | `https://localhost:7017` | `https://lotro-translator.pl` | ✅ non-dev | plain | Frontend post-logout return URL. The first entry is also the website root that the account pages and e-mails link to. No user name or password (#1108). |
| `Cors__AllowedOrigins__0` | — (AllowAnyOrigin) | `https://lotro-translator.pl` | ✅ non-dev | plain | Bare origin = Frontend public URL. Lowercase, no default port, no path or slash. |
| `ForwardedHeaders__KnownNetworks__0` | — (dev skips `UseForwardedHeaders`) | `10.60.0.100/32` (Caddy's pinned static IP) | optional | plain | Trusts `X-Forwarded-*` only from Caddy's exact address (#399, narrowed to a /32 by #506). Keep in step with Caddy's `ipv4_address` on the default network in `compose.hetzner.yaml`. A malformed CIDR aborts boot. |
| `FrontendCaller__Key` | — (the limiter is off) | from `FRONTEND_CALLER_KEY` | ✅ non-dev | **secret** | The frontend's caller key (ADR-0054): a frontend call that carries it next to the visitor's address is rate-limited on that visitor, not on the frontend container. ≥ 32 chars, printable ASCII only (no line break, tab or non-ASCII character), no whitespace at either end (write it unquoted); boot fails without it outside Development/Testing. **The same variable feeds tms-api's `FrontendCaller__Key` and the frontend's `AuthSystem__CallerKey` + `TranslationSystem__CallerKey`.** |
| `HealthCheck__Key` | — (the full `/health` is open) | from `HEALTH_CHECK_KEY` | ✅ non-dev | **secret** | Opens the full `/health` (ADR-0058): the database, SMTP and broker checks run only for a request that sends this value in `X-LOTRO-Health-Key`; anyone else gets 404. Same format rules as `FrontendCaller__Key`; boot fails without it outside Development/Testing. **The same variable feeds tms-api's `HealthCheck__Key`.** |
| `DataProtection__KeyRingPath` | — (host default) | `/keys` | ✅ non-dev | plain | Persistent volume (`auth-keys`); without it logins, antiforgery and reset links break on every deploy. |
| `Email__Host` | `localhost` (the compose mailpit, published on `:1025`) | `smtp-relay.brevo.com` | ✅ all | plain | SMTP host. Validated at start in every environment. |
| `Email__Port` | `1025` | `587` | ✅ all | plain | 1–65535. |
| `Email__Mode` | `None` | `StartTls` | ✅ all | plain | One of `None` / `StartTls` / `SslOnConnect`. |
| `Email__SenderEmail` | `noreply@lotro-translator.pl` | a Brevo-**authorised** sender (today `koniecdev@gmail.com`) | ✅ all | plain | ⚠️ Not a free-text label: an unauthorised sender is accepted by the relay and **silently dropped** by the receiver. See [E-mail deliverability](#e-mail-deliverability--the-sender-must-be-one-brevo-is-authorised-to-send-as). |
| `Email__Sender` | `lotro-translator.pl` | `LOTRO PL` | ✅ all | plain | Display name. |
| `Email__Username` / `Email__Password` | — | Brevo SMTP login + key | optional¹ | **secret** (Password) | ¹If `Username` is set, `Password` is required. The login is shaped `<id>@smtp-brevo.com`, **not** the Brevo account e-mail (that fails with `535`). |
| `RabbitMq__Host` | `localhost` (the compose broker, published on `:5672`) | `rabbitmq` (the in-stack broker service) | ✅ all | plain | Validated at start in every environment, but auth-api **boots and serves with the broker down**: outbox rows wait, the consumer retries. See [Message broker](#message-broker-rabbitmq). |
| `RabbitMq__Username` | `rabbitmq` | `rabbitmq` | ✅ all | plain | Matches the `RABBITMQ_DEFAULT_USER` literal in every compose file. |
| `RabbitMq__Password` | `changeme` (appsettings.Development + the dev compose default) | from `RABBITMQ_PASSWORD` | ✅ all | **secret** | ⚠️ The broker applies `RABBITMQ_DEFAULT_PASS` on **first boot only**: rotation is a lockstep change, see the [secrets table](#secret-material--source-of-truth-and-how-to-rotate). |
| `AdminUser__Username` / `AdminUser__Email` | `appsettings.Local.json` (git-ignored; copy its `.example`) | from `AUTH_ADMIN_USERNAME` / `AUTH_ADMIN_EMAIL` | optional | plain | Seeds one admin **only when missing**, **without a password**; the operator sets it through the reset mail ([Admin account](#admin-account--first-sign-in-and-rotation), ADR-0056). Leave the e-mail blank to skip. Username must match `^[a-zA-Z0-9]+$` (ADR-0022) or auth-api fails at startup; the admin logs in **by e-mail**. |
| `AdminUser__Password` | `appsettings.Local.json` | — (**never set**) | optional | plain (a dev value) | Read **only** in Development and Testing, so the host dev loop and the test suites keep a known admin login. Every other environment ignores it and logs warning `2352`. Neither compose file passes it, on purpose. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://localhost:4317` (launchSettings → the compose aspire-dashboard) | `http://10.60.0.1:4317` (the box's Alloy agent) | optional | plain | Empty = no exporter at all. The protocol is fixed to `grpc` in `compose.hetzner.yaml`. See [Observability](#observability--monitoring). |

### tms-api

| Variable | local-dev | Staging / Production | Required? | Source | Notes |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` | ✅ all | plain | Gates fail-fast validation + the CORS policy. |
| `ASPNETCORE_URLS` | `https://localhost:5002` (launchSettings) | `http://+:8080` | ✅ all | plain | Host dev Kestrel (native dev cert); deployed it serves HTTP only (Caddy owns TLS). |
| `ConnectionStrings__TranslationDatabase` | `…;Database=lotro_translation;…;Password=changeme` (appsettings.Development) | Neon: `Host=…;Database=lotro_translation;Username=…;Password=…;Ssl Mode=Require;Timeout=60` | ✅ all | **secret** | TMS write context. Same Neon format rules as the auth string. Here `Timeout=60` is the TMS API's own limit for opening a connection. |
| `Auth__Issuer` | `https://localhost:5003` | `https://auth.lotro-translator.pl` | ✅ all | plain | Must equal auth `OpenIddict__Issuer` (the token `iss`); tokens are rejected otherwise. No user name or password, in every environment (#1156). |
| `Auth__Authority` | — (unset → falls back to `Issuer`) | `https://auth.lotro-translator.pl` | optional | plain | Back-channel for OIDC metadata + JWKS. The dev host run relies on the fallback to `Issuer`. Deployed: must be `https` (OpenIddict rejects plain HTTP in Production) and reachable from the container, i.e. the **public Caddy origin**, never `http://auth-api:8080`. No user name or password, in every environment (#1156). |
| `Auth__Audience` | `lotrokoniecdev-api` | `lotrokoniecdev-api` | ✅ all | plain | Default in base `appsettings.json`. |
| `Cors__AllowedOrigins__0` | — (AllowAnyOrigin) | `https://lotro-translator.pl` | ✅ non-dev | plain | Bare origin = Frontend public URL. |
| `ForwardedHeaders__KnownNetworks__0` | — (dev skips `UseForwardedHeaders`) | `10.60.0.100/32` (Caddy's pinned static IP) | optional | plain | See the auth-api row. |
| `FrontendCaller__Key` | — (the limiter is off) | from `FRONTEND_CALLER_KEY` | ✅ non-dev | **secret** | The same key and rules as auth-api's `FrontendCaller__Key` (ADR-0054, #823). |
| `HealthCheck__Key` | — (the full `/health` is open) | from `HEALTH_CHECK_KEY` | ✅ non-dev | **secret** | The same key and rules as auth-api's `HealthCheck__Key` (ADR-0058); here the full `/health` runs the database check. |
| `RabbitMq__Host` / `RabbitMq__Username` / `RabbitMq__Password` | `localhost` / `rabbitmq` / `changeme` (appsettings.Development) | `rabbitmq` / `rabbitmq` / from `RABBITMQ_PASSWORD` | ✅ all | **secret** (Password) | The broker and the user auth-api uses: tms-api consumes the `AccountErased` events of ADR-0065 from its own queue `tms.account-erased`. Validated at start, but tms-api **boots and serves with the broker down**: the consumer retries its connection, and the AuthSystem's outbox keeps the event until it is delivered. |
| `TranslationFileDiskCache__Directory` | — (unset → a private folder per process in the temp folder) | `/tmp/lotro-translation-files` (set in `compose.hetzner.yaml`) | optional | plain | Where full downloads of the translation file are served from (ADR-0064). Fixed in the containers, so a crash restart reuses and sweeps the one copy (about 82 MB at full size) instead of leaving one behind. It must belong to this one process; an empty value stops the boot. |
| `TranslationSaveBudget__SaveLimit` | — (default `1000`) | from `TMS_SAVE_LIMIT`, default `1000` | optional | plain | Saves one non-admin account may make in the window (ADR-0072, #687). Past it the save is refused with 429. At least 1, or tms-api stops at boot. |
| `TranslationSaveBudget__SaveAlertThreshold` | — (default `500`) | from `TMS_SAVE_ALERT_THRESHOLD`, default `500` | optional | plain | From this save on, every save logs the Warning that the `lotro-translation-save-budget` alert mails. Between 1 and `SaveLimit`. |
| `TranslationSaveBudget__OverwriteLimit` | — (default `100`) | from `TMS_OVERWRITE_LIMIT`, default `100` | optional | plain | Saves on a row that already has Polish, counted inside the save budget. Past it only such saves are refused; rows with no Polish still save. Raise it for the days after a large game update, when translators correct many rows. At least 1. |
| `TranslationSaveBudget__OverwriteAlertThreshold` | — (default `50`) | from `TMS_OVERWRITE_ALERT_THRESHOLD`, default `50` | optional | plain | The overwrite Warning, as for saves. Between 1 and `OverwriteLimit`. |
| `TranslationSaveBudget__WindowHours` | — (default `24`) | from `TMS_SAVE_BUDGET_WINDOW_HOURS`, default `24` | optional | plain | The rolling window both budgets count in. 1 to 720 (30 days, the privacy policy's limit for technical data). The counts are kept in the `TranslationSaves` table, so a deploy or a restart does not reset them. Rows older than the window are deleted on the next save by a non-admin. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://localhost:4317` (launchSettings) | `http://10.60.0.1:4317` | optional | plain | See the auth-api row. |

### frontend

Runs on the **host** in dev (`dotnet run`, ADR-0006), so its dev column is
`appsettings.Development.json` + `launchSettings.json`; it is containerized only in Staging and
Production.

| Variable | local-dev | Staging / Production | Required? | Source | Notes |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` | ✅ all | plain | Gates the DP keyring guard. |
| `ASPNETCORE_URLS` | `https://localhost:7017` (launchSettings) | `http://+:8080` | ✅ all | plain | Deployed it serves HTTP only (Caddy owns TLS). |
| `AuthSystem__Authority` | `https://localhost:5003` | `https://auth.lotro-translator.pl` | ✅ all | plain | Drives the `/authorize` redirect (front-channel) **and** discovery/token (back-channel). |
| `AuthSystem__BaseUrl` | `https://localhost:5003/` | `https://auth.lotro-translator.pl/` | ✅ all | plain | Auth origin (trailing slash). Must be the same origin as auth's `OpenIddict__Issuer`: see [consistency rule 2](#consistency-rules-that-bite). |
| `AuthSystem__ClientId` | `lotrokoniecdev-web` | `lotrokoniecdev-web` | ✅ all | plain | Must match the OpenIddict web-client id. |
| `AuthSystem__CallbackPath` | `/callback` | `/callback` | ✅ all | plain | Origin + this must be registered in auth `RedirectUris`. |
| `AuthSystem__SignedOutCallbackPath` | `/signout-callback-oidc` | `/signout-callback-oidc` | ✅ all | plain | Origin + this must be in auth `PostLogoutRedirectUris`. |
| `AuthSystem__Scopes` | `openid,email,profile,roles,api,offline_access` | same | ✅ all | plain | At least one scope. |
| `TranslationSystem__BaseUrl` | `https://localhost:5002/` | `https://tms.lotro-translator.pl/` | ✅ all | plain | TMS API origin (trailing slash). |
| `DataProtection__KeyRingPath` | — (host default) | `/keys` | ✅ non-dev | plain | Persistent volume (`frontend-keys`, ADR-0005); without it antiforgery and auth cookies break on every deploy. |
| `AuthSystem__CallerKey` | — (the auth limiter is off) | from `FRONTEND_CALLER_KEY` | ✅ non-dev | **secret** | Sent with the visitor's address on every auth API call (ADR-0054). Must equal auth-api's `FrontendCaller__Key`; compose reads both from the one variable. Same format rules as there. |
| `TranslationSystem__CallerKey` | — (the TMS limiter is off) | from `FRONTEND_CALLER_KEY` | ✅ non-dev | **secret** | Sent with the visitor's address on every TMS API call (ADR-0054, #823). Must equal tms-api's `FrontendCaller__Key`: the same one variable again. |
| `ForwardedHeaders__KnownNetworks__0` | — (dev skips `UseForwardedHeaders`) | `10.60.0.100/32` (Caddy's pinned static IP) | optional | plain | See the auth-api row. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://localhost:4317` (launchSettings) | `http://10.60.0.1:4317` | optional | plain | See the auth-api row. |

### migrator

A one-shot job that reads only the two connection strings (see
[Database migrations](#database-migrations)):

| Variable | local-dev | Staging / Production | Required? | Source | Notes |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` | ✅ all | plain | |
| `ConnectionStrings__TranslationDatabase` | from `POSTGRES_PASSWORD` | the Neon TMS string | ✅ all | **secret** | TMS write context (`lotro_translation`). |
| `ConnectionStrings__AuthDatabase` | from `POSTGRES_PASSWORD` | the Neon Auth string | ✅ all | **secret** | Auth context (`lotro_auth`). |

### Box-identity variables (not secrets)

These decide **which environment a box is**. The full list with placeholders is in
`.env.hetzner.example`:

- `COMPOSE_PROJECT_NAME` (`lotro-prod` | `lotro-staging`), `IMAGE_NAMESPACE`, `IMAGE_TAG`,
  `DOMAIN_APP` / `DOMAIN_AUTH` / `DOMAIN_TMS`, `ACME_EMAIL`.
- `TKS_DOMAIN_*`: the guest TheKittySaver vhosts.
- `DOMAIN_OBS` / `OBS_ALLOWED_PUSH_IP` / `OBS_PUSH_USER`: the observability ingest vhost, on the
  **backend box only**. The other box keeps the inert compose defaults (#755).
- `XROBOTS`: Caddy stamps it as `X-Robots-Tag` on every LOTRO vhost response (#531). Prod leaves it
  unset (→ `all`, a no-op); the **staging box sets `XROBOTS="noindex, nofollow"`**, so the public
  staging trio is never indexed as a copy of prod. The TKS vhosts are left out, because TKS sets the
  header in its own app (koniecdev/TheKittySaver#390).
- `OOM_SCORE_ADJ_APP`: **opposite on the two boxes**, `500` on staging and `0` on prod (see
  [Memory pressure](#memory-pressure-who-the-kernel-kills-first)). The observability project's
  `/opt/obs/.env` has `OBS_ALLOY_OOM_SCORE_ADJ`, which inverts the same way (`-300` staging, `300` prod).

## Secrets

### Where they live

The live values sit in **`/opt/lotro/.env`** on each box (`chmod 600`, owner `deploy`): **one file
per box**, because one box = one environment. Compose reads it automatically, so no `--env-file`.
The tracked template with every key and placeholder values is **`.env.hetzner.example`**.

**A new required variable lands on a box before the change that needs it.** Put it into the staging
`.env` before the merge, and into the prod `.env` before the promotion. Compose refuses to render
without a required variable (`${VAR:?}`), so `deploy.sh` stops at its first gate and the old release
keeps serving, but the deploy is red until the value is there.

Two pieces of key material live **outside** `.env` and outside the DB: the `auth-keys` and
`frontend-keys` **Data Protection keyring volumes**. Nothing mints them. Losing a volume invalidates
every auth cookie and OIDC correlation state (everyone signs in again), which is why they are named
volumes and not bind mounts.

### Secret material — source of truth and how to rotate

| Variable | Read by | Source of truth | (Re)mint / rotate |
|---|---|---|---|
| `ConnectionStrings__AuthDatabase` | migrator, auth-api | **Neon**: the environment's project (prod: `lotro-translator-prod`; staging has its own, ADR-0018), DB `lotro_auth` | Neon console → Roles → *Reset password* → rebuild the string by hand, in the format the [auth-api matrix](#auth-api) shows (keyword form, `Ssl Mode=Require`, `Timeout=20`). |
| `ConnectionStrings__TranslationDatabase` | migrator, tms-api | same Neon project, DB `lotro_translation` | same, in the format the [tms-api matrix](#tms-api) shows (`Timeout=60`) |
| `OpenIddict__SigningKey__RsaPrivateKeyXml` | auth-api | **regenerate**: no copy exists anywhere else | Take the line from [`gen-openiddict-keys`](#openiddict-production-keys-auth-api) into the box `.env`. Pure config (never stored in the DB), so a rotation invalidates issued tokens and everyone signs in again. auth-api can also read `OpenIddict__SigningKey__PreviousRsaPrivateKeyXml` to keep old tokens valid during a rotation (`OpenIddictSettings`), but `compose.hetzner.yaml` does not pass it. |
| `OpenIddict__EncryptionKey__Key` | auth-api | regenerate | same script, same effect. |
| `OpenIddict__ApiClientSecret` | auth-api (**seeds** the `lotrokoniecdev-api` client row) | regenerate | same script, **but the DB row wins on restart: a rotation needs the [reseed](#reseed-traps--the-auth-seeder-is-create-if-missing).** Must stay equal to the `SMOKE_CLIENT_SECRET` GitHub secret of the same environment. |
| `Email__Username` | auth-api | **Brevo** dashboard → SMTP & API → SMTP keys | The SMTP **login**, shaped `<id>@smtp-brevo.com`, **not** the Brevo account e-mail, which fails the handshake with `535`. |
| `Email__Password` | auth-api | **Brevo** (the owner pastes it) | Generate a new SMTP key in Brevo. Brevo shows it **once**, so a lost key is generated again, never recovered. |
| The admin password | the admin `Users` row | **the admin's own password manager**, never a file on the box | Not a `.env` value (ADR-0056): the seeder creates the admin without one. The first password and every rotation go through the reset mail ([Admin account](#admin-account--first-sign-in-and-rotation)). A box `.env` that still has an `AUTH_ADMIN_PASSWORD` line predates #696: follow the migration steps there. |
| `RABBITMQ_PASSWORD` (→ auth-api and tms-api `RabbitMq__Password` **and** the broker's `RABBITMQ_DEFAULT_PASS`) | rabbitmq, auth-api, tms-api | **box-local**: `openssl rand -base64 24` | ⚠️ The broker applies `RABBITMQ_DEFAULT_PASS` on **first boot only** (empty data volume), so editing `.env` later changes what the apps send but **not** what the broker expects. Rotate in lockstep: `docker compose -f compose.hetzner.yaml exec --user rabbitmq rabbitmq rabbitmqctl change_password rabbitmq '<new>'` → update `.env` → `docker compose -f compose.hetzner.yaml up -d auth-api tms-api`. Leave tms-api out and it keeps working until its next reconnect, then is refused for good while erasures wait unseen in its queue (ADR-0065). |
| `FRONTEND_CALLER_KEY` (→ auth-api and tms-api `FrontendCaller__Key`, frontend `AuthSystem__CallerKey` **and** `TranslationSystem__CallerKey`) | auth-api, tms-api, frontend | **box-local**: `openssl rand -base64 32`, one per environment (not one per API) | Lets both APIs rate-limit a frontend call on the visitor's forwarded address instead of on the frontend container (ADR-0054). Put a new value into the box `.env` and redeploy: all three services read the same line, so they disagree only during the restart, and a call in that window only counts against the frontend's own bucket. **Required**: compose refuses to render without it, and all three apps refuse to boot outside Development/Testing. Write it **unquoted**. A key with whitespace at either end, a line break or a non-ASCII character (a pasted non-breaking space) stops all three apps at boot, because such a key cannot match the header the APIs compare it with; a tab is refused too, to keep one simple rule (#857, #877). A leaked key lets its holder dodge the three auth back-channel limits and the TMS API's one limit by inventing addresses, and nothing more: rotate it. |
| `HEALTH_CHECK_KEY` (→ auth-api and tms-api `HealthCheck__Key`) | auth-api, tms-api, **and on the prod box the daily health ping** | **box-local**: `openssl rand -base64 32`, one per environment (not one per API) | Opens the full `/health` (ADR-0058); without it both APIs answer 404 there and run no check. **Required**, with the same format rules as `FRONTEND_CALLER_KEY`. **Prod only: the same value is the `PROD_HEALTH_CHECK_KEY` repository secret** the daily health ping sends. Set or rotate it without the value reaching argv or the screen: `ssh deploy@<prod-box> "grep '^HEALTH_CHECK_KEY=' /opt/lotro/.env \| cut -d= -f2-" \| tr -d '"\r\n' \| gh secret set PROD_HEALTH_CHECK_KEY` (gh reads stdin when there is no `--body`; **never `--body -`**). The command copies the raw `.env` line, so write the value **unquoted**: `tr` drops double quotes, but single quotes would reach the secret and turn the ping red. Rotate: new value into the prod `.env` → redeploy → that command. Between the two steps the ping gets 404 and goes red, so rotate outside its 06:40 UTC slot. A leaked key lets its holder run the database, SMTP and broker checks at will, and nothing more: rotate it. |
| `OBS_PUSH_PASSWORD` (prod `/opt/obs/.env`) ↔ `OBS_PUSH_PASSWORD_HASH` (staging `/opt/lotro/.env`) | the prod agent (Alloy, `ship.agent.alloy`) ↔ the staging Caddy's ingest vhost | **box-local**: `openssl rand -base64 24`; hash it with `docker run --rm -it caddy:2-alpine caddy hash-password` (it prompts, never on argv) | One password in two forms; the plaintext exists on the prod box only. Rotate hash first: new hash into the staging `.env` → `deploy.sh` (reloads Caddy) → new password into the prod `.env` → `docker compose -f compose.observability.yaml up -d` in `/opt/obs`. **Telemetry sent in between is lost, not delayed**: all three writers treat a 401 as permanent and drop the batch. So pick a window you accept a hole in, or give the vhost a second `basic_auth` account first so the two credentials overlap. **Single-quote the hash in `.env`**: see [Bringing up the prod agent](#bringing-up-the-prod-agent-the-agent-only-role) step 3, which also has the probe that proves it landed. |
| `BACKUP_ENCRYPTION_KEY` (prod box only) | `backup.sh` | **the owner's password manager** first, then the prod `.env`: `openssl rand -base64 32` | The one value here that cannot be minted again: without it every dump in the bucket is unreadable ([Database backups](#database-backups)). Rotate: put the new key into the password manager **next to** the old one, then into `.env`. Keep the old key for 30 days, until the lifecycle rule has deleted the last dump encrypted with it. |
| `BACKUP_R2_ACCESS_KEY_ID` / `BACKUP_R2_SECRET_ACCESS_KEY` (prod box only; `BACKUP_R2_ACCOUNT_ID` and `BACKUP_R2_BUCKET` beside them are not secret) | `backup.sh` | **Cloudflare** → R2 → Manage API tokens: the Account API token `lotro-prod-backup`, Object Read & Write on `lotro-backups-prod` only | Cloudflare shows the secret **once**. Rotate: create a token with the same settings → replace both lines in `.env` → `bash /opt/lotro/backup.sh` must end with "every database landed" → delete the old token. The bucket lock rule stops any token from overwriting or deleting a dump younger than 29 days, so a leaked token can read encrypted dumps and add objects, but cannot wipe the backups. |
| `SMOKE_CLIENT_SECRET` *(GitHub secret, per environment — **not** a box variable)* | `scripts/smoke.sh`, CD | == `OpenIddict__ApiClientSecret` of that environment | `gh secret set SMOKE_CLIENT_SECRET --env <staging\|production> --body "$VALUE"`. **Never `--body -`**: gh takes `-` literally and the smoke then gets 401. |
| `HETZNER_SSH_KEY` *(GitHub secret, per environment — **not** a box variable)* | CD over ssh (`deploy.yml`) | generated for CD, one key per box | The `deploy` user's key, never `root`'s. Mint + install + pin the host key: [One-time setup per environment](#one-time-setup-per-environment). |

### OpenIddict production keys (auth-api)

`scripts/gen-openiddict-keys.sh` (PowerShell: `scripts/gen-openiddict-keys.ps1`) prints a comment
line and five `KEY=VALUE` lines:

```
# Generated by scripts/gen-openiddict-keys.sh — keep secret, never commit.
OpenIddict__EncryptionKey__Key=<base64 of a 32-byte key>
OpenIddict__ApiClientSecret=<48 hex chars>
OpenIddict__SigningKey__RsaPrivateKeyXml=<base64 of RSA.ToXmlString(true), 2048-bit>
FRONTEND_CALLER_KEY=<base64 of 32 random bytes>
HEALTH_CHECK_KEY=<base64 of 32 random bytes>
```

`scripts/up-prod.sh` uses it to fill a new `.env.prod`. For a box `.env`, put each value into its
empty line from `.env.hetzner.example`. **For a rotation, copy only the line you rotate.** Never
append the whole output to a live box `.env`: it would also change the API client secret, the caller
key and the health check key, and each of those has its own rotation steps in the table above.

Manual equivalents for the two simple ones (if you cannot run the script):

```bash
openssl rand -base64 32        # OpenIddict__EncryptionKey__Key  (256-bit symmetric key)
openssl rand -hex 24           # OpenIddict__ApiClientSecret     (48 chars, ≥ the 32 minimum)
```

`RsaPrivateKeyXml` is base64 of .NET's `RSA.ToXmlString(true)` for a ≥2048-bit key. The script does
the PEM → .NET XML conversion (it needs `openssl` + `python3`); use the script rather than doing it
by hand.

### E-mail deliverability — the sender must be one Brevo is authorised to send as

`Email__SenderEmail` is not a free-text label. It must be either Brevo's verified **single sender**
(today: `koniecdev@gmail.com`) or an address on a domain **authenticated in Brevo**, i.e. one whose
DKIM + SPF records are published in DNS. Any other value fails in a way no alarm can see:

1. Brevo's relay **accepts** the message, so `IEmailService.SendAsync` returns success.
2. The deep auth `/health` SMTP check stays **green**: it only proves connect + authenticate.
3. The receiver (Gmail, etc.) sees mail that claims a domain with no SPF and no aligned DKIM, and
   **silently drops it**. It does not even reach spam.
4. `RegisterUser` auto-confirms a user only when the send **fails** (`RegisterUser.cs`, the
   `emailResult.IsFailure` branch). A send that "succeeded" skips that net, so every new account is
   stuck at *"potwierdź adres e-mail"* and **nobody can log in**.

That is how both prods shipped with `no-reply@lotro-translator.pl` during the Hetzner cutover (#491):
`lotro-translator.pl` has **no SPF, DKIM or DMARC records at all**.

**The only check that works is an end-to-end one**: register a real account and read a real inbox.
`smoke.sh` cannot cover this, and neither can a health probe:

```bash
dig +short TXT lotro-translator.pl            # SPF/DKIM/DMARC present at all? (today: nothing)
# then actually register on the env and confirm the mail lands (Gmail plus-addressing works:
# koniecdev+check@gmail.com), because only delivery proves delivery.
```

To move off the personal Gmail sender, authenticate the domain in Brevo (Senders → Domains → add
`lotro-translator.pl`, publish the DKIM + SPF records it prints, add a DMARC record), *then* switch
`Email__SenderEmail` to `no-reply@lotro-translator.pl`, and run the registration check again.

### Reseed traps — the auth seeder is create-if-missing

`SeedAuthDatabaseAsync` (`AuthSystem.API/Extensions/DatabaseSeederExtensions.cs`) creates only what
is missing. The admin user is skipped when its e-mail **or** username already exists (the seeder
never gives the Admin role to an existing account at the admin e-mail), and each OpenIddict client is
skipped when its `client_id` already exists. The Neon DBs outlive any box, so this is the normal
case, and it means **editing `.env` and restarting silently changes nothing**:

| You changed in `.env` | What silently keeps the OLD value | Symptom |
|---|---|---|
| `OpenIddict__ApiClientSecret` | the `lotrokoniecdev-api` client row | `client_credentials` with the new secret → **401**; smoke's token leg fails |
| `AUTH_ADMIN_EMAIL` / `AUTH_ADMIN_USERNAME` | the admin `Users` row | a new address with the same username is skipped, with warning `2353` in the auth-api log; a new address that already belongs to a non-admin account is skipped, with warning `2354` (that row may be a stranger's account: read "Warning `2354`" under [Admin account](#admin-account--first-sign-in-and-rotation) before you delete anything); a new address that an account left less than 14 days ago, and can still undo back to, is skipped, with warning `2355`; a new address **and** a new username seed a **second** admin, and the first one keeps its role |
| `DOMAIN_APP` | the `lotrokoniecdev-web` client's redirect + post-logout URIs (written **only at creation**) | login bounces with `invalid_redirect_uri` |

Fix = delete the rows and let the seeder rebuild them from the current `.env`. The schema is
**`authsystem`**, and the Identity tables are **renamed** (`authsystem."Users"`, *not* `AspNetUsers`).
Delete in FK order:

```sql
DELETE FROM authsystem."OpenIddictTokens";
DELETE FROM authsystem."OpenIddictAuthorizations";
DELETE FROM authsystem."OpenIddictApplications";
-- only when replacing the admin account itself, e.g. after a typo in AUTH_ADMIN_EMAIL
-- (UserRoles cascades with the user). A password rotation never needs this: use the reset mail.
-- Never for warning 2354: that row is not the admin (see its section).
DELETE FROM authsystem."Users" WHERE "NormalizedEmail" = upper('<admin e-mail>');
```

```bash
docker compose -f compose.hetzner.yaml restart auth-api   # seeder runs at startup, recreates the rows
```

This signs out every user (their tokens are gone): free before launch, a real outage after it.

### Admin account — first sign-in and rotation

The `Admin` role reaches the whole distribution path to players: imports, approvals and game
versions, among others. No page in the product grants that role: the seeder is the only place it
comes from. Set these values in the box `.env` to seed the admin on the next auth-api start; leave
the e-mail blank to skip:

```
AUTH_ADMIN_USERNAME=…        # letters + digits only (^[a-zA-Z0-9]+$) — ADR-0022; blank = "admin"
AUTH_ADMIN_EMAIL=…           # a mailbox the operator reads: the first password arrives there
```

The username is a display-only handle; the admin **logs in by e-mail + password**. A username with
`-`, `.`, `_`, spaces or diacritics fails Identity's `AllowedUserNameCharacters` and stops auth-api
at startup (loud, by design).

**There is no admin password in the `.env` (ADR-0056).** The seeder creates the admin without a
password, so nobody can sign in to it until someone opens the reset mail. auth-api logs event `2351`
("Seeded the admin account … without a password") when it does this. The ADR says what this does and
does not protect: the `.env` still holds the OpenIddict signing key and the Neon connection strings,
and anyone who can read that file is already stronger than any admin password.

**Before the first sign-in, prove that mail works.** The admin gets its password through the same
mail path as every translator's confirmation: outbox → RabbitMQ → Brevo. If registration mail does
not arrive on this box, the admin mail will not arrive either, so fix that first
([E-mail deliverability](#e-mail-deliverability--the-sender-must-be-one-brevo-is-authorised-to-send-as),
[the dead-letter parking lot](#the-dead-letter-parking-lot-emailssenddlq)). There is no second channel.

**First sign-in, and every rotation after it** (the same four steps):

1. Open `https://auth.<env domain>/Account/ForgotPassword`, or the login page's link
   **"Nie pamiętam hasła →"**.
2. Type the admin e-mail **exactly**, with no trailing space. The page does not trim the address, so
   a pasted space shows the normal "Jeśli konto istnieje…" panel and sends nothing.
3. Open the mail, follow the link and set a long random password from a password manager. Never
   write it into the `.env`, a ticket, the wiki or a public channel. The prod admin must never use
   the same password as staging. Nothing in the code checks that, so it is up to you.
4. Sign in with it. A reset also revokes every session the admin had (ADR-0049), which is the point
   of a rotation. An access token that was already issued keeps working until it expires, which takes
   at most five minutes.

If the sign-in right after a successful reset shows "Nieprawidłowy e-mail lub hasło.", the account
may be **locked**. Five wrong passwords lock it for 5 minutes, and anyone who knows the admin address
can cause that, even before the first reset. A reset does not clear the lock. Wait 5 minutes and try
again before you start another rotation.

Two limits apply to the reset page, and they look different:

- **per account:** 3 mails per 15 minutes. A fourth request shows the normal panel and sends
  nothing;
- **per address (IP):** 3 form posts per 15 minutes. A fourth post from the same machine gets the
  **"Za dużo prób"** page (HTTP 429), which names the longest wait. The limit may be free again
  sooner.

Wait out that window before you blame delivery.

**Migrating a box seeded before #696.** Its admin row still has the password that was in
`AUTH_ADMIN_PASSWORD`, and the seeder does not remove it, because it skips an admin that already
exists. Rotate first, then remove the line, in this order:

1. Do the four steps above for the box's admin. The old password stops working the moment the reset
   succeeds. Check it: sign in with the old one and get the generic error.
2. Delete the line as the `deploy` user, so the file stays owned by `deploy` and CD can still read it
   (never edit it as root). The second command must print `0`. No restart is needed: neither compose
   file reads the line.

   ```bash
   ssh lotro-<env> "sudo -u deploy sed -i '/^AUTH_ADMIN_PASSWORD=/d' /opt/lotro/.env"
   ssh lotro-<env> "sudo -u deploy grep -c '^AUTH_ADMIN_PASSWORD=' /opt/lotro/.env"
   ```
3. Clean up the other copies of the old value. It is a leaked password now:
   - the `ADMIN_PASSWORD` GitHub secret and the `ADMIN_EMAIL` / `ADMIN_USERNAME` variables are
     leftovers from the Azure era (ADR-0013 calls them unused). Delete them;
   - any backup of the box `.env` still holds it;
   - if the value was ever reused anywhere else, change it there too.
4. Remember that a database restore brings the old password back. A Neon restore (PITR or a
   pre-migration snapshot) to a point before the rotation restores the old hash with the rest of the
   `Users` table. After such a restore, rotate again.

**Warning `2353`: the admin username belongs to a different address.** The seeder logs it on every
start when `AUTH_ADMIN_EMAIL` matches no account but `AUTH_ADMIN_USERNAME` is taken. There are two
causes, and they need opposite fixes. Look at the row first:

```sql
-- NormalizedUserName, because the seeder's check ignores case: "Admin" blocks "admin" too.
SELECT "UserName", "Email", "PasswordHash" IS NULL AS no_password FROM authsystem."Users" WHERE "NormalizedUserName" = upper('<admin username>');
```

- **The admin changed its own address in the product.** The row has the real address and a
  password. Put the new address into `AUTH_ADMIN_EMAIL`, or ignore the warning. **Never delete this
  row.** It is the live admin.
- **A typo in `AUTH_ADMIN_EMAIL` at the first seed.** The row has the misspelled address and no
  password, because nobody could ever receive its reset mail. A real address with no password is
  **not** this case: a cancelled deletion or an undone e-mail change also clears the password. If
  you receive mail at that address, it is the live admin, so reset it instead. Delete the row only
  when the address is really misspelled (see the
  [reseed traps](#reseed-traps--the-auth-seeder-is-create-if-missing)), fix the `.env` and restart
  auth-api.

**Warning `2354`: the admin address belongs to an account without the Admin role.** The seeder logs
it on every start when `AUTH_ADMIN_EMAIL` matches an account that is not an admin, and leaves that
account as it is. It never promotes an account it did not create itself: it cannot tell your own
account from a stranger's registration at a mistyped address (#839). Look at the row first:

```sql
SELECT "Id", "UserName", "EmailConfirmed", "PasswordHash" IS NULL AS no_password, "EmailChangeRevertTo" IS NOT NULL AS undo_armed FROM authsystem."Users" WHERE "NormalizedEmail" = upper('<admin e-mail>');
```

**The address is not one you read** (a typo, or someone else's address): fix `AUTH_ADMIN_EMAIL` and
restart auth-api. **Never delete that row.** It is a real person's account, and an admin seeded at
their address would get its first password from their inbox.

**It is your address.** Give the row the role by hand **only** when it is confirmed, has no password
and has no armed undo link. That is what an admin half-made by a crash before #839 looks like: only
the admin inbox can give it a password. `EmailConfirmed` alone is **not** enough. An e-mail change
moves someone else's account onto your address, with their password, as soon as you click the
confirm link it mails you, and its undo link can later take the row back, role included (ADR-0048).
The statements below end any session still open on the row, then grant the role only when all three
conditions hold. Granting a role does not change the security stamp, so a session that survived
would get the Admin role at its next refresh or sign-in. Changing the stamp ends both the auth cookie
and every refresh token (#848); the deletes are a second layer.

```sql
UPDATE authsystem."Users" SET "SecurityStamp" = gen_random_uuid()::text
WHERE "Id" = '<Id from the SELECT>'
  AND "EmailConfirmed" AND "PasswordHash" IS NULL AND "EmailChangeRevertTo" IS NULL;
DELETE FROM authsystem."OpenIddictTokens" WHERE "Subject" = '<Id from the SELECT>';
DELETE FROM authsystem."OpenIddictAuthorizations" WHERE "Subject" = '<Id from the SELECT>';
-- INSERT 0 1: granted. INSERT 0 0: the row is not safe to promote, so delete it instead (below).
INSERT INTO authsystem."UserRoles" ("UserId", "RoleId")
SELECT u."Id", r."Id" FROM authsystem."Users" u, authsystem."Roles" r
WHERE u."Id" = '<Id from the SELECT>'
  AND u."EmailConfirmed" AND u."PasswordHash" IS NULL AND u."EmailChangeRevertTo" IS NULL
  AND r."NormalizedName" = 'ADMIN';
```

Then set the password through the reset mail, as for any new admin (the four steps above).

When the INSERT refuses a row at your address, what to do depends on whose account it is:

- **`undo_armed` is true.** An e-mail change moved someone else's account onto your address, and its
  owner can still take it back with the undo link. Do not delete it. Put another address into
  `AUTH_ADMIN_EMAIL` and restart auth-api.
- **`EmailConfirmed` is false.** Nobody proved they own the address. Delete the row and restart
  auth-api, so the seeder creates the admin.
- **Your own translator account, with a password.** Delete it and restart, or put another address
  into `AUTH_ADMIN_EMAIL` if you want to keep that account. A confirmed account with a password that
  is not yours is someone else's: treat it like the first case.

Delete by the `Id`, not by the address: the stored address can differ from the `.env` in letter case.

```sql
-- UserRoles cascades with the user.
DELETE FROM authsystem."Users" WHERE "Id" = '<Id from the SELECT>';
```

**Warning `2355`: an account can still undo an e-mail change back to the admin address.** The seeder
logs it on every start when `AUTH_ADMIN_EMAIL` matches no account and `AUTH_ADMIN_USERNAME` is free,
but an account moved off that address less than 14 days ago and its undo link still works (#849). An
admin created at that address would make the undo link fail for good, so the seeder skips it. (When
the admin itself changed its address, you get warning `2353` instead, because its username is
taken.) Look at the account that holds the address:

```sql
-- Only undo links that still work: older ones, or rows from before #684 with no timestamp, reserve nothing.
SELECT "Id", "UserName", "Email", "EmailChangeRevertArmedAt" FROM authsystem."Users" WHERE "NormalizedEmailChangeRevertTo" = upper('<admin e-mail>') AND "EmailChangeRevertArmedAt" > now() - interval '14 days';
```

- **It is an account of yours that moved to a new address.** Put that address (`Email`) into
  `AUTH_ADMIN_EMAIL` if the account is the admin, or ignore the warning until the 14 days are over.
- **It is someone else's account.** Leave it alone, and never clear its `EmailChangeRevertTo` by
  hand: if the change was a hijack, that link is how the owner gets the account back. Put another
  address into `AUTH_ADMIN_EMAIL`, or wait until 14 days after `EmailChangeRevertArmedAt` and restart
  auth-api. If the owner uses the undo link first, the address has an account again and the seeder
  logs `2354` instead.

### TLS certificates

- **Real environment:** **Caddy** obtains and renews Let's Encrypt certificates automatically, one
  per vhost (`ACME_EMAIL` in the box `.env`). The app containers handle no TLS: they speak plain HTTP
  behind the proxy. ACME needs the DNS A record to resolve **before** the first bring-up.
- **Local prod-parity:** `scripts/init-prod-https.sh` mints a local CA + a `*.lotro.test` leaf for
  Caddy; `.docker/trust-ca-entrypoint.sh` installs the CA into the tms-api/frontend OS store so their
  OIDC back-channel to `https://auth.lotro.test` is trusted (.NET ignores `SSL_CERT_FILE`).
- **Local dev (host Kestrels):** the apps serve HTTPS with the **native** ASP.NET Core dev cert. Run
  `dotnet dev-certs https --trust` once. No PFX, no mount.

### Databases

The system uses **two** databases: `lotro_translation` (TMS) and `lotro_auth` (Auth). Both live in
the environment's **Neon** project (one project per environment, ADR-0018) on the same branch.
A managed Postgres usually provisions one database, so create the second:

```sql
CREATE DATABASE "lotro_auth";
```

`scripts/init-postgres.sh` does this automatically for the self-hosted (compose) Postgres on first
boot.

### Hygiene

- The box `.env` is the **only** live copy of the set, so the owner keeps one encrypted off-box copy
  (password manager). Losing it costs a re-mint plus the reseed above, never data.
- `.gitignore` ignores `.env`, `.env.*` and `*.env` and lets through only the placeholder examples,
  so a filled env file cannot be committed by accident; the required **GitGuardian** check on `main`
  is the second net. Values travel to a box over ssh only, never through an issue, PR or commit.

## Consistency rules that bite

The cross-service settings that are each valid on their own but break the system when they disagree.
Most "works locally, 401 or redirect error on staging" failures are one of these.

1. **Issuer must equal the token `iss`.** auth-api stamps `OpenIddict__Issuer` into every token's
   `iss`; tms-api validates it against `Auth__Issuer` (`ValidIssuer`). The two must be byte-identical
   and must be the **public auth origin the browser uses**. A mismatch makes tms-api answer `401` to
   every request. Outside Development and Testing, auth-api refuses an issuer that contains
   `localhost`.

2. **Authority is the back-channel address, not the issuer.** `Auth__Authority` (tms-api) and
   `AuthSystem__Authority` (frontend) are where the app **fetches OIDC metadata + JWKS**; the issuer
   is what it **validates `iss` against**. They may differ, but in this repo they are the same in
   every environment: dev leaves `Auth__Authority` unset so it falls back to `Auth__Issuer`
   (`https://localhost:5003`, the host auth Kestrel), and both containerized stacks (Hetzner and the
   parity stack) set both to the **public proxy origin**. Two traps outside dev: (a) in Production
   **OpenIddict rejects plain HTTP**, so the Authority must be `https`: use the public Caddy origin,
   never `http://auth-api:8080`; (b) that host must be reachable from inside the container, with a
   certificate the container's OS store trusts. On the boxes it is a real Let's Encrypt certificate;
   only the local parity stack needs the CA shim.
   One more coupling: the frontend's `AuthSystem__BaseUrl` must share the issuer's origin. The
   refresh-token revoke goes to the `revocation_endpoint` from discovery, which OpenIddict builds from
   `OpenIddict__Issuer`, and the frontend refuses an endpoint off `AuthSystem__BaseUrl`'s origin. A
   mismatch shows only as two warnings per sign-out ("Refused an API request to …" and "Refresh token
   revocation threw an exception."), and the session is then revoked only if the browser reaches
   `connect/logout` (#964). The same warnings appear, once per token, when the website ends a session
   on its own, and then the refresh token stays valid until it expires (#1027).

3. **Frontend redirect URIs must be registered at the auth server.** The frontend sends
   `redirect_uri = <its public origin> + AuthSystem__CallbackPath` (and post-logout = origin +
   `SignedOutCallbackPath`). Those exact absolute URLs must appear in auth's
   `OpenIddict__WebClient__RedirectUris` / `…PostLogoutRedirectUris`. Any difference (scheme, host,
   trailing slash) → OIDC `invalid redirect_uri` at login. `AuthSystem__ClientId` must equal the
   registered web-client id (`lotrokoniecdev-web`). ⚠️ These URIs are written to the DB **only when
   the client row is created**, so changing `DOMAIN_APP` later needs the
   [reseed](#reseed-traps--the-auth-seeder-is-create-if-missing).

4. **CORS origin = the frontend's public URL, as a bare origin.** auth/tms `Cors__AllowedOrigins__0`
   must be the browser app's exact origin: **lowercase scheme + host, no default port, no userinfo,
   no path, no query, no trailing slash** (the validator rejects anything else at boot). It is the
   same value as a redirect URI's origin part.

5. **Behind the TLS-terminating proxy, forwarded headers carry the scheme.** Caddy sends
   `X-Forwarded-Proto` (and Host); all three apps read them (`UseForwardedHeaders`) to rebuild the
   `https` scheme used for `iss`, `redirect_uri` and `Secure` cookies. Trust is limited to Caddy's
   exact address: `ForwardedHeaders__KnownNetworks__0` = `10.60.0.100/32` for all three apps in
   `compose.hetzner.yaml` (#506; `compose.prod.yaml` keeps the `/24`, because it has no second stack
   to fence off). A malformed CIDR aborts boot. The value must stay in step with Caddy's
   `ipv4_address` on the default network (see [Topology](#topology)): moving Caddy's pinned IP without
   updating all three apps silently drops the trust and breaks the `https` scheme. Leaving it unset
   trusts every upstream (with `ForwardLimit = 1`), which is safe **only** while the container port is
   unreachable except through the proxy, so **never publish `:8080`**.

6. **The Data Protection keyring must be persistent.** auth-api + frontend need
   `DataProtection__KeyRingPath` pointing at a persistent volume (`/keys`). A throwaway keyring logs
   everyone out on every deploy and breaks antiforgery and password-reset / e-mail-confirmation
   links. Boot fails fast when it is unset outside Development.

7. **The frontend caller key is one value read by three services.** `FRONTEND_CALLER_KEY` in the box
   `.env` feeds auth-api's and tms-api's `FrontendCaller__Key` and the frontend's
   `AuthSystem__CallerKey` and `TranslationSystem__CallerKey` (ADR-0054). Every frontend→API call
   arrives from the frontend container, so without the key each API's per-address rate limits are
   one bucket for every user; with it, each API meters the visitor the frontend forwards. If the
   services hold different values, nothing fails: calls just fall back to the shared bucket. So never
   set the key on one side by hand.

8. **The health check key is one value per box, and on prod also a GitHub secret.**
   `HEALTH_CHECK_KEY` in the box `.env` feeds auth-api's and tms-api's `HealthCheck__Key`
   (ADR-0058). Without the key in `X-LOTRO-Health-Key`, the full `/health` answers 404 and runs no
   check; `/health/live` and `/health/ready` stay open. On prod, the `PROD_HEALTH_CHECK_KEY`
   repository secret must hold the same value, or the daily health ping goes red with HTTP 404 (the
   command is in the [secrets table](#secret-material--source-of-truth-and-how-to-rotate)).

## Bringing the stack up

### Locally, development (infra + host Kestrels)

The day-to-day inner loop (ADR-0006, amended by #190). Boot the infra-only stack, then run the three
apps on the host:

```bash
scripts/up.sh                       # PowerShell: scripts/up.ps1 — boots postgres + rabbitmq + migrator + mailpit + aspire
dotnet dev-certs https --trust      # one-time, so the host Kestrels serve trusted HTTPS
```

Then start the three host Kestrels: all at once via the Rider compound **`TMS dev (all hosts)`**, or
each in its own terminal:

```bash
dotnet run --project src/AuthSystem/LotroKoniecDev.AuthSystem.API                 # → https://localhost:5003
dotnet run --project src/TranslationSystem/LotroKoniecDev.TranslationSystem.API   # → https://localhost:5002
dotnet run --project src/Frontend/LotroKoniecDev.Frontend                         # → https://localhost:7017
```

Verify: open `https://localhost:7017`, log in via OIDC, and confirm an authenticated TMS call works.
An app-code change is picked up by re-running that one project (or hot reload), with no image
rebuild.

### Locally, production-parity (the rehearsal)

The fastest way to exercise the real topology (real keys, forwarded headers, DP volumes, a
containerized frontend, Caddy TLS) on a laptop (ADR-0008 §4). It runs the **same four images and the
same proxy shape** as the box, so prod-only breakage shows up before staging:

```bash
scripts/up-prod.sh --build          # PowerShell: scripts/up-prod.ps1 --build
```

It bootstraps `.env.prod` (with freshly generated OpenIddict secrets, the frontend caller key and the
health check key), the local CA + certs and the `*.lotro.test` hosts mapping, then runs
`docker compose -f compose.prod.yaml up`. The `.env.prod` bootstrap runs only when the file is
missing. An older `.env.prod` without `FRONTEND_CALLER_KEY` or `HEALTH_CHECK_KEY` needs them appended
by hand, or compose refuses to render:
`printf 'FRONTEND_CALLER_KEY=%s\n' "$(openssl rand -base64 32)" >> .env.prod`, and the same for
`HEALTH_CHECK_KEY`. Verify (the full `/health` needs the health check key from `.env.prod`; without it
the answer is 404):

```bash
HEALTH_CHECK_KEY="$(grep '^HEALTH_CHECK_KEY=' .env.prod | cut -d= -f2-)"
curl --cacert .docker/prod-https/rootCA.crt -H "X-LOTRO-Health-Key: $HEALTH_CHECK_KEY" https://auth.lotro.test/health
curl --cacert .docker/prod-https/rootCA.crt -H "X-LOTRO-Health-Key: $HEALTH_CHECK_KEY" https://tms.lotro.test/health
# browser OIDC login: https://app.lotro.test
```

For an all-local run (no external SMTP or OTLP), add the profiles, so the SMTP check of the deep auth
`/health` passes and traces are viewable, and set `Email__Host=mailpit` / `Email__Port=1025` /
`Email__Mode=None` + `OTEL_EXPORTER_OTLP_ENDPOINT=http://aspire-dashboard:18889` in `.env.prod`:

```bash
docker compose -f compose.prod.yaml --env-file .env.prod --profile local-smtp --profile local-otel up --build
```

The parity stack runs Production, so its admin is seeded **without a password**, like a box
(ADR-0056). Set `AUTH_ADMIN_USERNAME` / `AUTH_ADMIN_EMAIL` in `.env.prod` and run with
`--profile local-smtp`. Then do the [first sign-in steps](#admin-account--first-sign-in-and-rotation)
against `https://auth.lotro.test` and read the reset mail in Mailpit at `http://localhost:8026`.

### (Re)provisioning a box

1. **Owner:** Hetzner console → create the box (Ubuntu LTS, backups on for prod) with the owner's ssh
   **public key** → note the IP.
2. **DNS first** (ACME needs it to resolve before certificates can issue): registrar panel → A
   records for every public hostname → the new IP, TTL 300. The hostnames are in the
   [env matrix](#environment-variable-matrix): prod trio → prod IP, staging trio → staging IP.
3. **Bootstrap** (idempotent: a re-run changes nothing). It asks nothing, so it needs no `ssh -t`:

   ```bash
   scp scripts/hetzner/bootstrap.sh root@<ip>:/root/
   ssh root@<ip> REBOOT_TIME=02:30 bash /root/bootstrap.sh    # prod
   ssh root@<ip> REBOOT_TIME=01:30 bash /root/bootstrap.sh    # staging: reboots an hour before prod
   ```

   A box's first run needs `REBOOT_TIME` and stops before any change without it. A later re-run
   without it keeps the time the box has ([Gotchas](#gotchas)).

   **Bootstrap stores no registry login for `deploy`** (#1330). Every image of our stack is public, so
   it pulls without one, and `deploy.sh` ignores `deploy`'s Docker config anyway ([Gotchas](#gotchas)).
   Our images must stay public for that reason. Another stack with private images sets up its own
   login. Bootstrap never touches a login that is already there.

   **Bootstrap overwrites `/etc/ssh/sshd_config.d/00-hardening.conf`, `/etc/fail2ban/jail.local`,
   `/usr/local/sbin/docker-user-firewall` and `/etc/systemd/system/docker-user-firewall.service`**
   (it converges them to the repo version), so fold any hand-tuned directive into the script first.

   **The Docker leg adopts the box's engine; it does not install one**: see [Gotchas](#gotchas).
   Swap needs no separate step: bootstrap's first leg lays it down.
4. **Stack files:** as `deploy`, copy `compose.hetzner.yaml` + `.docker/hetzner/` to `/opt/lotro/`
   and `scripts/hetzner/deploy.sh` to `/opt/lotro/deploy.sh`. Assemble the box's `.env` from
   `.env.hetzner.example` ([Secrets](#secrets) has every value's source of truth and rotation command)
   and `chmod 600` it.
5. **Bring-up (staging box first, as the rehearsal):** as `deploy`, on each box, through `deploy.sh`
   and not a plain `up -d`. It is the same rollout CD runs, and it is the one step that gives a new
   broker its policy for parked e-mail (ADR-0036 §6). Name a `sha-<short sha>` tag of a green commit
   on `main`, never `latest`: the script pins the tag in `.env`, CD's next rollback returns to whatever
   `.env` names, and `latest` moves with every green CI run on `main`:

   ```bash
   cd /opt/lotro     # .env is picked up automatically; COMPOSE_PROJECT_NAME comes from it
   IMAGE_TAG=sha-<short sha> bash /opt/lotro/deploy.sh          # validate → pull → migrate → up -d → policy → pin
   docker compose -f compose.hetzner.yaml logs -f migrator caddy   # one-shot migration + ACME
   ```

   Then re-check the [consistency rules](#consistency-rules-that-bite), run the
   [smoke test](#post-deploy-smoke-test) against the public origins from an external network
   (`GET / -> 200` proves nothing; smoke's fingerprint leg is the real check), and check that
   `emails.send.dlq` carries the policy
   ([the dead-letter parking lot](#the-dead-letter-parking-lot-emailssenddlq)).
6. **Re-pin the CD host key** (`ssh-keyscan`): see
   [One-time setup per environment](#one-time-setup-per-environment).

### One-time: cutting a live box over to the two segregated networks (#506)

This moved a box that already ran both stacks on one shared network to the two networks of #506. The
lotro deploy recreates our containers on the new topology and creates `${project}_tks`. The TKS
containers keep running meanwhile, but on the old network and **without** the Caddy aliases they
need, so they must be recreated straight after (compose cannot move a running container between
networks). Do the whole sequence on **staging first**, and on prod only once staging is verified. A
short TKS outage is expected and acceptable before launch.

**Pre-flight**: record what you are changing, as `deploy` (`lotro-prod` shown; staging is
`lotro-staging`):

```bash
docker network ls                                     # before the cutover: ONE shared network, both stacks on it
docker network inspect lotro-prod_default \
  --format '{{range .Containers}}{{.Name}} {{.IPv4Address}}{{"\n"}}{{end}}'
```

Everything in that list that is **not** ours (every `tks-*` container) is what this change fences
off. If Caddy already sits at `10.60.0.100` and a `lotro-prod_tks` network exists, the box has been
cut over already: stop here.

1. **Deploy lotro.** CD does it on merge; by hand: `IMAGE_TAG=<tag> bash /opt/lotro/deploy.sh`.
2. **Verify the boundary before touching TKS**: Caddy must hold both pinned addresses, and nothing
   but our containers may remain on `10.60.0.0/24`:

   ```bash
   docker inspect lotro-prod-caddy-1 \
     --format '{{range $n,$c := .NetworkSettings.Networks}}{{$n}} {{$c.IPAddress}}{{"\n"}}{{end}}'
   docker network inspect lotro-prod_default \
     --format '{{range .Containers}}{{.Name}}{{"\n"}}{{end}}'
   ```

3. **Recreate the TKS stack onto `${project}_tks`** (its compose change is the twin ticket,
   koniecdev/TheKittySaver#295). Ship its new `compose.hetzner.yaml`, point `LOTRO_NETWORK` at THIS
   box's network, then recreate:

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

4. **Verify both sites from outside the box**: ours with the [smoke test](#post-deploy-smoke-test),
   TKS by loading its public origin. A 502 on TKS means its containers did not land on
   `${project}_tks` (or lost their `tks-` service-key prefix). Caddy itself is fine; re-check step 3.

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
| `cd.yml` → `gate` | CI finishing on main (`workflow_run`), `v*` tag, dispatch | the **CI-success gate**: CD fires when CI completes, goes on only after a green CI run that a **push in this repository** started, for a commit that **is on `main`** (a fork's branch or a pushed tag can also be called `main`; `scripts/ci/cd-gate.sh`), and pins the exact tested commit (`head_sha`) for build + rollout. Docs-only pushes run no CI, so they trigger no CD. A dispatch from another branch still builds, but its deploy jobs are refused (below). |
| `cd.yml` → `build-and-push` | after a passing `gate` | builds the 4 images, **scans each with Trivy** (fails on a fixable HIGH/CRITICAL), then pushes to GHCR **signed (cosign keyless) + attested (SLSA provenance + SBOM)**: `:sha-<short>`, `:latest` on main, semver on tags. A `v*` tag only publishes; it does not deploy, and the deploy accepts only images built from the run's own ref, which is `main`: an image built from a tag or a branch never deploys (the one exception is a branch's own images during a temporary-rule proof, see [One-time setup per environment](#one-time-setup-per-environment)). **Until #1344 lands, push no `v*` tag, and do not dispatch `cd.yml` from a branch whose head is a commit on `main`:** either one builds that commit again under the same `:sha-<short>`, from another ref, so the commit can then no longer be deployed or rolled back to |
| `cd.yml` → `deploy-staging` | after `build-and-push`, `STAGING_ENABLED == true`, **automatic** | calls the reusable [`deploy.yml`](../../.github/workflows/deploy.yml) under `environment: staging` |
| `cd.yml` → `deploy-prod` | after `deploy-staging`, **behind the `production` approval gate** | the SAME reusable `deploy.yml` under `environment: production` |

**The two-stage promotion model.** Every merge to main with a green CI builds once and
**automatically deploys to staging**. The same `sha-<short>` then **waits at the `production`
environment** for a human. Clicking *Approve* (GitHub → the run → *Review deployments*) **is** the
staging→prod promotion: production receives the same image that passed staging. The one other run
that waits at this gate is a hand-started `Smoke test` against production; approving it deploys
nothing ([Post-deploy smoke test](#post-deploy-smoke-test) → "In CI").

**The switches** (repo Variables): `CD_ENABLED=true` arms the deploy jobs at all; `STAGING_ENABLED=true`
arms the staging leg and makes prod wait on it (ADR-0018).

**Only `main` may deploy (#1336).** Both environments have a deployment branch rule that allows only
`main` ([One-time setup per environment](#one-time-setup-per-environment)). A job on any other branch
or tag that names `staging` or `production` is refused before it starts, so it never gets the
environment's secrets: the deploy key, the Neon key and the smoke secret. The deploy also checks that
every image was built and attested by `cd.yml` from `main` (`scripts/ci/resolve-verified-digests.sh`),
so an image that a branch built cannot be deployed by naming it as `image_tag`, and a tag that a branch
moved fails the check. What this does not cover:

- **It adds no review.** Anyone with write access can still merge their own PR (the `main` ruleset
  asks for no approval), and the merge deploys to staging.
- **`PROD_HEALTH_CHECK_KEY` is still a repository secret** (#1335), so a workflow on any branch can
  read it.
- **Re-runs of older CD runs.** A re-run uses the workflow files of the run it repeats. A CD run that
  started before #1336 merged checks only that the image comes from this repository, so a writer
  could move that run's tag to a branch build and re-run its staging job, which needs no approval.
  GitHub allows a re-run for 30 days after the run, so this ends by itself.

**Stale candidates expire after 24 h.** The gate pins the tested commit, so approving a days-old
run would deploy a days-old SHA. [`cd-janitor.yml`](../../.github/workflows/cd-janitor.yml) (#527)
cancels any CD run left `waiting` at the `production` gate for more than 24 hours (nightly cron,
04:25 UTC; on demand: `gh workflow run cd-janitor.yml`). Nothing is lost: every newer green-CI merge
produces a fresh candidate, and any older commit built on `main` can still be deployed: dispatch
`cd.yml` from `main` with `image_tag=sha-<short>`. That run ships main's current compose file,
Caddyfile and `deploy.sh` with the older images.

**Cancelling a `waiting` run by hand:** the UI *Cancel* button and `gh run cancel` do **not** work on
approval-gated runs. They only file a cancellation request no runner ever picks up, so the run stays
`waiting` ("requested to cancel") for good (#592). Use the call the janitor makes:
`gh api -X POST repos/koniecdev/LotroKoniecDev/actions/runs/<run-id>/force-cancel`.

### What `deploy.yml` does

**Prod leg only, first: the N-1 promotion gate (#534).** Promotion is batched, so the release the
approval replaces is the last *deployed* one, not the last *merged* one. The job finds the release
that is actually serving: the last successful `production` deployment from the API, cross-checked
against the `IMAGE_TAG` pinned on the box, which also stands in when the API cannot answer. When the
span since that release touches `Migrations/`, it re-runs the ADR-0024 proof against it. There are two
different red verdicts, with two different fixes:

- **`RED`: the batch's schema breaks the release prod is running right now. Do not retry.** Approve a
  candidate that contains only the expand, let it serve, then promote the contract.
- **`COULD NOT RUN`: nothing ran against the schema, so the batch is unjudged, not proven bad.** That
  covers the proof's own setup (script generation, the worktree, the seam), and a serving release
  that no longer restores or builds with today's toolchain: an old tree is not judged again by
  today's rules, so a warning-shaped failure there is infra, never a schema verdict (#679). Fix the
  failure named in the log and re-run the job. Do **not** split the batch, and do not reach for the
  `image_tag` dispatch: it skips the gate, so nothing would be proven at all.

A promotion without migrations skips the proof in seconds. A baseline that cannot be found fails
closed. "Nothing is serving yet" is the one verdict that lets a batch through unproven, so it needs
**two signals that agree**: no `production` deployment on record ever reached `success`, *and* the
box pins no `IMAGE_TAG`. A silent box the API cannot back up therefore blocks instead of skipping.
Check `/opt/lotro/.env` and `docker compose ps`; if it really is the first deploy there, dispatch with
an explicit `image_tag`. A manual `image_tag` dispatch skips the gate with a warning, so verifying
that path is up to you. Note that the deployment record it leaves names the *run's* sha, not the tag
you rolled; the box cross-check catches that on the next promotion.

On the runner, the job then resolves the tag to **digests** and checks that each image was built and
attested by this repository's `cd.yml` from the run's own ref, which is always `main`
(`scripts/ci/resolve-verified-digests.sh`; fails closed). Then it cuts the Neon pre-migration snapshot branch (MIGR-04) and publishes the restore
point. Then it ssh-es to **that environment's box** (`vars.HETZNER_HOST`: one box IS one
environment), snapshots the live config into `/opt/lotro/.previous/`, copies `compose.hetzner.yaml`,
the Caddyfile and `scripts/hetzner/deploy.sh` into `/opt/lotro`, and runs the script:

1. validates the composed config **and the Caddyfile**: a typo stops here, while the running Caddy
   still serves its current config;
2. `pull`, then checks that the pulled digests are the ones whose provenance CD just verified (GHCR
   tags are mutable, so this closes the gap between the gate and what actually starts);
3. **the migration gate:** runs the migrator as its own one-off container (`compose run --rm --no-deps
   migrator`) and goes on only on exit 0. A bad migration therefore stops the roll with the
   **previous release still serving**: it changes nothing for the live site;
4. `up -d`: only now are the app containers recreated, onto an already-migrated schema;
5. reloads Caddy (its Caddyfile is a bind mount, and compose does not notice content changes);
6. sets the broker policy that makes a parked e-mail message expire after 7 days (ADR-0036 §6): see
   [the dead-letter parking lot](#the-dead-letter-parking-lot-emailssenddlq). A broker that does not
   answer only prints a warning; it never fails the deploy;
7. pins `IMAGE_TAG` in the box `.env` **last, and only on success**, so the file always describes
   what is actually running.

Finally the runner waits for the public origins to answer, then smokes them. A red smoke rolls the
box back automatically, **images and config** (from `.previous/`), and the job fails. The previous
tag gets the same provenance check first, against `main` and tried up to three times, and its
digests go to the box (`scripts/ci/rollback-to-previous-tag.sh`). If the tag cannot be verified (the check refuses it, or
its digests cannot be looked up), only the config is restored, the run summary says **Not rolled
back**, and the box keeps the release that just failed: roll back by hand
([Deploy or roll back by hand](#deploy-or-roll-back-by-hand)).

> ⚠️ **Never fold the migration gate back into `up -d`.** `compose.hetzner.yaml` declares
> `depends_on: { migrator: { condition: service_completed_successfully } }`, and it is tempting to let
> that be the gate. It is not: the condition gates the **start** of the apps, not their **creation**.
> `compose up` recreates every changed service first (and every deploy changes the image tag), so the
> old app containers are destroyed *before* the migrator runs. A failed migration then leaves the apps
> created but never started, and the site **down** behind the surviving Caddy (verified on Docker
> 29.6.1, #490). Running the migrator as its own container first is what keeps the old release serving.

**There is no blue/green.** A 4 GB box cannot hold a second live set of three apps, so containers are
recreated in place: a deploy costs a few seconds of downtime per app, and the smoke can only run after
the new containers are live. The migration gate and the automatic rollback take its place.
Recreating Caddy also blips TheKittySaver, which it proxies.

**A rollback reverts code, never schema** (ADR-0023). Re-deploying the previous tag re-runs the
*older* migrator, which only applies pending migrations and never reverts one, so the old app comes
back up against the **new** schema. That is safe because every migration is N-1 backward-compatible.
It is also why a bad *migration* is not a rollback case at all, but a roll-forward or a restore
([Database migrations](#database-migrations)).

### One-time setup per environment

**Precondition: the box needs a `deploy` user that owns the stack.** CD connects as `deploy` and
copies files over `/opt/lotro`, so a box brought up by hand as root cannot be deployed: there is no
`deploy` to log in as, and even once created it cannot read the root-owned `0600` `.env` nor
overwrite root-owned stack files. `bootstrap.sh` creates the user *and* gives it `/opt/lotro` +
`/opt/tks` recursively. That is the fix, not a `chown` by hand.

CD fails closed with a "not wired for ssh deploys" error until each environment (`staging`,
`production`) has these:

| Kind | Name | Value |
|---|---|---|
| var | `HETZNER_HOST` | the box IP — staging `91.98.74.228`, production `167.233.159.221` |
| var | `HETZNER_SSH_KNOWN_HOSTS` | `ssh-keyscan` output for that IP (host-key pinning — see below) |
| var | `HETZNER_USER` | optional; defaults to `deploy` |
| var | `AUTH_URL` / `TMS_URL` / `FRONTEND_URL` | the environment's public origins (smoke targets) |
| var | `NEON_PROJECT_ID` | that environment's Neon project (MIGR-04 snapshot; unset ⇒ the snapshot step skips) |
| secret | `HETZNER_SSH_KEY` | the CD deploy **private** key for that box (below) |
| secret | `SMOKE_CLIENT_SECRET` | `= OpenIddict__ApiClientSecret` from that box's `.env` |
| secret | `NEON_API_KEY` | project-scoped Neon key (MIGR-04) |

> ⚠️ **A stale `SMOKE_CLIENT_SECRET` looks exactly like a broken deploy.** It is the one value here
> that lives in *two* places, the box `.env` and the GitHub secret, and nothing keeps them in step.
> Rotate the OpenIddict keys on a box without setting the secret again and CD still deploys: images
> pull, the migration gate passes, the containers come up, and then smoke's `client_credentials` leg
> gets **401**, CD judges the rollout red and **rolls it back automatically**. The log blames the
> release; the fault is the secret. Whenever you rotate `OpenIddict__ApiClientSecret`, set this again
> at the same time, **per environment**, and check it with the hand-started smoke
> ([Post-deploy smoke test](#post-deploy-smoke-test) → "In CI") before the next deploy. There is no
> repository-level copy (#1335), so an environment without its own `SMOKE_CLIENT_SECRET` stops CD at
> the "not wired for ssh deploys" check, before anything touches the box. **Never set it without
> `--env`:** a workflow cannot tell a repository copy from an environment one, so a repository copy
> would again fill in, unseen, for an environment that has none.

**Allow only `main` to use the environment (#1336).** GitHub gives an environment's secrets to any
job that names it, from any branch, unless the environment limits the branches. `staging` has no
reviewer, so without this rule a workflow on any pushed branch would get the staging deploy key at
once. Set it on **both** environments: Settings → Environments → `staging` (then `production`) →
**Deployment branches and tags** → **Selected branches and tags** → **Add deployment branch or tag
rule** → Ref type **Branch**, name pattern `main` → **Add rule**. On `production`, keep the required
reviewer, with "Prevent self-review" off and administrator bypass on. Check it:

```bash
gh api repos/koniecdev/LotroKoniecDev/environments \
  --jq '.environments[] | {name, deployment_branch_policy, rules: [.protection_rules[].type]}'
# both: "deployment_branch_policy": {"protected_branches": false, "custom_branch_policies": true}
# production's rules still include "required_reviewers"
for e in staging production; do
  gh api "repos/koniecdev/LotroKoniecDev/environments/$e/deployment-branch-policies" \
    --jq '[.branch_policies[] | {name, type}]'
done
# both: [{"name":"main","type":"branch"}]
```

- **No tag rule.** Nothing deploys from a tag, and no rule limits who may push one, so a tag rule
  would let anyone with write access deploy any commit by tagging it.
- **Not "Protected branches only".** `main` is guarded by a ruleset, not by a classic branch
  protection rule. GitHub documents that option only for branch protection rules, and says that with
  none of them every branch may deploy.

**Proving a change to a job that names an environment** (`deploy.yml`, `smoke.yml`, the deploy jobs in
`cd.yml`). A run from a branch is refused now, so prove such a change after the merge, or with a
temporary rule: add a branch rule for that one branch on `staging` only, never on `production`, run
the proof, and delete the rule straight after. While the rule exists, that branch can read staging's
secrets. During the proof the production job is refused at once. The proof run's own rollback still
works, because the rollback checks its target against `main`. But after a proof deploy, staging runs
the branch's images, and the next `main` deploy cannot roll back to them. So after you delete the
rule, put a `main` build back on staging at once: dispatch `cd.yml` from `main` and reject its
production step.

**Mint a CD deploy key per box**: one key per environment, so a staging compromise cannot touch prod.
It is the `deploy` user's key, never `root`'s:

```bash
ssh-keygen -t ed25519 -N '' -C 'github-cd-staging' -f ./cd-staging      # no passphrase: CD is non-interactive
ssh-copy-id -i ./cd-staging.pub deploy@91.98.74.228                     # or append the .pub to /home/deploy/.ssh/authorized_keys
gh secret set HETZNER_SSH_KEY --env staging < ./cd-staging              # the PRIVATE key
gh variable set HETZNER_HOST --env staging --body '91.98.74.228'
gh variable set HETZNER_SSH_KNOWN_HOSTS --env staging --body "$(ssh-keyscan -t ed25519 91.98.74.228)"
rm ./cd-staging ./cd-staging.pub                                        # GitHub + the box now hold the only copies
```

Repeat for `production` with the prod IP and `--env production`. Then delete the local private key:
losing it costs one re-mint, and a copy lying around is a spare key to prod.

**Host-key pinning is not optional.** `HETZNER_SSH_KNOWN_HOSTS` is why CD can run with
`StrictHostKeyChecking=yes`; without it the deploy would trust whoever answers on that IP and hand
them the deploy key. Re-provisioning a box changes its host key: run the `ssh-keyscan` line above
again, or every deploy fails at the ssh step (that failure is the pin working, not a bug).

### Deploy or roll back by hand

The same script CD runs, so a hand run and a CD run leave the box in the same state. Check the tag
first, on your own machine, with the check CD uses (it needs `gh` signed in and `docker buildx`; run
`docker login ghcr.io` first if the packages are private):

```bash
line="$(REGISTRY=ghcr.io IMAGE_NAMESPACE=koniecdev GITHUB_REPOSITORY=koniecdev/LotroKoniecDev \
  GITHUB_REF=refs/heads/main scripts/ci/resolve-verified-digests.sh sha-1a2b3c4)"
# $line is EXPECTED_DIGESTS=<four repo@digest refs>, and empty unless all four were built by cd.yml from main
[ -n "$line" ] && ssh deploy@<box> "IMAGE_TAG=sha-1a2b3c4 EXPECTED_DIGESTS='${line#EXPECTED_DIGESTS=}' bash /opt/lotro/deploy.sh"
```

With `EXPECTED_DIGESTS`, `deploy.sh` refuses any pulled image that is not one of the checked digests,
so a tag moved between the check and the pull cannot slip in.

Always name a `sha-<short>` tag, never `latest`: the script pins the tag in `.env`, the next
automatic rollback returns to whatever `.env` names, and `latest` moves with every green CI run.

Rolling back = deploying the previous tag. The tag that is live now is the `IMAGE_TAG` line in
`/opt/lotro/.env` (written only after a successful roll, so it never lies), and every CD run prints the
one it replaced (`PREVIOUS_IMAGE_TAG=…` in the job log, plus the deployed tag in the run summary).
GHCR keeps every `sha-<short>`, so any commit built on `main` can still be deployed (an image built
from a branch or a tag fails the check). The config that
was live before the last deploy sits in `/opt/lotro/.previous/`: restore those three files first if
you are undoing a compose or Caddyfile change, not just an image change.

Two edges worth knowing. `deploy.sh` prunes unused images older than 14 days
(`PRUNE_UNUSED_OLDER_THAN`), so an older tag may need a fresh pull on the box; GHCR still has it.
`deploy.sh` pulls it for you. A pull by hand needs `DOCKER_CONFIG=/nonexistent` in front, or a stored
login that has expired fails it ([Gotchas](#gotchas)). And re-running CD for a commit (**Actions → CD
→ Run workflow**, optionally with an explicit `image_tag`) works only from `main`, the `image_tag` must
be an image built from `main`, and the run still goes through the staging→prod gate.

## Database migrations

### Strategy (ADR-0008 §6, ADR-0023)

Schema changes apply as a **pre-deploy step**: a one-shot container that runs to completion *before*
the app containers are recreated, never from inside an application at startup. The rules:

- **Two write contexts, one job.** The Translation Management System (`ApplicationWriteDbContext`,
  schema `translation`, database `lotro_translation`) and the Auth server (`AuthDbContext`, schema
  `authsystem`, database `lotro_auth`) each have their own migration history. The job applies
  Translation first, then Auth.
- **The artifact is the migrator image** `ghcr.io/koniecdev/lotrokoniecdev-migrator`, built from
  `Dockerfile.migrator.prod` and published by CD. It holds two **self-contained**
  `dotnet ef migrations bundle` executables (one per context) on a lean `runtime-deps` base: no SDK,
  no `dotnet-ef` tool, no source.
- **Idempotent.** Each bundle applies only the migrations missing from that context's
  `__EFMigrationsHistory` table, so re-running it is a safe no-op.
- **The bundle RID follows the build platform** (#594). The bundles are self-contained, so their
  architecture must match the `runtime-deps` base, which resolves to whatever is being built for. The
  Dockerfile derives the RID from BuildKit's `TARGETARCH` (`amd64` → `linux-x64`, `arm64` →
  `linux-arm64`): CD's amd64 runners produce the `linux-x64` image the boxes run, and a local
  prod-parity build on Apple Silicon gets arm64 bundles with no build-arg. Override with
  `--build-arg TARGET_RUNTIME=<rid>`; an architecture with no mapping fails the build rather than
  shipping a wrong-arch image.
- **Fail fast = nothing serves half-migrated.** Any failure (unreachable DB, bad migration, missing
  connection string) exits non-zero. On the box, CD runs the migrator as **its own container before
  `up -d`**: see the warning in [What `deploy.yml` does](#what-deployyml-does).
- **Forward-only (ADR-0023).** There is no automated rollback step. EF down-migrations exist but are
  never run in a real environment; a bad migration is rolled forward with a new migration. The
  safety valve for a logically bad migration is a Neon restore: the deploy's pre-migration
  auto-snapshot (MIGR-04) or a point-in-time restore.

### Inputs (environment variables)

The migrator reads only the two Npgsql connection strings in the [migrator table](#migrator). Both
databases must already exist (see [Databases](#databases)).

### Running migrations

**On a box**: CD does this for you (the migration gate). By hand, as `deploy` (with
`DOCKER_CONFIG=/nonexistent`, so a stored login that has expired cannot fail the pull of a missing
image: [Gotchas](#gotchas)):

```bash
cd /opt/lotro
DOCKER_CONFIG=/nonexistent docker compose -f compose.hetzner.yaml run --rm --no-deps migrator
```

**Local production-parity stack (`compose.prod.yaml`)**: automatic. The `migrator` service runs to
completion and `auth-api` / `tms-api` wait for its success:

```bash
scripts/up-prod.sh --build
docker compose -f compose.prod.yaml --env-file .env.prod logs migrator   # watch it apply
```

**Anywhere else**: pull the published image and run it once against the target databases. Use the
**same image tag** you are about to deploy for the APIs, so schema and code move together:

```bash
docker run --rm \
  -e ConnectionStrings__TranslationDatabase="Host=…;Database=lotro_translation;Username=…;Password=…;Ssl Mode=Require;Timeout=60" \
  -e ConnectionStrings__AuthDatabase="Host=…;Database=lotro_auth;Username=…;Password=…;Ssl Mode=Require;Timeout=20" \
  ghcr.io/koniecdev/lotrokoniecdev-migrator:<tag>
```

Expected tail on success:

```
== TRANSLATION MIGRATOR DONE ==
== AUTH MIGRATOR DONE ==
== MIGRATOR COMPLETE ==
```

A non-zero exit means the migrations did **not** fully apply. Do not roll out the APIs; read the log,
fix forward, run it again.

### Authoring a new migration (developer, not operator)

Migrations are generated with the SDK + `dotnet-ef` against the source: see the CLAUDE.md "TMS — EF
Core migrations" command block for the exact `dotnet ef migrations add …` call (TMS resolves through
its Persistence project, Auth through its API project; only those two carry EF Core Design). Once
committed, the next CD build bakes them into the migrator image; operators never generate migrations.

### Verifying

```bash
# applied Translation migrations
psql "$ConnectionStrings__TranslationDatabase" -c 'SELECT "MigrationId" FROM translation."__EFMigrationsHistory" ORDER BY "MigrationId";'
# applied Auth migrations
psql "$ConnectionStrings__AuthDatabase"        -c 'SELECT "MigrationId" FROM authsystem."__EFMigrationsHistory" ORDER BY "MigrationId";'
```

### Recover from a bad migration (Neon PITR)

Forward-only (ADR-0023) means a logically bad or data-corrupting migration is **never** undone by a
down-migration in a real environment: the deploy's migrator gate commits the schema *before* any
traffic moves, and the pipeline's failure path rolls back **app code only**. The real safety valve is
the database's own history. Both real environments run on Neon (prod: ADR-0014; staging: a separate
Neon project, ADR-0018), and Neon keeps page history that allows an instant point-in-time restore of
a branch to any moment inside the retention window.

| Environment | Neon project | Branch (single, default) | History retention |
|---|---|---|---|
| production | `lotro-translator-prod` | `production` | **7 days** (604800 s) on Launch (ADR-0071); 6 h before the switch |
| staging | `lotro-translator-staging` | `production` | **6 h** (21600 s) |

- **Look the ids up live**; they change when a project is re-created. The project id is the
  `NEON_PROJECT_ID` variable of that GitHub environment, and the API lists the branch ids:

  ```bash
  gh variable get NEON_PROJECT_ID --env production        # staging: --env staging
  curl -s -H "Authorization: Bearer $NEON_API_KEY" \
    https://console.neon.tech/api/v2/projects/<project_id>/branches | jq '.branches[] | {id, name, default}'
  ```

- **6 hours is the Free-plan ceiling** (staging); Launch allows 7 days (prod). Check the live value at any time:

  ```bash
  curl -s -H "Authorization: Bearer $NEON_API_KEY" \
    https://console.neon.tech/api/v2/projects/<project_id> | jq .project.history_retention_seconds
  ```

- **A branch restore is branch-wide.** `lotro_translation` and `lotro_auth` live on the same branch,
  so they always rewind **together**. That is right for "undo the migrator run", because the one
  migrator applies both contexts.

#### Risk boundary — what can be undone, and how far back (MIGR-01, revised by #688)

The first outside translator started on prod on 2026-11-11, so the data can no longer be re-created
by hand. Since ADR-0071 the layers are:

| How far back | What | Granularity |
|---|---|---|
| up to 7 days | Neon point-in-time restore (Launch plan, history window 7 days) | to the second |
| the latest deploy | the MIGR-04 auto-snapshot (below) | the moment before that migrator run |
| up to 30 days | the nightly encrypted dump in R2 ([Database backups](#database-backups)) | one per night, 01:00 UTC |

**Still not covered:** rows written after the last dump that are also outside the history window
when Neon itself is lost (at most one day of work), and damage noticed more than 30 days later.
The history window is 7 days only once the production projects are on Launch
([Database backups](#database-backups) → Neon plan); until then it is the Free plan's 6 hours.

#### Procedure

Scenario: the migrator gate ran a migration that runs fine but is *logically* wrong: it dropped or
corrupted data, or broke the serving release beyond what N-1 compatibility (ADR-0023) guarantees.
Time matters: **the restore point must still be inside the history window** (7 days on prod, 6 h
on staging), unless you have the auto-snapshot, which never expires.

**0. Find the restore point.** The deploy run's step summary prints a **"DB restore point
(pre-migration)"** table: the UTC timestamp captured just *before* the migrator ran, the target
migration per context, and the **auto-snapshot branch**. When it names one, prefer
[Restore from the auto-snapshot](#restore-from-the-auto-snapshot-migr-04): no timestamp math, no window
pressure.

**1. Put the last good release back first.** After the restore the schema is pre-migration again, and
only the previous release's code is proven against it (N-1 holds one step back; *new* code on the
*old* schema is the combination nothing proves). Check the previous tag and redeploy it on the box,
as in [Deploy or roll back by hand](#deploy-or-roll-back-by-hand):

```bash
line="$(REGISTRY=ghcr.io IMAGE_NAMESPACE=koniecdev GITHUB_REPOSITORY=koniecdev/LotroKoniecDev \
  GITHUB_REF=refs/heads/main scripts/ci/resolve-verified-digests.sh <previous-good-sha>)"   # on your machine
[ -n "$line" ] && ssh deploy@<box> "IMAGE_TAG=<previous-good-sha> EXPECTED_DIGESTS='${line#EXPECTED_DIGESTS=}' bash /opt/lotro/deploy.sh"
```

(When the rollout failed on its own, CD's automatic rollback already did this, and only steps 2–4
remain.)

**2. Restore the Neon branch to just before the migrator ran.** Use the project + branch ids you
looked up above and the step-0 timestamp:

```bash
curl -sf -X POST -H "Authorization: Bearer $NEON_API_KEY" -H "Content-Type: application/json" \
  "https://console.neon.tech/api/v2/projects/<project_id>/branches/<branch_id>/restore" \
  -d '{
    "source_branch_id": "<branch_id>",
    "source_timestamp": "<step-0 restore point, RFC 3339, e.g. 2026-07-05T14:03:00Z>",
    "preserve_under_name": "pre-restore-<yyyymmdd-hhmm>"
  }'
```

- `source_branch_id` is the **same branch** (a restore into its own history). In that mode
  `preserve_under_name` is **mandatory**, and it is also the undo: Neon parks the current
  (post-migration) head as a branch under that name, so the restore can be reversed and any
  post-migration writes can still be salvaged from it.
- Console alternative: project → *Branches* → `production` → **Restore** → *From this branch's
  history* → pick the timestamp.
- The restore is near-instant (copy-on-write). Active connections drop once; the apps' Npgsql pools
  reconnect on the next request.

**3. Verify.** Run the [Verifying](#verifying) queries: the bad migration's row must be **gone** from
that context's `__EFMigrationsHistory` (the restore rewinds schema, data and history together). Then
[smoke](#post-deploy-smoke-test) the environment.

**4. Roll forward.** Fix the migration in a new commit and let the normal pipeline redeploy; the
migrator gate re-applies from the rewound history. Once you are confident, delete the `pre-restore-*`
safety branch (the Free plan allows at most 10 branches per project):

```bash
curl -sf -X DELETE -H "Authorization: Bearer $NEON_API_KEY" \
  "https://console.neon.tech/api/v2/projects/<project_id>/branches/<preserved_branch_id>"
```

#### Restore from the auto-snapshot (MIGR-04)

Every deploy with the Neon step configured creates a **pre-migration snapshot branch** in that
environment's Neon project. It runs on the runner after the images are verified and before anything
touches the box, so before the migrator starts:

- **Shape:** `migr04-pre-<short-sha>-<utc-ts>`, branched from the project's default branch head, with
  **no compute endpoint**: it costs no compute, only pinned history and storage.
- **Where it is recorded:** the run summary's "DB restore point (pre-migration)" table, name + id.
- **Why it exists next to PITR:** a branch head never expires. PITR history lasts 7 days on prod
  (Launch) and 6 h on staging (Free); the snapshot stays restorable however late the bad migration is
  noticed.
- **Configuration (per GitHub environment; optional):** the environment secret `NEON_API_KEY`
  (project-scoped) + the variable `NEON_PROJECT_ID`. When either is missing, the deploy logs
  `Neon snapshot skipped (not configured)` and goes on; a Neon API error logs a warning and goes on
  too. **The snapshot is a net, not a gate**: the deploy never fails because of it.

**Retention: at most ONE snapshot branch per project.** Right before it creates the new snapshot, the
deploy deletes every older `migr04-pre-*` branch: every branch pins history (billed on prod, near
the Free plan's storage cap on staging), and by the time deploy N+1 runs, deploy N's migration has proven
itself. So **the snapshot protects the latest deploy only**; an older bad migration falls back to
PITR (inside the history window), the nightly dump, or the risk boundary above.

**Restore procedure**: the same as the [PITR procedure](#procedure) except step 2. Restore the
default branch **from the snapshot branch's head** instead of from its own history. With
`source_timestamp` left out, the source is restored to its head, which for the snapshot **is** the
pre-migration state. `preserve_under_name` is **required** here because the restored branch has
children:

```bash
curl -sf -X POST -H "Authorization: Bearer $NEON_API_KEY" -H "Content-Type: application/json" \
  "https://console.neon.tech/api/v2/projects/<project_id>/branches/<default_branch_id>/restore" \
  -d '{
    "source_branch_id": "<snapshot branch id from the run summary, br-…>",
    "preserve_under_name": "pre-restore-<yyyymmdd-hhmm>"
  }'
```

- The current (post-migration) head is parked as the `pre-restore-*` branch (the undo), and the
  branch's existing children, the snapshot included, move under it.
- Cleanup order matters: delete the `migr04-pre-*` branch **before** the `pre-restore-*` branch. Neon
  refuses to delete a branch that still has children.

#### Rehearsal (staging drill)

Steps 2–4 can be rehearsed on staging at any time, without a deploy: restore the staging branch to
five minutes ago (no data change while staging is idle), check that the [Verifying](#verifying)
output is unchanged, then delete the preserved branch. Do **not** run it while someone is using
staging: the restore drops connections and rewinds anything written after the restore point.

## Database backups

ADR-0071 (#688). Three layers stand between a mistake and lost work: Neon's point-in-time restore
(7 days on the Launch plan), the MIGR-04 snapshot of the latest deploy, and a nightly encrypted
dump of both prod databases in Cloudflare R2, kept 30 days. Staging has the first two only: it is
disposable, and a nightly dump would use up its free plan's 5 GB of transfer.

`/opt/lotro/backup.sh` (from `scripts/hetzner/backup.sh`; CD copies it to every box) runs from the
`deploy` user's crontab on **lotro-prod** at 01:00 UTC. Per database, auth first: a table that is
never empty on prod must have rows → `pg_dump` in a `postgres` container pinned by digest against
the direct host → `pg_restore` reads the whole dump → AES-256 → upload → read back size and MD5.
Only when both landed does it write
`/opt/lotro/backups/metrics/lotro_backup.prom`, which the agent exports and the alert
"LOTRO — the nightly database backup did not land" reads (26 hours). Its header comment is the
reference; its suite is `scripts/tests/hetzner-backup.tests.sh`.

**Do not approve a prod deploy between 01:00 and 01:30 UTC.** The dump holds a shared lock on every
table until it ends. A migration that needs an exclusive lock waits for it, and every query on that
table then waits behind the migration, so the site stalls until the dump is done. #1357 gives the
migrator a lock limit, and then this rule goes.

### Neon plan (once, owner)

A nightly dump moves about 0.2 GB out of Neon, about 6 GB a month, and the Free plan stops a
project's database for the rest of the month after 5 GB. So the production projects go to the
Launch plan, and staging stays free. Plans apply to a whole organization, and Neon does not move a
project from a paid organization to a free one, so **the order matters**:

1. Neon console → create a new organization on the Free plan.
2. Move every staging project into it: project → **Settings** → **General** → **Transfer project**.
   The connection string does not change, so nothing goes down. If Neon refuses a second free
   organization, skip steps 1–3 and accept that the staging projects are billed too.
3. The staging environment's MIGR-04 key belonged to the old organization: mint a project-scoped
   key in the new one and set it as the `staging` environment's `NEON_API_KEY` secret
   (`NEON_PROJECT_ID` does not change).
4. The old organization → **Billing** → upgrade to **Launch**.
5. On each production project: **Settings** → **Compute**: autoscaling maximum **0.25 CU**, scale to
   zero on. **Settings** → **Postgres** → **History window**: **7 days** (Launch starts at 1 day).
   Check with the command in [Recover from a bad migration](#recover-from-a-bad-migration-neon-pitr):
   it must print `604800`.
6. **Billing** → **Spending notifications**: $25. It only warns; it stops nothing. At 0.25 CU one
   project's compute costs at most about $19 a month even if something keeps it awake all month;
   a normal month is a few dollars.

### Wiring (once, prod; owner)

Do the Neon plan first: the first dump already counts against the transfer allowance.

1. Cloudflare → **R2** → **Create bucket** `lotro-backups-prod`, jurisdiction **European Union**.
   The bucket holds nothing but dumps.
2. Bucket → **Settings** → **Object lifecycle rules** → add "delete objects **30 days** after upload",
   for **all** objects (no prefix). Read the rule list back: one delete rule, 30 days, no prefix.
   Nothing in the code can see this setting.
   Then **Bucket lock rules** → add a rule for all objects that keeps each one for **29 days**. The
   box's token can then upload but cannot delete a recent dump, so someone who takes over the box
   cannot wipe the backups along with the database. The lifecycle rule still deletes on day 30.
3. R2 → **Manage API tokens** → **Create API token**: **Object Read & Write**, applied to
   `lotro-backups-prod` only. Note the access key id and the secret (shown once).
4. Make the key on your own machine: `openssl rand -base64 32`. Put it in the password manager
   **first**. It is the only value in the box `.env` that cannot be minted again: without it every
   dump is unreadable.
5. As `deploy` (never as root: a root-owned `.env` locks CD out, #833), add to `/opt/lotro/.env` the
   five keys from `.env.hetzner.example`: `BACKUP_ENCRYPTION_KEY`, `BACKUP_R2_ACCOUNT_ID`,
   `BACKUP_R2_BUCKET=lotro-backups-prod`, `BACKUP_R2_ACCESS_KEY_ID`, `BACKUP_R2_SECRET_ACCESS_KEY`.
6. The first run by hand, then the crontab line. The crontab belongs to the `deploy` user, which the
   guest stack uses too, with a backup line of its own: **append, never replace**, and match on the
   full path.

   ```bash
   ssh deploy@lotro-prod
   bash /opt/lotro/backup.sh            # must end with: every database landed in lotro-backups-prod
   crontab -l 2>/dev/null | grep -F '/opt/lotro/backup.sh' \
     || ( crontab -l 2>/dev/null; echo '0 1 * * * bash /opt/lotro/backup.sh 2>&1 | logger -t lotro-backup' ) | crontab -
   crontab -l
   ```

   01:00 UTC keeps the run clear of the prod box's reboot window (02:30 UTC, ssh closed from 02:25).
7. Redeploy the observability stack on **both** boxes ([The observability stack](#the-observability-stack)):
   the prod box for the agent's `textfile` collector, the staging box for the alert rule. Then
   Grafana → **Explore**: `lotro_backup_last_success_timestamp_seconds{box="lotro-prod"}` has a value.
   The rule arms itself on that first value.
8. Run the [restore drill](#restore-drill) once on a real dump.

Day to day: `journalctl -t lotro-backup --since yesterday` on the box shows the last runs, and
`cat /opt/lotro/backups/metrics/lotro_backup.prom` the last good one with the object sizes. The objects
are compressed and the transfer Neon counts is not, so read the month's transfer from the Neon
project's **Usage** page, not from these sizes.

### Shell helpers (paste first)

Every procedure below runs on the prod box as `deploy` and starts with these lines. The `eval` line
takes `env_value` and `PG_IMAGE` from `backup.sh` itself: the same reader of `.env` values, so a key
read here is the key the dumps were encrypted with, and the same digest-pinned image. `psql_in` hands
the password to the container as `PGPASSWORD`, never in argv, where `ps` on the box would show it.
`DOCKER_CONFIG` keeps a registry login stored for `deploy` out of the image pulls, as in `deploy.sh`.
Every connection string below is the keyword form without a password.

```bash
umask 077    # a decrypted dump holds personal data: readable by deploy only
export DOCKER_CONFIG=/nonexistent
mkdir -p /opt/lotro/backups/restore && cd /opt/lotro/backups/restore
eval "$(bash /opt/lotro/backup.sh --helpers)"; echo "$PG_IMAGE"
psql_in() { docker run --rm -i --network host -e PGPASSWORD "$PG_IMAGE" psql -v ON_ERROR_STOP=1 -X "$@"; }
```

### Fetch and decrypt a dump

After the [shell helpers](#shell-helpers-paste-first), in the folder they made (on disk: `/tmp` is
RAM there). Nothing below prints a secret: every value goes from `.env` into a variable or a pipe.

```bash
s3() { printf 'user = "%s:%s"\n' "$(env_value BACKUP_R2_ACCESS_KEY_ID)" "$(env_value BACKUP_R2_SECRET_ACCESS_KEY)" \
         | curl -fsS -K - --aws-sigv4 'aws:amz:auto:s3' "$@"; }
R2="https://$(env_value BACKUP_R2_ACCOUNT_ID).eu.r2.cloudflarestorage.com/$(env_value BACKUP_R2_BUCKET)"

# The objects of one database, oldest first (the names carry the UTC time of the dump):
s3 "$R2?list-type=2&prefix=lotro_translation/" | grep -o '<Key>[^<]*</Key>' | sed 's/<[^>]*>//g' | sort

# Take the newest one from BEFORE the damage:
s3 -o translation.dump.enc "$R2/lotro_translation/lotro_translation-<UTC stamp>.dump.enc"
BACKUP_ENCRYPTION_KEY="$(env_value BACKUP_ENCRYPTION_KEY)" openssl enc -d -aes-256-cbc -md sha256 \
  -pbkdf2 -iter 600000 -pass env:BACKUP_ENCRYPTION_KEY -in translation.dump.enc -out translation.dump
```

### Restore into a scratch database

On the prod box, after the [shell helpers](#shell-helpers-paste-first), so the data stays with the
providers the privacy policy names; never on a laptop.

```bash
# The data files go into a named volume, so the clean-up below can remove them by name. The limit
# and the OOM score make the kernel stop this restore, not the product, when the box runs short.
docker run -d --name restore-scratch -p 127.0.0.1:5433:5432 -e POSTGRES_PASSWORD=scratch \
  --memory 1g --oom-score-adj 500 -v restore-scratch-data:/var/lib/postgresql \
  -v /opt/lotro/backups/restore:/restore:ro "$PG_IMAGE"
# pg_isready does not wait, so loop. Over TCP, not the socket: the image's first-start server
# listens on the socket only, then restarts.
until docker exec restore-scratch pg_isready -q -h 127.0.0.1 -U postgres; do sleep 1; done
docker exec restore-scratch createdb -U postgres scratch
docker exec restore-scratch pg_restore -U postgres --no-owner --no-privileges --exit-on-error \
  -d scratch /restore/translation.dump
SCRATCH='host=127.0.0.1 port=5433 dbname=scratch user=postgres password=scratch'
```

`--no-owner --no-privileges` belong here, on `pg_restore`: `pg_dump` ignores them for an archive.
The scratch database holds the names and e-mail addresses of translators, so neither it nor its
data files outlive the job. `docker rm -f` alone keeps the volume, and with it a plain copy of the
database on the box. When you are done:

```bash
docker rm -f restore-scratch && docker volume rm restore-scratch-data && rm -rf /opt/lotro/backups/restore
docker volume ls -q -f name=restore-scratch-data    # must print nothing
```

### Restore drill

Proven on 2026-11-12, locally: Postgres 18 with the schema of every EF migration, an S3 stand-in
(versitygw) instead of R2, the real `backup.sh`, and the copy-back block as it was then. Eight
rows. After the backup: two honest edits (one of a row that stays undamaged, one of a draft), an
import that changed one row's English and one that added a row, and one account that put rubbish
into five rows (two of them as approvals, one over a row waiting for review and one over the added
row) and approved the edited draft. The dry run changed nothing. The real run restored four rows
(the approved-over one back to NeedsReview with its old English; the approved draft back to the
draft of the night before, where the block now keeps the newer text), skipped the one whose English had
changed, listed the added row as `NOT IN THE COPY`, and left the undamaged row's edit alone. The auth
dump restored with all 9 migrations. On 2026-11-13 the shell helpers, the scratch restore, the
copy-back and the clean-up as they stand now ran again as written, locally against the migrated
schema, on eight rows. The account's own rubbish went back to the copy. A row whose import changed
only its arguments was listed `SKIP: source changed` and kept, and the row an import added was
listed `NOT IN THE COPY`. A row the account approved after an import changed its English was listed
`SKIP: source changed, still approved by the account` and kept. Three rows the account only approved kept their author's text
(`undo approval, keep text`): a new translation and a rework went back to Draft, and an unchanged
NeedsReview row went back to NeedsReview with its old English. The dry run changed nothing, the
scratch container ran with its memory limit, and the clean-up left no container and no volume.

On the real bucket, after the wiring and then every few months: [fetch and decrypt](#fetch-and-decrypt-a-dump)
last night's dumps, [restore them into a scratch database](#restore-into-a-scratch-database) (the
auth dump into a second scratch database), compare
`SELECT count(*) FROM translation."Translations"` with prod. Before you clean up, prove that the key
in the **password manager** works too, since it is the copy you would need after losing the box. In
the same folder:

```bash
read -rs PM_KEY    # paste the key from the password manager
BACKUP_ENCRYPTION_KEY="$PM_KEY" openssl enc -d -aes-256-cbc -md sha256 -pbkdf2 -iter 600000 \
  -pass env:BACKUP_ENCRYPTION_KEY -in translation.dump.enc | cmp - translation.dump \
  && echo 'the password-manager key decrypts the dump'
unset PM_KEY
```

Then [clean up](#restore-into-a-scratch-database): no plain copy stays behind.

### Rows vandalised on Tuesday, now it is Friday

What an account can break: a **translator** cannot approve, so it cannot put text into the game
file. Its damage is rubbish in drafts, and every approved row it edits falls back to Draft and drops
out of the file. An **admin** account can approve, so its damage can reach the file. A whole-database
restore is never the answer: it would also undo every honest change since, account deletions
included (wiki: Import do TMS). Copy back only the damaged rows.

What this procedure covers: rows the account wrote, and rows it approved, other people's drafts
included (one by one or in bulk). A row the account wrote goes back to the copy. A row it only
approved keeps its text and its author and loses only the approval: it goes back to the copy's
status when its text is still the copy's, and to Draft when someone wrote newer text, as an edit
leaves a row. It does not cover an admin account's imports or game-version changes; those need their
own look. On the dump path the copy holds the state of the night before, so when the account wrote
over another person's honest edit made between that night and the damage, that edit is lost too, and
the printed list cannot show it: it compares the copy with prod, nothing in between. A Neon branch
taken one minute before the damage keeps such edits, so prefer it while the history window reaches.
`UpdatedAt` also moves when an import changes a row, so check step 2's answer against the import
times.

1. **Stop the damage.** Lock the account and end its sessions. `LockoutEnd` alone does nothing on
   a row whose `LockoutEnabled` is false, and a new security stamp ends the auth cookie and every
   refresh token (#848); the deletes are a second layer, as in
   [Admin account — first sign-in and rotation](#admin-account--first-sign-in-and-rotation). Then wait out
   the five-minute access-token lifetime (ADR-0049) before you copy anything back.

   ```sql
   UPDATE authsystem."Users"
   SET "LockoutEnabled" = true, "LockoutEnd" = 'infinity', "SecurityStamp" = gen_random_uuid()::text
   WHERE "Id" = '<the user id>';
   DELETE FROM authsystem."OpenIddictTokens" WHERE "Subject" = '<the user id>';
   DELETE FROM authsystem."OpenIddictAuthorizations" WHERE "Subject" = '<the user id>';
   ```

2. **Find the account's translator profile and the start of the damage:**

   ```sql
   SELECT "Id" FROM translation."Translators" WHERE "IdentityId" = '<the user id>';
   SELECT min("UpdatedAt") FROM translation."Translations"
   WHERE ("SubmittedById" = '<profile id>' OR ("ApprovedById" = '<profile id>' AND "Status" = 'Approved'))
     AND "UpdatedAt" > '<a time you trust>';
   ```

3. **Get the state before the damage into a scratch database.**
   - Inside the 7-day history window: Neon console → prod project → **Branches** → **Create branch**,
     from **a point in time** one minute before the damage, with a compute. `SCRATCH` is
     `host=<the branch's direct host> dbname=lotro_translation user=neondb_owner sslmode=require`
     (the branch has prod's password). Delete the branch when you are done.
   - Older: [fetch the last dump from before the damage](#fetch-and-decrypt-a-dump) and
     [restore it into a scratch database](#restore-into-a-scratch-database).
4. **Copy back.** After the [shell helpers](#shell-helpers-paste-first). `PROD` names the prod
   translation database's direct host (Neon console → **Connect**, not pooled), and `PGPASSWORD`
   holds `neondb_owner`'s password from the same page. The run prints every candidate row first. The update is one
   transaction and touches only rows that the account wrote or approved since `SINCE` and whose
   source is still what the copy holds: the English text and both argument columns, everything an
   import compares. An approval does not change `SubmittedById`, so the filter
   names `ApprovedById` too: an admin account that approved other people's drafts is covered, and
   the list marks such a row `undo approval, keep text`. Run
   the last block with `END=ROLLBACK` first: it prints the list and the `UPDATE` count and changes
   nothing. `UPDATE 0` while some rows say `restore` means the filter is wrong. When every row says
   `NOT IN THE COPY` and the copy step printed `COPY 0`, the export from the scratch database failed:
   check `SCRATCH`.

   ```bash
   read -rs PGPASSWORD && export PGPASSWORD    # paste it: not echoed, not in the shell history
   PROD='host=<direct host> dbname=lotro_translation user=neondb_owner sslmode=require'
   ACCOUNT='<profile id>'; SINCE='<start of the damage, UTC>'
   END=ROLLBACK      # the first run only prints; set END=COMMIT and run the last block again

   psql_in "$PROD" -At -c "\copy (SELECT \"Id\" FROM translation.\"Translations\" WHERE (\"SubmittedById\" = '$ACCOUNT' OR (\"ApprovedById\" = '$ACCOUNT' AND \"Status\" = 'Approved')) AND \"UpdatedAt\" >= '$SINCE') TO STDOUT" > damaged-ids.tsv

   { echo 'CREATE TEMP TABLE damaged (id uuid);'; echo '\copy damaged FROM STDIN'; cat damaged-ids.tsv; echo '\.'
     echo '\copy (SELECT t."Id", t."SourceText", t."ArgsOrder", t."ArgsId", t."PreviousSourceText", t."TranslatedText", t."Status", t."SubmittedById", t."ApprovedById" FROM translation."Translations" t JOIN damaged d ON d.id = t."Id") TO STDOUT'
   } | psql_in "$SCRATCH" -q -At > before.tsv

   { echo 'BEGIN;'
     echo 'CREATE TEMP TABLE damaged (id uuid PRIMARY KEY);'
     echo '\copy damaged FROM STDIN'; cat damaged-ids.tsv; echo '\.'
     echo 'CREATE TEMP TABLE restore_rows ("Id" uuid PRIMARY KEY, "SourceText" text, "ArgsOrder" text, "ArgsId" text, "PreviousSourceText" text, "TranslatedText" text, "Status" varchar(100), "SubmittedById" uuid, "ApprovedById" uuid);'
     echo '\copy restore_rows FROM STDIN'; cat before.tsv; echo '\.'
     echo "SELECT t.\"FileId\", t.\"GossipId\", t.\"TranslatedText\" AS now, r.\"TranslatedText\" AS in_the_copy,
             CASE WHEN r.\"Id\" IS NULL THEN 'NOT IN THE COPY: fix in the editor'
                  WHEN t.\"SourceText\" = r.\"SourceText\" AND t.\"ArgsOrder\" IS NOT DISTINCT FROM r.\"ArgsOrder\"
                       AND t.\"ArgsId\" IS NOT DISTINCT FROM r.\"ArgsId\"
                  THEN CASE WHEN t.\"SubmittedById\" = '$ACCOUNT' THEN 'restore' ELSE 'undo approval, keep text' END
                  WHEN t.\"SubmittedById\" = '$ACCOUNT' THEN 'SKIP: source changed'
                  ELSE 'SKIP: source changed, still approved by the account' END AS action
           FROM damaged d JOIN translation.\"Translations\" t ON t.\"Id\" = d.id
           LEFT JOIN restore_rows r ON r.\"Id\" = t.\"Id\" ORDER BY 1, 2;"
     # Every SET expression reads the row as it was before this UPDATE.
     echo "UPDATE translation.\"Translations\" t
           SET \"TranslatedText\" = CASE WHEN t.\"SubmittedById\" = '$ACCOUNT' THEN r.\"TranslatedText\" ELSE t.\"TranslatedText\" END,
               \"SubmittedById\" = CASE WHEN t.\"SubmittedById\" = '$ACCOUNT' THEN r.\"SubmittedById\" ELSE t.\"SubmittedById\" END,
               \"Status\" = CASE WHEN t.\"SubmittedById\" = '$ACCOUNT' OR t.\"TranslatedText\" IS NOT DISTINCT FROM r.\"TranslatedText\"
                               THEN r.\"Status\" ELSE 'Draft' END,
               \"PreviousSourceText\" = r.\"PreviousSourceText\", \"ApprovedById\" = r.\"ApprovedById\", \"UpdatedAt\" = now()
           FROM restore_rows r
           WHERE t.\"Id\" = r.\"Id\" AND t.\"SourceText\" = r.\"SourceText\"
             AND t.\"ArgsOrder\" IS NOT DISTINCT FROM r.\"ArgsOrder\" AND t.\"ArgsId\" IS NOT DISTINCT FROM r.\"ArgsId\"
             AND (t.\"SubmittedById\" = '$ACCOUNT' OR (t.\"ApprovedById\" = '$ACCOUNT' AND t.\"Status\" = 'Approved'))
             AND t.\"UpdatedAt\" >= '$SINCE';"
     echo "$END;"
   } | psql_in "$PROD"
   ```

   The account's own honest edits after `SINCE` are reverted too; the printed list shows them.
   Rows marked `undo approval, keep text` keep what their author wrote; read them and approve the
   good ones again. Rows marked `SKIP: source changed` need a person: an import changed their English or its
   arguments after the copy was made, so the old Polish may no longer fit. A row marked
   `SKIP: source changed, still approved by the account` holds someone else's text that the account
   approved after the import, so it is still in the game file under that approval: check it against
   the new English, then approve it yourself or save it unchanged to take it out. Rows marked `NOT IN THE COPY` were added
   by an import after the copy was made, so there is no earlier state to put back. Fix both kinds in
   the editor, and an approved one at once: editing an approved row turns it back into a draft,
   which leaves the game file.
5. **Rebuild the translation file.** A raw SQL update schedules no rebuild. Approve one row in the
   editor: any approval rebuilds the whole file for its language a few seconds later. When nothing
   waits for approval, open a restored row, save it unchanged (it becomes a draft) and approve it.
6. **Clean up:** `unset PGPASSWORD`, `rm -f damaged-ids.tsv before.tsv`, then delete the branch or
   [remove the scratch container and its volume](#restore-into-a-scratch-database). Unlock the
   account only once its owner has a new password.

### Neon is gone (whole-database restore)

The one case for a whole-database restore: the Neon project itself is lost.

1. Create a new project in the Launch organization (same region, Postgres 18) with the databases
   `lotro_translation` and `lotro_auth`, and set the history window to 7 days.
2. [Fetch and decrypt](#fetch-and-decrypt-a-dump) the newest dump of **both** databases (the same
   night), then restore each into the new project, auth first. With the new project's password in
   `PGPASSWORD` (`read -rs PGPASSWORD && export PGPASSWORD`):

   ```bash
   docker run --rm --network host -e PGPASSWORD -v "$PWD":/restore:ro "$PG_IMAGE" \
     pg_restore --no-owner --no-privileges --exit-on-error \
     -d 'host=<new direct host> dbname=lotro_auth user=neondb_owner sslmode=require' /restore/auth.dump
   ```

   and the same for `lotro_translation` with `translation.dump`.

   Then, as `deploy`, put the new host and password into both `ConnectionStrings__*` lines of
   `/opt/lotro/.env` and run the migrator, before the SQL of steps 3 and 4: when a migration was
   deployed after the dump, the restored schema is older than the running code, and the migrator
   brings it up to date (with nothing pending it does nothing). The migrator starts no app, so the
   finalizer stays off until step 5; do not deploy before then.

   ```bash
   cd /opt/lotro && export DOCKER_CONFIG=/nonexistent
   docker compose -f compose.hetzner.yaml --env-file .env run --rm --no-deps migrator
   ```
3. **Do every erasure again, and undo every later cancellation.** Do it before step 5: the finalizer
   runs as soon as auth-api starts on the new database. The dump is older than the loss. An account whose deletion was asked
   for before the dump comes back still waiting, and the finalizer erases it again on its next run.
   A deletion asked for after the dump is gone with the old database: Brevo's log of the deletion
   mails sent since the dump time (the ones with the link that cancels the deletion) names those
   accounts and the time of each request. Schedule each one again the way the request does, then
   the finalizer erases it once 14 days from the original request are over (on its next run, when
   they already are):

   ```sql
   UPDATE authsystem."Users"
   SET "DeletionScheduledAt" = '<time of the original request, UTC>', "LockoutEnabled" = true, "LockoutEnd" = 'infinity'
   WHERE "NormalizedEmail" = upper('<address from the mail>');
   ```

   The erasure then reaches the translation system as usual
   ([The account events parking lot](#the-account-events-parking-lot-tmsaccount-erased-dlq)).
   `UPDATE 0` means one of two things. The account was created after the dump: nothing of it came
   back, so there is nothing to erase. Or its address changed after the dump: Brevo's log has a mail
   to the earlier address from the same days (the e-mail change mails); use that address. The old
   cancel link in that person's mailbox no longer works, because the restored row holds an older
   security stamp. If they still want to cancel, clear what the link clears: set
   `"DeletionScheduledAt"`, `"LockoutEnd"` and `"PasswordHash"` to `NULL` and `"AccessFailedCount"`
   to `0`. Then tell them to set a new password with "forgot password": whoever asked for the
   deletion may know the old one.

   The other way round: a deletion asked for before the dump and cancelled after it comes back
   still waiting, and the finalizer would erase an account its owner chose to keep. Brevo's log of
   the mails that confirm a cancelled deletion (subject "Anulowano usunięcie konta"), sent since
   the dump time, names those accounts. An e-mail change undone by its link cancels a deletion too,
   and sends the same mail.
   When one address has both kinds of mail since the dump, the later one decides. For each
   cancellation, clear what the cancel clears:

   ```sql
   UPDATE authsystem."Users"
   SET "DeletionScheduledAt" = NULL, "LockoutEnd" = NULL, "PasswordHash" = NULL, "AccessFailedCount" = 0,
       "SecurityStamp" = gen_random_uuid()::text
   WHERE "NormalizedEmail" = upper('<address from the mail>') AND "DeletionScheduledAt" IS NOT NULL;
   ```

   A cancel clears the password and sends its owner to set a new one. A password set after the
   dump is lost with the rest of that day, so tell them to set it again with "forgot password".
4. **Sign everyone out.** The auth dump brings back sign-in sessions and refresh tokens that were
   ended after the dump. Delete every row of `authsystem."SignInSessions"` and
   `authsystem."OpenIddictTokens"`; everyone signs in again.
5. Start the APIs on the new database, then run the [smoke test](#post-deploy-smoke-test):

   ```bash
   cd /opt/lotro && export DOCKER_CONFIG=/nonexistent
   docker compose -f compose.hetzner.yaml --env-file .env up -d --no-deps --force-recreate auth-api tms-api
   ```
6. Point the `production` environment's `NEON_PROJECT_ID` variable and `NEON_API_KEY` secret at the
   new project (MIGR-04).

Everything written after that dump is lost: at most one day of work.

## Message broker (RabbitMQ)

Each box runs a **single-node RabbitMQ container** in the stack (`rabbitmq` in
`compose.hetzner.yaml`: pinned image, pinned `hostname`, named `rabbitmq-data` volume; ADRs
0035–0037). **auth-api** publishes committed outbox rows: e-mail work to the `lotro.emails`
exchange, which its own e-mail consumer reads from `emails.send`, and the `AccountErased` event to the
`lotro.accounts` exchange (ADR-0065). **tms-api** reads that event from its own queue
`tms.account-erased` and takes the person's name and address off the translator profile. Both use the
one broker user `rabbitmq`; #1086 tracks a least-privilege user for tms-api.

Each app **declares what it needs when it opens a channel** (exchanges, quorum queues, bindings, the
dead-letter wiring), so a fresh broker needs no manual setup. auth-api declares `lotro.accounts`;
tms-api only checks that it exists, so the tms-api consumer keeps retrying until auth-api has started
once. The one thing no app declares, the 7-day expiry of parked e-mail messages, is a broker policy
that `deploy.sh` sets on every deploy (ADR-0036 §6).

The broker is a **soft dependency** on purpose:

- **auth-api boots and serves with the broker down**: there is no `depends_on` edge. The publisher
  connects lazily, the consumer retries with growing backoff, and committed outbox rows wait
  (ADR-0035's safety sweep is the ceiling). An outage delays confirmation e-mails; it never blocks
  login or token issuance.
- It shows up as the **`rabbitmq` check on auth's deep `/health`** (not on `/health/ready`, for the
  same reason as SMTP). The daily health ping is what tells you the container died between deploys.

### Management UI — over an ssh tunnel only

The UI is published on the **box loopback** only, never on the internet:

```bash
ssh -L 15672:localhost:15672 deploy@<box-ip>
# then http://localhost:15672 — user `rabbitmq`, password = RABBITMQ_PASSWORD from the box .env
```

### The dead-letter parking lot (`emails.send.dlq`)

Messages land there in two ways only (ADR-0036): **poison** (unreadable payload or unusable message
id, rejected on first sight) and **exhausted** (`x-delivery-limit` = 5 redeliveries spent, e.g. an
SMTP outage the backoff could not outlast). Nothing consumes the queue; it waits for a human. A parked
message usually means a user never got a confirmation e-mail, so diagnose from the payload's
`IdentityUserId` plus the matching outbox row's error.

**Replay = ack-and-republish, NEVER reject-requeue** (ADR-0036 §5). The parking lot is a quorum queue
with the *default* delivery limit (20) and no DLX of its own, so a reject-requeue loop **silently
drops** the parked message. In the management UI:

1. **Inspect** without consuming: `emails.send.dlq` → *Get messages* with ack mode *Nack message
   requeue true* (a nack is an "explicit return" and does not count a delivery; a reject does).
2. **Replay**: *Publish message* on the **`lotro.emails`** exchange with the message's preserved
   routing key (e.g. `email.confirmation`), its original **`message_id` and `type` properties** and
   body. The consumer picks its processor by `type` (ADR-0038), so a replay without it is parked
   again at once. The inbox deduplicates on the message id (ADR-0037), so replaying an id that was
   already processed is a safe no-op.
3. Only after the replay has visibly worked (e-mail sent / inbox row present): remove the parked
   copy with *Get messages*, ack mode *Automatic ack*.

**A parked message expires 7 days after it parks** (ADR-0036 §6, #1085), so replay it within that
week or it is gone for good. The limit exists because an e-mail change message holds the person's old
and new address, and an account erasure cannot reach a queue. It is the RabbitMQ **operator** policy
`emails-send-dlq-ttl` (pattern `^emails\.send\.dlq$`, `message-ttl` 604800000 ms; in the management UI
under *Admin → Policies → Operator policies*). `deploy.sh` sets it on every deploy, on both boxes, so
nobody sets it by hand. Check that it is on:

```bash
docker compose -f compose.hetzner.yaml exec -T --user rabbitmq rabbitmq rabbitmqctl list_queues name operator_policy messages
# emails.send.dlq  emails-send-dlq-ttl  0   <- the operator_policy column must name it
```

Run `rabbitmqctl` as the `rabbitmq` user, as above. On a broker whose volume is new, a root CLI can
write the Erlang cookie before the server does, and the server then stops at boot.

- **A broker you start by hand has no policy.** `deploy.sh` is the only thing that sets it, so a
  broker whose volume is new (a rebuilt box, a `down -v`, a deleted `rabbitmq-data`) gets it only when
  `deploy.sh` runs. Bring such a broker up with `deploy.sh`, never with a plain `docker compose up -d`
  ([bring-up](#reprovisioning-a-box), [disaster recovery](#disaster-recovery)), and check it with the
  command above before anything can park.
- **The deploy warned that the policy did not land.** The broker did not answer in time. A broker that
  had the policy keeps it, so this matters only for a new broker. Deploy again once the broker is
  healthy, then check `emails.send.dlq`: a message that parked while the policy was missing never
  expires.
- **A message with no expiry blocks the others.** A quorum queue gives a message its expiry when the
  message parks, and it drops expired messages only from its head. So a message that parked before
  the policy existed stays for good, and so does every message behind it. Replay or remove such a
  message by hand.
- **A user policy cannot switch it off.** An operator policy applies next to whatever user policy
  matches the queue, and the lower `message-ttl` wins, so a policy added in the management UI can
  shorten the limit but never lengthen or remove it. Change or remove the operator policy only through
  `deploy.sh` and ADR-0036.

### The account events parking lot (`tms.account-erased.dlq`)

Only a message tms-api can never handle lands here: an unknown type, an unreadable payload or a
command the handler refuses (parked on first sight), or one that failed five times in a row for a
reason other than the database, which means a bug. A database error never parks a message, whatever
its cause (an outage, a rotated password, a lost grant): the consumer returns it with a nack, which
does not count against the delivery limit, and tries again at most every 15 minutes until the
database answers (ADR-0065). A parked message means a person's name may still be on a translator
profile, so look at it the same day.

Replay works as for `emails.send.dlq`, on the **`lotro.accounts`** exchange with the routing key
`account.erased` and the `type` property `AccountErased`. Erasing a profile twice is harmless, so a
replay never needs a duplicate check. This parking lot has **no time limit, on purpose**: a parked
message is erasure work that must wait for a person. Never give it a policy (user or operator) with
a `message-ttl`, and never widen the e-mail expiry pattern to reach it (ADR-0036 §6).

Changing the arguments of `tms.account-erased` follows the same rules as `emails.send` (see
[Traps](#traps)), with tms-api in auth-api's place. A message lost there is worse than a lost e-mail:
the AuthSystem never sends an erasure again once it was sent, so the person's name stays on the
profile for good.

### Rolling back past ADR-0065

A release from before ADR-0065 cannot route `AccountErased`. Its relay marks such a row failed
("No routing key is mapped…", an Error line), moves on to the other rows and keeps the row for later.
Nothing is lost: the row goes out once a release that routes it runs again. Two things to know: the
first run of the deletion finalizer after ADR-0065 writes one such row for every account erased before
it, and a run of 100 or more failing rows at the head of the outbox holds newer e-mail back on the old
release. So roll forward rather than back once those rows exist, or roll back only long enough to fix
forward.

### Rolling back past #860

The same holds for `RegistrationAttemptedOnExistingAccount`, the notice to the owner of an address
someone tried to register with. A release from before #860 cannot route it: its relay marks the row
failed with an Error line and keeps it, and its consumer sends a message already on the queue to the
dead-letter queue as an unknown type. Nothing else breaks. The notices go out late, or from the
dead-letter queue by hand, once a release that routes them runs again.

### Traps

- **`RABBITMQ_PASSWORD` is required.** Compose interpolation fails without it
  (`${RABBITMQ_PASSWORD:?…}`) in `deploy.sh`'s first step, which stops the deploy while the old release
  keeps serving.
- **`RABBITMQ_DEFAULT_PASS` applies on first boot only** (empty data volume). Rotate it in lockstep
  with `rabbitmqctl change_password`, then restart **both** apps (commands: the
  [secrets table](#secret-material--source-of-truth-and-how-to-rotate)).
- **Queue arguments cannot change.** Declaring `emails.send` again with different `x-*` arguments
  fails the channel with `PRECONDITION_FAILED` (ADR-0036). Changing them on a live box means draining
  and deleting the queue: treat it as a small migration, not a config tweak.
  - Delete it only while the release with the new arguments is running, or while auth-api is
    stopped. The running auth-api declares the queue again with **its own** arguments and attaches,
    usually within a minute and with no restart (#1088), so a delete under the old release brings the
    old arguments straight back.
  - The release with the new arguments cannot read the old queue, so check in the management UI that
    it holds no messages right before the delete: anything still in it is lost.
  - A message that is waiting out an outage at that moment (SMTP for e-mail, the database for an
    erasure) holds the attach back until its pause ends, about 15 minutes at most.
- **Never remove the `hostname:` pin** on the service. RabbitMQ keys its on-disk state by node name
  (`rabbit@<hostname>`); a recreation without the pin boots a fresh node beside the old data and
  orphans the volume's quorum queues, parked dead letters included.

## Post-deploy smoke test

One command that gives a green/red signal that a deployed environment came up correctly, without
manual clicking. Run it as the final [bring-up](#bringing-the-stack-up) step and after every later
deploy (CD runs it for you). `scripts/smoke.sh` (with a `scripts/smoke.ps1` twin) takes the three
base URLs + the OpenIddict API client secret and runs the seven legs that actually break on a deploy:

| # | Check | Pass condition |
|---|---|---|
| 1 | **Health** | `GET {auth}/health/ready` = 200, `GET {tms}/health/ready` = 200, `GET {frontend}/` = 2xx/3xx |
| 2 | **Frontend assets + CSP** | `GET {frontend}/` references a fingerprinted `_framework/blazor.web.<hash>.js` that serves 200, and the page has no inline `<script>` its own `script-src` would block (#414, #670) |
| 3 | **OIDC token** | `POST {auth}/connect/token` (client_credentials) = 200 + an `access_token` |
| 4 | **Token accepted by tms** | anonymous `GET {tms}/api/v1/game-versions` = **401**; the same call **with** the bearer token is **200 or 403** (401 = token rejected) |
| 5 | **File distribution** | `GET {tms}/api/v1/translation-files/{lang}` = 200 + `ETag`, then a re-GET with `If-None-Match` = 304 |
| 6 | **Auth pages: headers + CSP** | `GET {auth}/Account/Login` (a form) and `/Account/ConfirmEmail` (no form) each send `X-Frame-Options: DENY` with `frame-ancestors 'none'`, `nosniff` and a `Referrer-Policy` of `no-referrer`, `same-origin`, `strict-origin` or `strict-origin-when-cross-origin`; `style-src` admits `'self'`, no inline `<style>` (the styles live in `/account.css`), no inline `<script>`, no `'unsafe-inline'`; `GET {auth}/account.css` and `/error-page.css` = 200 `text/css` (#693, #833) |
| 7 | **Discovery links** | `GET {tms}/` and `GET {auth}/` with the HATEOAS `Accept` header (`application/vnd.dev-lotrokoniecdev.hateoas.json`) = 200 with at least one link, and every `href` has the scheme, host and port of the configured URL (#898) |

It prints a `✓`/`✗`/`⚠` per check and **exits 1 on any failure** (a usage or config problem exits 2).
Two behaviours are worth knowing before you read a result:

- **Leg 4 passes on 403.** The only non-interactive OIDC grant in a deployed environment is
  **client credentials**. Such a token carries **no user role**, and every TMS endpoint is role-gated,
  so a *validated* token gets **403 Forbidden** (200 also passes). The check proves the token was
  **accepted** (it got past authentication), and the anonymous 401 proves the endpoint is protected.
  **A 401 with a valid token is the real red flag**: tms rejected it, almost always because of an
  issuer / audience / JWKS mismatch (see [Consistency rules that bite](#consistency-rules-that-bite),
  rules 1–2) or a **stale `SMOKE_CLIENT_SECRET`**.
- **Leg 5 warns (does not fail) on 404.** A freshly deployed environment with nothing imported yet has
  no translation file, so the endpoint returns 404: it is up, there is just nothing to distribute yet.

### Running it

```bash
# A real environment (publicly trusted Let's Encrypt cert — no --insecure needed):
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
`--insecure`/`SMOKE_INSECURE=1`, `--require-csp`/`SMOKE_REQUIRE_CSP=1`). `bash scripts/smoke.sh --help`
prints the full reference.

`--require-csp` makes a frontend or auth page with **no** `Content-Security-Policy` header a failure
instead of a warning. Pass it against any deployed environment; CD and the `Smoke test` workflow
both do. Leave it off only for a local Development stack, which skips the security-headers middleware
on purpose and would otherwise always report red.

The flag also means an image from before the CSP legs cannot pass: an auth image older than #693
fails leg 6, a frontend image older than #670 fails leg 2. So a deliberate CD deploy of such an old
`image_tag` goes red, and the rollback step puts the newer tag back. Pick a rollback target from #693
on.

**In CI:** `deploy.yml` runs it against the environment's public origins after `up -d`; a red smoke
triggers the automatic rollback. The [`Smoke test`](../../.github/workflows/smoke.yml) workflow runs
the same check **on demand** against one environment, with that environment's own URLs and
`SMOKE_CLIENT_SECRET`, the values CD uses:

```bash
gh workflow run smoke.yml -f environment=staging       # or production
```

It creates **no deployment record** (`deployment: false`), so `image-scan.yml` and the N-1 promotion
gate still see the last real deploy. Do not start it while a deploy runs on that box: `up -d`
recreates the containers, and a red result in those seconds says nothing about the release.

A `production` run waits at the same reviewer gate as a promotion: approving it (*Review
deployments*) runs only the smoke and deploys nothing. A run started from any branch but `main` is
refused before it starts, on both environments, because each one allows only `main` (#1336). So a
change to `smoke.yml` is proven after the merge, or with a temporary rule on `staging`
([One-time setup per environment](#one-time-setup-per-environment) → "Proving a change to a job that
names an environment"). To drop a waiting run, choose *Reject* in the same dialog. The janitor clears only `cd.yml` runs, so a run nobody approves or rejects stays `waiting`;
*Cancel* does not work on a gated run (see
[Continuous deployment](#continuous-deployment-cd-over-ssh)), so end it with
`gh api -X POST repos/koniecdev/LotroKoniecDev/actions/runs/<run-id>/force-cancel`.

> **`GET / -> 200` never proves the frontend works.** A `[StreamRendering]` page returns 200 with its
> spinner frame before it fetches anything. Leg 2's **fingerprint** check is the signature of a
> healthy image: `@Assets[]` renders `_framework/blazor.web.<hash>.js` only when `MapStaticAssets`
> resolved its manifest. Leg 2 also catches an inline `<script>` the page's own CSP blocks, which
> shows up nowhere but the browser console (#670). Leg 6 does the same for the auth pages, which take
> their styles from `/account.css` and must serve no inline `<style>` or `<script>` (#693, #833).
>
> **A green API health check does not prove its links work either.** The APIs build each link from
> the address the request came in on, which Caddy passes on in the forwarded headers. A change to
> Caddy, to the forwarded-headers setup or to a domain variable can turn those links into `http://`,
> an internal host name or another port. The website and the CLI refuse to follow a link off the
> configured origin and follow no redirect (#611, #830), so every page that follows a link would
> break while the health checks stay green. Leg 7 reads both discovery roots and catches that (#898).
> ADR-0041 allows one link off the origin: after a service split, the root links to the root of the
> service that left. The change that adds such a link must also allow it in leg 7, or every deploy
> fails there and rolls back.

## Observability & monitoring

**What exists:**

- **Structured logs**: every app logs JSON to stdout (Serilog). Read them on the box:
  `docker compose -f compose.hetzner.yaml logs -f <service>`.
- **Health endpoints**: deep `/health` (DB, plus SMTP and the broker on auth; only with the health
  check key, else 404 — ADR-0058), `/health/live`, `/health/ready`. The probes run no DB check
  (ADR-0025), so container probes cannot keep the scale-to-zero Neon compute awake: **Neon still
  suspends**. The deep answer names each check and its status only; the reason a check failed is in
  the app's log at Error level.
- **The daily health ping**: [`.github/workflows/health-ping.yml`](../../.github/workflows/health-ping.yml)
  probes the prod origins once a day (06:40 UTC) on the **deep** `/health`, so it is the one check
  that proves the database is reachable. It runs on GitHub, so it is also the off-box backstop for
  this stack's own failure: a dead staging box means no dashboards, no alert evaluation and no alert
  delivery, alerts about prod included. Keep both. It sends the key from the `PROD_HEALTH_CHECK_KEY`
  repository secret; a red run with **HTTP 404** means that secret and the prod box's
  `HEALTH_CHECK_KEY` differ, not that the site is down. A failed run e-mails the last committer of
  that file. Trigger it on demand: `gh workflow run health-ping.yml`.
- **The daily image scan**: [`.github/workflows/image-scan.yml`](../../.github/workflows/image-scan.yml)
  scans the four images that staging and prod run, once a day (05:10 UTC), with the same Trivy gate
  as `cd.yml` (#1326). A vulnerability published after the image was built shows up here, not at the
  next build. It finds each environment's image from its newest successful deployment record, so
  after a deploy or rollback by hand on the box it scans the recorded image, not the running one. A
  red run usually means the base image pins need to move (#1324); when only prod is red, the fix may
  already be on staging and need a promotion. A failed run e-mails the user who last changed its
  cron line. Trigger it on demand: `gh workflow run image-scan.yml`.
- **Post-deploy smoke**: every CD rollout, with automatic rollback on red.
- **Grafana**: logs, metrics and traces for **both projects** from both boxes, on the stack described
  below (epic #707).

### The observability stack

Grafana, Loki, Prometheus, Tempo and Alloy, deployed from `compose.observability.yaml` as its **own
compose project** in `/opt/obs`. It stays outside CD, because CD overwrites `compose.hetzner.yaml`
and recreates every service in it on each rollout, and monitoring must not share the lifetime of the
thing it monitors (ADR-0050 §2).

**Every command against this project carries `-f compose.observability.yaml`.** The file is not one
of the names compose looks for on its own, so a bare `docker compose …` in `/opt/obs` exits with
*"no configuration file provided: not found"* and does nothing. In a chain (`down && up -d`) the rest
then never runs. Do not drop the flag, and do not add a `compose.yaml` symlink either: the project
name comes from the `name: obs` key in this file, and a second entry point is one more thing to keep
in step.

| | |
|---|---|
| Backend box | **`lotro-staging`**, on purpose. A backend on the same box as the workload it observes dies in the same outage and takes the evidence with it; on staging, a *prod* outage leaves the dashboards up and prod's last minutes readable (ADR-0050 §1) |
| Agent | Alloy, on every box. Tails the Docker socket, accepts OTLP, scrapes the host and containers |
| Retention | Loki 14 d · Prometheus 15 d **and** 4 GB (whichever comes first, size enforced by the store) · Tempo 14 d |
| Memory | a hard `mem_limit` on every component, **1856 MiB** of ceilings against ~810 MiB measured (ADR-0051 and its #782 amendment: Tempo 384 MiB and Grafana 512 MiB, because a measurement with nobody querying got Tempo OOM-killed by one person browsing traces) |
| Reaching it | **no public hostname.** `ssh -L 3000:127.0.0.1:3000 lotro-staging` → `http://localhost:3000`. Password: `ssh lotro-staging 'grep GF_SECURITY_ADMIN_PASSWORD /opt/obs/.env'` |
| The other box | pushes over **`obs.lotro-translator.pl`**: a Caddy vhost on the backend box with three ingest paths (the Loki push, the Prometheus remote-write, the OTLP trace gRPC method), each admitted only from the prod box's public `/32` **and** with one basic-auth credential. Everything else answers 404, and Grafana is not behind it (ADR-0050 §4, #755). Agent side: `alloy/ship.agent.alloy` (TLS + the credential) versus `alloy/ship.backend.alloy` (the containers next door), picked by `COMPOSE_PROFILES` |
| Dashboards & alerts | provisioned as code: ours from `.docker/observability/`, TheKittySaver's from its own repo into `/opt/obs/grafana/dashboards/tks/` |

**Deploying a change** to the configs, dashboards or alert rules, as `deploy` (staging box shown;
the prod box takes the same `rsync` + `scp`):

```bash
rsync -az .docker/observability/ lotro-staging:/opt/obs/ && scp compose.observability.yaml lotro-staging:/opt/obs/
ssh lotro-staging 'cd /opt/obs && docker compose -f compose.observability.yaml up -d --force-recreate && docker compose -f compose.observability.yaml ps'
```

`--force-recreate` is needed: each config file is a bind mount, `rsync` replaces the file, and
compose does not look inside a mounted file, so a plain `up -d` leaves every container on its old
config (the same reason `deploy.sh` reloads Caddy). It costs about a minute of telemetry.

This carries our half only. TheKittySaver's dashboard and alert rules live in its own repo and land
in the same tree, so run its two lines from a TheKittySaver checkout in the same sitting, or the box
keeps whatever version it had (`docs/observability/README.md` there):

```bash
rsync -az docs/observability/dashboards/ lotro-staging:/opt/obs/grafana/dashboards/tks/
rsync -az docs/observability/alerting/  lotro-staging:/opt/obs/grafana/provisioning/alerting/
```

Run `docker restart obs-grafana` after TheKittySaver's two lines, so Grafana reads its new provisioning.

**Nothing is published to the internet by the observability project itself.** Grafana binds to
loopback. Alloy's OTLP receiver binds the two docker **bridge gateways** (`10.60.0.1` ours,
`10.61.0.1` the guest stack's), never `0.0.0.0`, because a port published on `0.0.0.0` bypasses ufw
(see [Gotchas](#gotchas)). The `DOCKER-USER` leg would still drop it, but binding the gateways stays
the rule: that chain is a second wall, not the first. Loki, Prometheus and Tempo publish nothing. No observability container
joins `default` or `tks`: the agent reads the Docker socket, containerd and `/proc`, so it never needs
a route to an app container and does not have one (ADR-0050 §3).

**Alerts (#713).** Six are project-scoped (crash loop, 5xx rate, error-log spike, the two
translation save rules of #687, and the nightly backup; TheKittySaver ships its own set) and eight
are fleet-scoped, because box saturation belongs to neither project. This repo ships fourteen;
TheKittySaver's rules live in its own repo. All of them deliver to **one**
contact point, `owner-email`, over the Brevo relay the apps already use. There is no second channel.

| Folder | Rules |
|---|---|
| LotroKoniecDev | crash loop · 5xx above 5% · error-level log spike · a translator account reached its save budget · more than 100 translations overwritten in 24 hours · the nightly database backup did not land ([Database backups](#database-backups)) |
| Fleet | memory > 85% · swapping hard or waiting for memory · swap > 70% full · CPU > 85% · disk < 20% · disk < 10% · a box has gone silent · a container above 85% of its own `mem_limit` |
| TheKittySaver | its own set (that repo's #511 and later additions) |

**The two translation save rules (#687, ADR-0072).** Both read tms-api's log lines, so they work only
while the box ships its logs to Loki, and only after `/opt/obs` is synced (above). Each matches the
template text in the JSON line (`"@mt":"Translation save budget:` and `"@mt":"Translation
overwritten:`), never the bare text, because a caller's header or request path also lands in tms-api's
log lines. A unit test (`TranslationSaveBudgetLogRuleTests`) ties the rule file to the C# text; when the
text changes, the rule file changes with it and needs the sync too.

- **A translator account reached its save budget** (`lotro-translation-save-budget`, warning): one alert
  per account, budget (`Saves` or `Overwrites`) and outcome (`AlertThreshold` or `Refused`). The first
  mail comes within about 90 seconds, or up to 5 minutes when another of its alerts already fired. A
  "resolved" mail follows about an hour after the account's last budget line. That does not mean the
  account is under its limit again: the budget window is 24 hours by default. A new mail follows when it
  saves above the threshold again. The thresholds and limits are the `TranslationSaveBudget__*`
  settings in the tms-api table above. The mail names the account by its translator id; its name is
  `SELECT "DisplayName", "Email" FROM translation."Translators" WHERE "Id" = '<the id>';` on the TMS
  database.
- **More than 100 translations overwritten in 24 hours** (`lotro-translation-overwrites-total`,
  warning): saves that replaced Polish already on a row, from all accounts together, admins included.
  It blocks nothing. One mail when the total passes 100, a repeat every 6 hours while it stays above,
  and a resolved mail when it drops. The 100 and the 24 hours are in the rule file, not in `.env`.

Both are best effort: tms-api logs every request before its rate limiter, so a flood of requests can
delay or drop lines on the way to Loki. The block itself is in the database and does not depend on the
log.

**What the two swap rules measure (#1084).** How full swap is says little on its own. Linux moves
memory that nothing has used for a while into swap and leaves it there until something reads it
again, so on a calm box swap fills slowly over weeks and almost never empties. A rule on swap use
alone mailed every 6 hours about a healthy staging box.

- **Swapping hard or waiting for memory** (`fleet-swap-in-use`, warning) fires when either holds
  for 5 minutes: more than 350 pages a second (about 1.4 MiB/s) moved in and out of swap on average
  over the last 30 minutes (`node_vmstat_pswpin` + `node_vmstat_pswpout`), or at least one program
  waiting for memory during more than 10% of the last 5 minutes
  (`node_pressure_memory_waiting_seconds_total`, the kernel's "some" memory pressure). The window is
  30 minutes because real squeezes come as bursts of 5 to 15 minutes, so a long `for:` on a 5-minute
  rate never fires. Replayed against two weeks of both boxes' history, it fired only on the one real
  squeeze, the night of 2026-10-02. The 10% stall line is there for memory waits that do not show up
  as swap traffic.
- **Swap nearly full** (`fleet-swap-nearly-full`, warning) is the late guard: more than 70% for
  15 minutes. That leaves about 600 MiB of swap, and one ordinary CI job pushed 650 MiB into swap in
  five minutes on 2026-10-09. Above the line, the next such burst can fill swap, and then the kernel
  kills a container.

To find what holds the swap, list `VmSwap` per process (`grep VmSwap /proc/*/status`) and map a pid
to its container with `/proc/<pid>/cgroup`. A host service outside Docker, such as a CI runner, does
not show up in the container memory panels. If the processes together hold much less than the swap in
use, the rest is shared memory, most often files in `/tmp`.

**`/tmp` is memory on these boxes.** On Ubuntu 26.04 it is a tmpfs (1.9 GiB), so a file left there
counts as memory and can end up in swap. `systemd-tmpfiles` deletes old files only after 10 days
(`Q /tmp 1777 root root 10d`); on 2026-10-08 staging held 168 MiB of stale files there. Check
`du -sh /tmp` before you blame the apps.

To check a threshold against history, query the staging Prometheus over read-only ssh. It holds both
boxes, with labels `box="lotro-staging"` and `box="lotro-prod"`, for 15 days. URL-encode the query on
your machine first: a raw `+` arrives as a space, and a `"` breaks the ssh quoting. Prometheus returns
at most 11,000 points per series, so 7 days fit at `step=60` and 15 days need `step=120`:

```bash
q='max by (box) (rate(node_vmstat_pswpin[30m]) + rate(node_vmstat_pswpout[30m]))'
enc=$(python3 -c 'import sys, urllib.parse; print(urllib.parse.quote(sys.argv[1], safe=""))' "$q")
end=$(date +%s); start=$((end - 7 * 86400))
ssh lotro-staging "docker exec obs-prometheus wget -qO- 'http://localhost:9090/api/v1/query_range?query=$enc&start=$start&end=$end&step=60'"
```

**Turning telemetry on for a box** is an `.env` edit and a restart, never an image rebuild:

```bash
# /opt/lotro/.env
OTEL_EXPORTER_OTLP_ENDPOINT=http://10.60.0.1:4317
OTEL_LOGS_EXPORTER=                  # keep EMPTY
OTEL_DEPLOYMENT_ENVIRONMENT=staging  # or production
docker compose -f compose.hetzner.yaml up -d
```

The protocol is fixed to `grpc` in `compose.hetzner.yaml`, so the box `.env` does not set it.

`OTEL_LOGS_EXPORTER` stays empty because **logs have one path only**: Alloy tailing stdout, which is
the larger set (it catches a crash before the logging pipeline is up, and it covers Caddy, RabbitMQ
and the migrator, which speak no OTLP). Alloy drops OTLP log records, so a box that sets it by mistake
still cannot produce duplicates (ADR-0051).

`OTEL_DEPLOYMENT_ENVIRONMENT` is the **only** thing that separates the two environments in Grafana,
because both boxes run `ASPNETCORE_ENVIRONMENT=Production`. Set it wrong and every metric and trace
that box emits is filed under the other environment.

### Bringing up the prod agent (the agent-only role)

The route and the role came with #755 (the TKS half is koniecdev/TheKittySaver#599), and prod has
shipped telemetry since 2026-09-09. These are the steps to repeat when the prod box is rebuilt or the
credential is rotated. Every step is idempotent, none touches a running app container, and only
step 4 interrupts anything (telemetry, for about a minute).

1. **DNS**: registrar panel → `A obs.lotro-translator.pl → 91.98.74.228` (the **staging** box,
   where the backend runs), TTL 300. ACME needs it to resolve before the vhost's first request, and
   `remote_ip` needs the prod box's egress to still be `167.233.159.221`
   (`ssh lotro-prod curl -4s https://ifconfig.me`). **An `A` record and no `AAAA`**: the gate is an
   IPv4 `/32`, and with an `AAAA` the agent's resolver may prefer IPv6, whose source address Caddy
   sees as a bridge gateway, so every push would get a 404 for a reason the diagnosis below does not
   name. No vhost on this fleet has an `AAAA` today.

   **Do this step FIRST, and do not let anything look the name up before it exists.** A failed lookup
   is cached too, for the zone's negative TTL (`min(SOA TTL, SOA minimum)`, three hours here), and
   Hetzner's recursive resolvers (`185.12.64.1/2` and their v6 twins, all the boxes use) hold it, so
   `resolvectl flush-caches` on the box does nothing. On 2026-09-09 that kept the prod box from
   resolving the name for 35 minutes after the record went in. `dig @185.12.64.1 obs.<domain>` shows
   the time left as the TTL on the SOA in the authority section. Waiting is the only clean fix; the
   agent retries on its own.
2. **Mint the push credential.** One password in two forms, and the two never travel together:
   ```bash
   openssl rand -base64 24                                  # the password → prod /opt/obs/.env (step 5)
   docker run --rm -it caddy:2-alpine caddy hash-password   # prompts for it; the hash → staging /opt/lotro/.env
   ```
3. **Staging box, `/opt/lotro/.env`** (as `deploy`; the block is in `.env.hetzner.example`):
   ```
   DOMAIN_OBS=obs.lotro-translator.pl
   OBS_ALLOWED_PUSH_IP=167.233.159.221/32
   OBS_PUSH_USER=prod-agent
   OBS_PUSH_PASSWORD_HASH='$2a$14$…'      # SINGLE quotes. Bare or "double" both corrupt it:
                                          # compose expands the salt after the third $ to nothing,
                                          # and caddy validate accepts the result — see the probe below
   ```
   Then a rollout to staging: any merge, or re-apply the current tag by hand:
   `sudo -u deploy env IMAGE_TAG=$(sed -n 's/^IMAGE_TAG=//p' /opt/lotro/.env) bash /opt/lotro/deploy.sh`.
   It creates `lotro-staging_obs`, attaches Caddy, validates and reloads the Caddyfile, and ACME
   issues the certificate. Check from anywhere: `curl -sI https://obs.lotro-translator.pl/` → `404`
   with `x-robots-tag: noindex, nofollow`, and `curl -sI https://obs.lotro-translator.pl/loki/api/v1/push`
   → **also 404**: from a source the vhost does not admit, the push paths do not exist.

   Then prove the credential landed, **from the prod box** (the only source the vhost admits), before
   any agent exists there. This is the check that catches a hash broken by the wrong quoting, which
   nothing in the rollout catches:

   ```bash
   # -u with no password makes curl prompt for it: it never reaches argv or shell history.
   ssh -t lotro-prod 'curl -s -o /dev/null -w "%{http_code}\n" -u prod-agent \
     -X POST -H "Content-Type: application/json" --data-binary "{}" \
     https://obs.lotro-translator.pl/loki/api/v1/push'
   ```

   **422** = route and credential both good (Loki rejecting an empty push means it received it).
   **401** = the credential does not match, most likely because quoting corrupted the hash. **404** =
   this source address is not the one `OBS_ALLOWED_PUSH_IP` admits.

   That probe needs the route, so it needs step 1's DNS and the certificate ACME issues from it.
   **When the record is not there yet** (step 1 is the one you may be waiting on someone else for),
   the credential can still be proven on its own: against the hash the running Caddy actually holds,
   with the same bcrypt check, on loopback:

   ```bash
   # On the prod box, with the plaintext in a 600 file there and the DEPLOYED hash read back out
   # of the staging Caddy (never out of the .env — the point is to test what compose rendered).
   # The throwaway Caddy runs the live proxy's own image, so it never pulls a floating tag (#988).
   ssh lotro-staging 'docker exec lotro-staging-caddy-1 printenv OBS_PUSH_PASSWORD_HASH' \
     | ssh lotro-prod 'umask 077; tr -d "\n" > /root/.obs-push-hash.deployed'

   ssh lotro-prod 'd=$(mktemp -d) || exit 1; printf ":8899 {\n\tbasic_auth {\n\t\tprod-agent %s\n\t}\n\trespond \"ok\" 200\n}\n" \
       "$(cat /root/.obs-push-hash.deployed)" > "$d/Caddyfile"; chmod 644 "$d/Caddyfile"
     docker run -d --rm --name obs-cred-probe -p 127.0.0.1:8899:8899 \
       -v "$d/Caddyfile:/etc/caddy/Caddyfile:ro" "$(docker inspect -f "{{.Config.Image}}" lotro-prod-caddy-1)" >/dev/null; sleep 2
     printf "machine 127.0.0.1 login prod-agent password %s\n" "$(cat /root/.obs-push-password)" > "$d/netrc"
     chmod 600 "$d/netrc"
     curl -s -o /dev/null -w "%{http_code}\n" --netrc-file "$d/netrc" http://127.0.0.1:8899/
     docker rm -f obs-cred-probe >/dev/null; rm -rf "$d"'
   ```

   **200** = the pair matches. **401** = it does not, and the hash is the usual reason. Once this
   passes, only the route itself can still be wrong. The netrc file keeps the password off argv, where `ps` and the shell history would both see
   it. Publishing on `127.0.0.1` keeps the throwaway Caddy off the box's public interface, where a
   bare `-p 8899:8899` would get past ufw and be held back only by the `DOCKER-USER` leg (see
   [Gotchas](#gotchas)).

4. **Staging box, `/opt/obs` on the `lotro-staging_obs` network**: about a minute without
   telemetry; the volumes, and the history in them, survive a `down` without `-v`. **Check that the
   network exists before you take the stack down**: step 3's rollout creates it, and a `down` followed
   by an `up -d` that cannot find it leaves the whole backend, and all alerting for both environments,
   off until someone notices.
   ```bash
   rsync -az .docker/observability/ lotro-staging:/opt/obs/ && scp compose.observability.yaml lotro-staging:/opt/obs/
   ssh lotro-staging 'docker network inspect lotro-staging_obs >/dev/null'   # step 3 landed? if not, STOP
   ssh lotro-staging 'cd /opt/obs && grep -q "^OBS_NETWORK=" .env || printf "OBS_NETWORK=lotro-staging_obs\n" >> .env'
   ssh lotro-staging 'cd /opt/obs && docker compose -f compose.observability.yaml down && docker compose -f compose.observability.yaml up -d && docker compose -f compose.observability.yaml ps'
   ```
   The network check is a separate command on purpose: on one `&&` chain, a failure anywhere after
   the `down` would leave the backend stopped and the rest not run. Run TheKittySaver's two `rsync`
   lines from [Deploying a change](#the-observability-stack) in the same sitting.
5. **Prod box, `/opt/obs`**: the first time, **as root**, `install -d -o deploy -g deploy /opt/obs`
   (`bootstrap.sh` does not create this directory). Then, **as `deploy`**, the same `rsync` + `scp`
   as above pointed at `lotro-prod` (never write into a `deploy`-owned stack as root: see
   [Gotchas](#gotchas)), and a `/opt/obs/.env` in the agent-only form of
   `.env.observability.example`: `COMPOSE_PROFILES=` empty, `OBS_BOX=lotro-prod`,
   `OBS_ENVIRONMENT=production`, `OBS_NETWORK=lotro-prod_obs`, `OBS_ALLOY_OOM_SCORE_ADJ=300`,
   `OBS_BACKEND_HOST=obs.lotro-translator.pl`, `OBS_PUSH_USER=prod-agent`,
   `OBS_PUSH_PASSWORD=<step 2>`, both `OBS_OTLP_BIND_*` gateways, `chmod 600`. Keep the `GF_*` and
   `OBS_ALERT_EMAIL` placeholders the template has for this role: compose interpolates every service
   before it applies profiles, so their guards are checked even though Grafana never starts here.
   The network exists once a prod rollout has run with the current `compose.hetzner.yaml`:
   `docker network ls | grep lotro-prod_obs`. Then `docker compose -f compose.observability.yaml up -d`
   (never `--profile` anything; the env file is the only switch) and `docker logs -f obs-alloy`. A
   wrong credential shows up within seconds as a 401 on every push, a source address the vhost does
   not admit as a 404; a correct pair is silent.

   **If the agent was ever up while the name did not resolve, restart it once it does**:
   `docker restart obs-alloy`. The three writers do not recover equally. `prometheus.remote_write`
   resolves the name again and catches up on its own; `otelcol.exporter.otlp` does not: its gRPC
   client keeps the empty address list it built at startup, and every trace after that dies with
   `Unavailable desc = no children to pick from`, which reads like a dead backend rather than a stale
   resolver (seen on 2026-09-09: metrics arrived while traces had been dropping for four minutes).
   One restart fixes all three and costs nothing.
6. **Prod box, the apps**: in `/opt/lotro/.env` the same `OTEL_*` lines as staging with
   `OTEL_DEPLOYMENT_ENVIRONMENT=production`, then `docker compose -f compose.hetzner.yaml up -d`.
   TheKittySaver's half is the same edit in `/opt/tks/.env` (its runbook, #599).
7. **Prove it in Grafana** (tunnel): Explore → Loki `{box="lotro-prod"}` lists the prod containers;
   the LotroKoniecDev dashboard's environment picker offers `production` with data behind it; a trace
   from a prod request opens from its log line. Then **prove the absence rule**:
   `ssh lotro-prod docker stop obs-alloy`, wait ~20 minutes for *Fleet — a box has gone silent* to
   fire and the e-mail to arrive, `docker start obs-alloy`, and watch it resolve. Measured on
   2026-09-09: the rule went `pending` 15 minutes after the last sample and `firing` 5 minutes later,
   both e-mails arrived within 90 seconds, and the agent coming back cleared the rule in under two
   minutes.

   **`{box="lotro-prod"}` lists the containers that WRITE, which is not all of them.** Caddy and
   RabbitMQ write nothing to stdout in a quiet half-hour (Caddy has no `log` directive, and RabbitMQ
   is silent after boot), and `obs-alloy` drops its own log on purpose (shipping it through itself is
   a feedback loop). So the expected list is the five **application** containers, the same as on the
   staging box: check against that before you believe discovery is broken. Nothing filters discovery:
   `discovery.docker` takes every container on the socket.

### Not done yet

- **A dead staging box means no alerting at all**, for either environment. The daily health ping
  (above) is the only backstop.

## Disaster recovery

Each box is **fully disposable**: nothing on it is the source of truth for anything.

| What | Lives | Recovery |
|---|---|---|
| Databases | Neon (prod + staging, PITR + MIGR-04 snapshots); prod also as a nightly encrypted dump in R2 | nothing to do; when the Neon project itself is lost, [Database backups](#database-backups) → "Neon is gone" |
| Backup job | `/opt/lotro/backup.sh` (CD copies it) + one line in the `deploy` user's crontab (prod only) + the `BACKUP_*` keys in the box `.env` | add the crontab line again ([Database backups](#database-backups) → Wiring, step 6) |
| Images | GHCR (built by `cd.yml`) | nothing to do |
| Config | this repo (`compose.hetzner.yaml`, Caddyfile, `deploy.sh`, `compose.observability.yaml` + `.docker/observability/`) | copy again |
| Secrets | `/opt/lotro/.env` and `/opt/obs/.env` per box: the owner's backup, and every value can be minted again ([Secrets](#secrets)) **except `BACKUP_ENCRYPTION_KEY`**: without it every dump in R2 is unreadable, so its only other copy is the password manager | restore or re-mint; restore the backup key, never mint a new one over it |
| TLS certs | Caddy volume; Let's Encrypt issues them again | automatic on first bring-up |
| DP keyrings | `auth-keys` / `frontend-keys` volumes | losing them logs everyone out; nothing to restore |
| Telemetry history | the `obs_*` volumes on **lotro-staging** only | **nothing to restore, on purpose**: telemetry is not a source of truth. A rebuilt box gets the dashboards, datasources and alert rules back from this repo; the history is gone, and that is accepted (ADR-0050) |

Recovery = **new VPS → `bootstrap.sh` → copy the stack files → restore or re-mint the box's `.env` →
`IMAGE_TAG=<the tag the box ran> bash /opt/lotro/deploy.sh` as `deploy` → on the prod box, the
backup's crontab line ([Database backups](#database-backups) → Wiring, step 6) → re-point the DNS A records →
re-pin `HETZNER_SSH_KNOWN_HOSTS` → bring `/opt/obs` back**: as root `install -d -o deploy -g deploy
/opt/obs`, restore its `.env`, then, as `deploy`, the commands under
[Deploying a change](#the-observability-stack). On a rebuilt prod box follow
[the prod agent](#bringing-up-the-prod-agent-the-agent-only-role) steps 5–6, plus steps 1 and 3 when
its public IP changed (the staging Caddy admits pushes only from `OBS_ALLOWED_PUSH_IP`). Use
`deploy.sh`, not a plain `up -d`: the broker volume is new, and `deploy.sh` is what sets its policy for
parked e-mail (ADR-0036 §6). Hetzner backups (prod box) are a shortcut to the same result, not a
dependency.

Neither stack file is named `compose.yaml`, so the `-f` is not optional in any hand-run compose
command. `docker compose ps` in `/opt/lotro` happens to work (it finds the project by the
`COMPOSE_PROJECT_NAME` in `.env` and never reads a file), which makes it easy to believe the bare form
works for `up` too. It does not.

## Gotchas

- **`/opt/lotro` belongs to `deploy`: never write into it as `root`.** CD connects as `deploy` and
  **overwrites** `compose.hetzner.yaml`, `.docker/hetzner/Caddyfile` and `deploy.sh` on every
  rollout. A file that a hand-run `scp` left **root-owned** is not writable by `deploy` (a
  deploy-owned directory does not help: `scp` truncates the existing file, it does not unlink it), so
  the next CD run dies in *"Sync the stack files to the box"* and the rollout never starts. That failed
  the #507 production rollout on 2026-07-13. If you must stage files by hand, `scp` as `deploy`, or
  run `chown -R deploy:deploy /opt/lotro` afterwards. Run `deploy.sh` itself **as `deploy`** too
  (`sudo -u deploy env IMAGE_TAG=… bash /opt/lotro/deploy.sh`), so a hand run does what CD does.
  - A root run does not lock CD out: the last step rewrites `.env` and gives the new file the owner of
    the old one (#1206). Any other user who does not own `.env` (`deploy` aside) is refused before
    anything runs.
  - A root run does not repair an `.env` that is already root-owned. `deploy.sh` refuses an `.env` or
    compose file it cannot read, and a `/opt/lotro` it cannot write to (#806): it exits 2 before it
    pulls anything and prints the owner, the mode and the fix (for `.env`:
    `chown deploy:deploy /opt/lotro/.env && chmod 600 /opt/lotro/.env`).
  - `bootstrap.sh` gives `/opt/tks` to `deploy` as well.
- **The bootstrap Docker leg adopts the box's engine; it does not install one.** The live pair runs
  **Ubuntu's** `docker.io` + `containerd` + `docker-compose-v2`, not Docker's `docker-ce` stack, and
  the two CONFLICT. An unguarded `apt-get install docker-ce containerd.io` on such a box swaps the
  engine: apt removes the running engine, dockerd restarts, and every container on the host goes with
  it, *both* stacks (`/opt/lotro` **and** `/opt/tks`). So `bootstrap.sh` checks for `docker` +
  `docker compose` and installs `docker-ce` only when the box has no engine at all (#502). Do not
  "simplify" that guard away: the engine's vendor does not matter to us, its presence does.
- **`denied` on a pull of a public image means a stored login was refused (#1330).** Docker sends
  the login it has stored for a registry even when the image is public. When the registry refuses
  that login (an expired token), Docker does not try again without it. On 2026-11-11 an expired token
  in `deploy`'s Docker config failed the staging rollout with `migrator Error error from registry:
  denied`, and the automatic rollback failed the same way.
  - `deploy.sh` runs every docker call with `DOCKER_CONFIG=/nonexistent` and without
    `DOCKER_AUTH_CONFIG`, so it ignores the logins in the Docker config of the user that runs it. A
    folder that does not exist gives Docker an empty config, and only root can create `/nonexistent`.
  - So every image the stack pulls must be public, ours on ghcr.io included. A private image fails
    every deploy at the pull. After the first push of a new image, check that its package is public.
  - The rest of that Docker config is ignored too: a compose plugin under `~/.docker/cli-plugins`, a
    docker context and proxy settings. Compose must be installed for the whole system (Ubuntu's
    `docker-compose-v2` or Docker's `docker-compose-plugin`, both in
    `/usr/libexec/docker/cli-plugins`). Bootstrap and CD check `docker compose version` the same way,
    with `DOCKER_CONFIG=/nonexistent`, so a box that breaks this fails there first.
  - A credential helper installed on the box is still used, even with no config folder. When `pass`
    is installed, Docker uses `docker-credential-pass` if it is there. When `pass` is not installed,
    Docker uses `docker-credential-secretservice` if it is there. Both come with the
    `golang-docker-credential-helpers` package. A helper that cannot run (a desktop keyring on a
    server) fails every pull with `error getting credentials`. Bootstrap installs none. To check, as
    `deploy`: `command -v pass docker-credential-pass docker-credential-secretservice` must print
    nothing.
  - A docker command you type by hand still reads the stored logins. Put `DOCKER_CONFIG=/nonexistent`
    in front of any hand command that may pull one of our images, for example
    `DOCKER_CONFIG=/nonexistent docker pull ghcr.io/koniecdev/lotrokoniecdev-migrator:<tag>`.
  - Never run `docker logout` as `deploy` to fix this: the other stack on the box may need the login
    stored there.
- **Two segregated networks per box; Caddy is the only shared component (#506).** Our stack lives
  alone on `${project}_default` (`10.60.0.0/24`); the guest TheKittySaver stack lives alone on
  `${project}_tks` (`10.61.0.0/24`), which our `compose.hetzner.yaml` defines and TKS joins as
  `external` (koniecdev/TheKittySaver#295). Caddy sits on both at pinned static IPs (`10.60.0.100` /
  `10.61.0.100`) and is the only container that crosses. This fences off the cross-stack pivot and the
  header spoofing that one shared network allowed (ADR-0034 amendment). Because TKS joins a network
  our stack creates, our stack must deploy first on a new box; the
  [cutover section](#one-time-cutting-a-live-box-over-to-the-two-segregated-networks-506) shows how
  TKS points `LOTRO_NETWORK` at it.
- **Compose service KEYS must still be unique per network.** Compose registers the service key itself
  as a Docker DNS alias on top of `container_name`. When both stacks shared one network, a guest
  service also named `frontend:` collided with ours, and Caddy's `reverse_proxy frontend:8080`
  round-robined into the WRONG stack (2026-07-12 prod incident: lotro-translator.pl served
  uratujkota.pl). Separate networks remove that collision, but the `tks-` prefix on every TKS service
  key stays the rule: Caddy resolves `reverse_proxy tks-frontend:8080` on the `tks` network, and the
  public-hostname aliases must stay unique there. Detection:
  `docker inspect -f '{{range $k,$v := .NetworkSettings.Networks}}{{$v.Aliases}}{{end}}' <ctr>`: no
  alias may appear on two containers of the same network.
- **Docker bypasses ufw for published ports** (it programs iptables directly). Our stacks publish only
  Caddy's 80/443, which ufw allows anyway. The `DOCKER-USER` leg of `bootstrap.sh` drops every other
  published port that arrives on the public interface (#808), so a port published on `0.0.0.0`
  answers on the box and on the bridges but **not from the internet**. A service that really needs a
  new public port needs that leg changed, not just a `ports:` line. The staging box needs the rule
  most: TheKittySaver's CI runner starts Testcontainers there, which publish Postgres, MinIO and Ryuk
  (it deletes containers on a plain TCP command) on `0.0.0.0` at random ports. On a box that predates
  the leg: `scp scripts/hetzner/bootstrap.sh root@<ip>:/root/ && ssh root@<ip>
  'BOOTSTRAP_SOURCE_ONLY=1 bash -c ". /root/bootstrap.sh; ensure_docker_user_firewall"'`, then read it
  back with `iptables -S DOCKER-USER` (four rules: replies, 80, 443, drop) and the same for
  `ip6tables`. Re-running the leg also repairs a chain someone edited by hand, because every pass
  re-applies it. Never publish another service's port "just to debug"; exec into the network instead
  (`docker compose exec caddy wget -qO- http://tms-api:8080/health/live`; the full `/health` also
  needs the `X-LOTRO-Health-Key` header, ADR-0058).
- **sshd config precedence:** sshd honours the *first* occurrence of a keyword, and
  `/etc/ssh/sshd_config.d/` is included at the top in lexical order. Bootstrap's hardening lives in
  `00-hardening.conf` so it wins over cloud-init's `50-cloud-init.conf`: don't rename it to a higher
  number.
- **ACME fails until DNS propagates.** Caddy keeps retrying on its own, so it is not an error to fix.
  Set up DNS before the bring-up.
- **First cold hit after Neon scale-to-zero** wakes the database in a few seconds. Measured on Hetzner
  from 2026-09-26 to 2026-10-11: the auth database in 0.7 s typically and 4.7 s at worst, the
  translation database in 7.7 s at worst. The auth API caps each connection open at 20 s in code. The
  website's login renewal and its OIDC back-channel give up after 30 s per call, and its account
  client after 10 s per attempt (#923). EF's own retries can still keep the auth API working past
  30 s (#1121).
- **4 GB box + ~9 containers → swap is mandatory, and `bootstrap.sh` provisions it** (#708).
  Hetzner images ship with none, and a box with no swap does not slow down under a memory spike: the
  kernel OOM-kills a container and picks the victim by score, not by importance. The bootstrap's first
  leg lays down a **2 GiB `/swapfile`**, persists it in `/etc/fstab` and sets **`vm.swappiness = 10`**
  (`/etc/sysctl.d/99-swappiness.conf`): an emergency buffer, not a working tier. It is safe on a
  running box: no restart, and every step is guarded, so a re-run changes nothing.
  `scripts/tests/hetzner-bootstrap-swap.tests.sh` drives the real `ensure_swap` against a throwaway
  root with stubbed swap tools, in `pr-verify` and `ci`.

  To apply the leg alone to a box that is already bootstrapped (no full re-bootstrap, no restart):

  ```bash
  scp scripts/hetzner/bootstrap.sh root@<ip>:/root/
  ssh root@<ip> "bash -c 'BOOTSTRAP_SOURCE_ONLY=1 . /root/bootstrap.sh && ensure_swap'"
  ```

  `BOOTSTRAP_SOURCE_ONLY=1` is the script's source seam: it defines the helpers and stops before the
  script touches anything, so you run one function instead of the whole bootstrap. `bash -c` because
  the script needs bash: `. file` alone would run it in whatever login shell root has.

  Verify without a reboot. `swapon -a` reads `/etc/fstab` again, which is what boot does, and
  `findmnt --verify` catches fstab damage that otherwise only shows up at the next boot:

  ```bash
  ssh root@<ip> 'swapon --show; findmnt --verify --fstab; swapoff /swapfile && swapon -a && swapon --show; cat /proc/sys/vm/swappiness'
  ```

  Only run that `swapoff` right after creation, while nothing is paged out (`swapon --show` USED = 0B):
  on a box that is actually using swap, it forces every page back into RAM at once.
- **A new kernel runs only after a reboot, and the boxes reboot themselves at night** (#1327).
  `unattended-upgrades` installs security updates every day around 06:00 UTC, but a kernel, libc or
  Docker update stays on disk until the next boot, so a box nobody reboots runs an old kernel for
  months. `bootstrap.sh` writes `/etc/apt/apt.conf.d/52unattended-reboot`: on a night after an update
  that needs it, **staging reboots at 01:30 UTC and prod at 02:30 UTC** (02:30 and 03:30 Polish time
  in winter, an hour later in summer). Most nights nothing happens. Staging goes first, as an early
  warning, and an hour apart, so the monitoring stack on staging is up while prod reboots. There is
  no default time for that reason: a box without one refuses the bootstrap until `REBOOT_TIME` is
  set, and a re-run keeps the time the box already has.

  Every stack on the box, the guest stack included, is down for about a minute. The containers come
  back on their own (`docker` starts at boot and every container is `unless-stopped`; the migrator is
  not, so a reboot never runs a migration). The morning health ping is the check that prod came back.
  From 5 minutes before the reboot, ssh logins are refused, so a CD run to that box at 01:25-01:30 or
  02:25-02:30 UTC fails at ssh, and one in progress is cut off: do not approve a prod promotion near
  02:30 UTC, nor from 01:00 to 01:30 UTC while the nightly dump runs
  ([Database backups](#database-backups)), and re-run a staging deploy that hit the window. A job of the guest project's CI runner
  on the staging box dies too and needs a re-run. If prod's Hetzner backup window covers 02:30 UTC,
  give prod another time.

  **Bringing a box onto this leg** (both boxes predate it). Reboot by hand first, while you watch: a
  box that already has `/var/run/reboot-required` reboots unattended the night after the leg lands,
  onto a kernel many builds newer. Staging first, then prod after staging checks out:

  ```bash
  ssh root@<ip> 'cat /var/run/reboot-required.pkgs; reboot'
  # a minute later: new kernel, firewall chain, containers, and the smoke test against the box's origins
  ssh root@<ip> 'uname -r; iptables -S DOCKER-USER; systemctl --failed --no-legend; docker ps --format "{{.Names}} {{.Status}}"'
  ```

  Then apply the leg alone (it only writes the file):

  ```bash
  scp scripts/hetzner/bootstrap.sh root@<ip>:/root/
  ssh root@<ip> "bash -c 'BOOTSTRAP_SOURCE_ONLY=1 . /root/bootstrap.sh && REBOOT_TIME=01:30 ensure_unattended_reboot'"   # staging; 02:30 for prod
  ssh root@<ip> 'apt-config dump | grep Automatic-Reboot'
  ```

  `scripts/tests/hetzner-bootstrap-reboot.tests.sh` drives the real `ensure_unattended_reboot` against
  a throwaway file, in `pr-verify` and `ci`.
- **Container log rotation is unbounded by default** (json-file driver, small VPS disk).
  `compose.observability.yaml` caps it per service; `compose.hetzner.yaml` does not yet. Cap it at the
  compose level (`logging: { driver: json-file, options: { max-size: "10m", max-file: "3" } }` per
  service), NOT via `/etc/docker/daemon.json` on the live boxes: a daemon-config change restarts
  dockerd and bounces every running container.
- `.github/workflows/*` pushes need the koniecdev token.
- **auth-api calls Have I Been Pwned when a password is set** (registration, password change, reset;
  ADR-0066). It needs outbound HTTPS to `api.pwnedpasswords.com`. When that call fails, the password is
  accepted unchecked and auth-api logs warning `3300`, `3301`, `3302` or `3303`. A run of those
  warnings means the breach check is off in practice: look at the box's outbound network first.
  Information `2740` means the check works: it refused a password found in a breach.

## History — the Azure era

From M6 (2026-06) until **2026-07-12** the stack ran on **Azure Container Apps** (Terraform, Key
Vault secrets, Log Analytics + Application Insights, scale-to-zero with a scheduled warm window). The
Azure for Students subscription was disabled that day (credits used up, renewal refused) and both
prods went dark. [ADR-0034](../adr/0034-hetzner-vps-instead-of-azure-container-apps.md) records the
move; [`hetzner-migration-plan.md`](hetzner-migration-plan.md) is the executed playbook; epic #486
tracked it. The Terraform root and the Key Vault seeders are kept as a read-only record in
[`azure-graveyard/`](azure-graveyard/README.md), because the ADRs of that era discuss those files
line by line. Nothing in there can run: the subscription is disabled and the Terraform state died with
it.

**Recorded so nobody tries again:** with the subscription disabled, Key Vault serves secret
*metadata* but refuses every *value* read (`az keyvault secret list` works and returns the 8 names;
`az keyvault secret show` → **Forbidden — the subscription associated with this vault has been
disabled**). **No secret value could be recovered.** Every secret on the Hetzner boxes was minted from
scratch (see [Secrets](#secrets)), which is also why the auth DBs needed the
[reseed](#reseed-traps--the-auth-seeder-is-create-if-missing).

Two ADR rulings died with the platform and are marked obsolete by platform: ADR-0027 (the scheduled
warm window; the boxes are always on) and ADR-0029 (the single-active-revision sweep; there are no
revisions). ADR-0025 (DB-free readiness probes) **still applies**: Neon still scales to zero.

## See also

- [`compose.hetzner.yaml`](../../compose.hetzner.yaml) — the deployed stack; the authoritative list of
  what each container reads.
- [`.env.hetzner.example`](../../.env.hetzner.example) — the box env template (every key, placeholder values).
- [`compose.observability.yaml`](../../compose.observability.yaml) / [`.env.observability.example`](../../.env.observability.example)
  — the hand-deployed observability project and its env template.
- [`.env.example`](../../.env.example) / [`.env.prod.example`](../../.env.prod.example) — the dev-compose
  and production-parity env templates.
- [`compose.yaml`](../../compose.yaml) / [`compose.prod.yaml`](../../compose.prod.yaml) — the dev and
  production-parity stacks.
- [`scripts/hetzner/`](../../scripts/hetzner/) — `bootstrap.sh` (provision a box) + `deploy.sh` (the
  rollout script CD runs).
- [ADR-0034](../adr/0034-hetzner-vps-instead-of-azure-container-apps.md) — the Hetzner decision.
- [ADR-0008](../adr/0008-cloud-agnostic-deployment-and-environment-strategy.md) — the provider-neutral
  container contract this runbook puts into practice.
- [ADR-0023](../adr/0023-forward-only-n-1-backward-compatible-migrations.md) / [ADR-0024](../adr/0024-n1-backward-compat-ci-proof.md) — forward-only, N-1 compatible migrations.
