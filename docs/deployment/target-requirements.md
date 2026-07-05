# Deployment target requirements + Azure ⇄ AWS mapping

> The **platform contract**: exactly what any container host must provide to run LotroKoniecDev,
> mapped to the concrete service on Azure and AWS — so the provider choice is *informed, not
> guessed* (ADR-0008 §8). Provider-neutral by design: nothing here commits the repo to a cloud.
>
> **Scope.** *What* the platform must offer and *which* managed service supplies it on each cloud,
> plus a neutral decision aid. This is the layer **above** the [runbook](runbook.md) — the runbook
> says *how* to configure each service (the env-var matrix, secret generation, the bring-up
> sequence); this document says *what capabilities the host must have* before any of that applies.
> The provider-specific click-by-click walkthrough is **deliberately deferred** until the provider
> is chosen (see [Out of scope](#out-of-scope--deferred)).

## Contents

- [The platform contract](#the-platform-contract) — the host-capability requirements
- [Requirement → Azure ⇄ AWS mapping](#requirement--azure--aws-mapping)
- [Decision aid](#decision-aid) — neutral per-cloud pros/cons for *this* app
- [Out of scope / deferred](#out-of-scope--deferred)
- [See also](#see-also)

## The platform contract

The deployment unit is an **OCI container image** (ADR-0008 §1) — the four images CD publishes to
GHCR (`auth-api`, `tms-api`, `frontend`, `migrator`; M6-09). "Anything that can run an OCI image
behind a TLS ingress can run this system; nothing else is assumed." Concretely, the host must
provide the eleven capabilities below. Each is a *requirement on the platform*, already satisfied
by the app images and exercised end-to-end by `compose.prod.yaml` (M6-07/M6-08) — the cloud must
reproduce them.

| # | Requirement | Why (where it bites) | Source |
|---|---|---|---|
| **R1** | **HTTP ingress + TLS termination**, one public HTTPS origin per web app (frontend, auth-api, tms-api) | Apps serve **plain HTTP on `:8080`**; the ingress owns TLS. tms-api's origin must be **publicly reachable** for CLI/WPF translation-file distribution. | ADR-0008 §2; runbook *container contract* |
| **R2** | **Forwarded headers** — the ingress sends `X-Forwarded-Proto` / `Host` / `For` | Apps run `UseForwardedHeaders` first (M6-02) to rebuild the `https` scheme for OpenIddict `iss`, OIDC `redirect_uri`, and `Secure` cookies. Apps trust **all** upstream proxies → **`:8080` must never be publicly reachable**, only via the ingress. | M6-02; ADR-0008 §3; runbook rule 5 |
| **R3** | **Per-service secret injection** from a secret store, as env vars | OpenIddict signing/encryption keys + API client secret, both DB connection strings, SMTP password. Never baked into the image or app config. | ADR-0008 §2; runbook *generating secrets* |
| **R4** | **Shared, persistent storage for the Data Protection keyrings** — a **ReadWriteMany** volume mounted at `/keys` for **auth-api and frontend** | Identity login cookies, Razor antiforgery, password-reset/email links. Ephemeral or per-replica keyrings log everyone out on every deploy/scale and break those links. Fails fast at boot if `DataProtection__KeyRingPath` is unset outside Development. | ADR-0005; M6-04; runbook rule 6 |
| **R5** | **Postgres reachable over SSL**, hosting **two databases** (`lotro_translation`, `lotro_auth`) | The app knows only a connection string + `Ssl Mode=Require`. Swapping self-hosted → managed is a single env change per context. | ADR-0008 §5; runbook *databases* |
| **R6** | **A pre-deploy migration job** — run a one-shot image to completion **before** the APIs serve, and **gate API rollout on its success** | The `migrator` image applies both contexts' bundles idempotently and fail-fast; gating on success means there is never half-migrated serving. Forward-only (ADR-0023) — the deploy takes a pre-migration Neon snapshot automatically (MIGR-04), with PITR as the ambient net. | ADR-0008 §6; M6-10; runbook *database migrations* |
| **R7** | **Environment-variable injection** for all non-secret runtime config (12-factor) | No `appsettings.*` carries an environment-specific URL or secret (M6-06). Plain values in app config, secrets per R3. | ADR-0008 §2/§3 |
| **R8** | **≥1 always-on replica** for each of the three web apps; the migrator is one-shot | Auth/OIDC and the DP keyring make a warm replica the sane floor. **Scaling past 1 replica requires the shared keyring of R4** (otherwise replicas disagree on keys). | ticket M6-12; R4 |
| **R9** | **Outbound network (egress)** to: SMTP (auth email), the OTLP collector (telemetry), an external **managed Postgres** if used, and the **LOTRO forum** (the forum watcher — deferred, M2-18/#85 — but the allowance is forward-looking). | The app has **no cloud-provider SDK**; telemetry leaves only via OTLP, mail only via SMTP. *(The inverse — translation-file distribution — is **inbound** to tms-api over the public ingress of R1, not egress; spec 0001 / M2-20.)* | ADR-0008 §2; spec 0001 |
| **R10** | **OTLP telemetry ingestion** endpoint reachable from the apps (`OTEL_EXPORTER_OTLP_ENDPOINT`) | Serilog → OTLP and OpenTelemetry `UseOtlpExporter` are vendor-neutral; empty endpoint disables export (optional, but the platform should offer a collector). | ADR-0008 *context*; runbook matrix |
| **R11** | **The container floor** the images already meet: non-root (`USER app`), a `HEALTHCHECK` (the **APIs** serve + probe `/health/live` & `/health/ready`; the **frontend** probes `/`), **structured JSON logs to stdout**, registry pull from GHCR | The platform must run non-root images, gate readiness on the APIs' `/health/ready` (and may observe each image's `HEALTHCHECK`), and collect stdout. | ADR-0008 §2; Dockerfiles; runbook |

### The consistency constraint the platform shape must respect (R1 + R2)

Because **Production OpenIddict rejects plain HTTP** and the token `iss` must be byte-identical
everywhere, the **auth public origin** (e.g. `https://auth.<env-domain>`) must be reachable, over
that same `https` URL, from **three** places: the browser (front-channel `/authorize`), the
**frontend** container, and the **tms-api** container (their back-channel OIDC metadata + JWKS
fetch). In a real cloud the public ingress origin resolves from inside the cluster via public DNS,
so no split-horizon DNS is needed — but the platform must not block intra-cluster egress to that
public origin (this is what `compose.prod.yaml` reproduces with Caddy network aliases). Full rules:
runbook *[Consistency rules that bite](runbook.md#consistency-rules-that-bite)*.

## Requirement → Azure ⇄ AWS mapping

Every requirement above mapped to the concrete managed service on each cloud. The two AWS columns
reflect that **ECS Fargate** and **App Runner** are both candidates and differ on one load-bearing
point (R4 persistent storage).

| Requirement | **Azure** | **AWS — ECS Fargate** | **AWS — App Runner** |
|---|---|---|---|
| R1 Compute + HTTP ingress | **Container Apps** (managed Envoy ingress, one FQDN per app) | **ECS Fargate** behind an **Application Load Balancer** (host/path routing) | **App Runner** (built-in ingress, one URL per service) |
| R1 TLS termination + cert | ACA **managed certificate** (or Azure Front Door) | **ACM** cert on the ALB | App Runner **managed TLS** (or ACM + custom domain) |
| R2 Forwarded headers | ACA ingress sets `X-Forwarded-Proto/Host/For` | ALB sets `X-Forwarded-*` | App Runner sets `X-Forwarded-*` |
| R3 Secret injection | ACA **secrets** (optionally **Key Vault** references / managed identity) → env | **Secrets Manager** or **SSM Parameter Store** → task-def `secrets` → env | Secrets Manager / SSM → App Runner runtime env |
| R4 Shared RWX keyring volume | **Azure Files** share (SMB, ReadWriteMany) mounted at `/keys` | **Amazon EFS** (NFS, ReadWriteMany) mounted at `/keys` | **No persistent/shared FS** — see the decision-aid caveat |
| R5 Managed Postgres (SSL) | **Azure Database for PostgreSQL** (Flexible Server) | **Amazon RDS for PostgreSQL** (or Aurora PostgreSQL) | RDS / Aurora (reached via a **VPC connector**) |
| R6 Pre-deploy migration job | **ACA Job** (manual/scheduled/event) the revision waits on | **ECS `RunTask`** one-off (or a CodePipeline/CodeBuild step) gating the service update | A separate **ECS RunTask / CodeBuild** step (App Runner has no native run-once job) |
| R7 Env-var injection | ACA `env` | ECS task-def `environment` | App Runner runtime env |
| R8 ≥1 replica + scaling | ACA `minReplicas`/`maxReplicas` + scale rules (scale-to-zero optional) | ECS desired count + **Application Auto Scaling** | App Runner auto scaling (min instances ≥ 1) |
| R9 Egress (SMTP/OTLP/forum/DB) | ACA managed egress (or VNet integration + NAT) | ECS in a private subnet + **NAT Gateway** | App Runner **VPC connector** for private targets |
| R10 OTLP ingestion | **OTLP → Azure Monitor** / Application Insights (via the OpenTelemetry collector or AI's OTLP endpoint) | **OTLP → CloudWatch** via the **ADOT** (AWS Distro for OpenTelemetry) collector | OTLP → CloudWatch via ADOT |
| R11 Image pull (GHCR) | ACA pulls from **GHCR** (registry credentials; or mirror to ACR) | ECS pulls from GHCR (or mirror to **ECR**) | App Runner pulls from a registry (GHCR public, or mirror to ECR) |
| R11 stdout JSON logs | ACA → **Log Analytics** | ECS `awslogs` → **CloudWatch Logs** | App Runner → CloudWatch Logs |

> Neutrality is preserved: images stay on **GHCR** (R11), telemetry leaves only via **OTLP** (R10),
> storage is a generic **`/keys` mount** (R4), and the database is just a **connection string** (R5)
> — none of these references a cloud SDK. Mirroring images to ACR/ECR or using Key Vault/Secrets
> Manager references are *conveniences a chosen provider may add later*, never prerequisites.

## Decision aid

Neutral by request: both clouds **satisfy every requirement** above. The table states each cloud's
factual edge on the dimensions that matter for *this* app; it deliberately **does not recommend** a
provider — the maintainer weighs cost, operational fit, and familiarity and picks. Nothing in the
code or config commits to either, so the choice stays cheap and reversible (ADR-0008 *consequences*).

| Factor | **Azure** | **AWS** | Neutral note |
|---|---|---|---|
| **Existing familiarity** | App Service + Blob experience (portal, RBAC, Key Vault, `az`) transfers partially to **Container Apps** + Azure Files | New surface to learn | The only asymmetric *human* factor (ADR-0008 records "Azure App Service + Blob only"). Lowers ramp-up risk on Azure; says nothing about the platforms' merits. |
| **DP shared-FS (R4)** | **Azure Files** (RWX) mounts natively into ACA — direct fit | **EFS** (RWX) mounts natively into **ECS Fargate** — direct fit; **App Runner has no shared FS** | Both clouds satisfy R4 *via their container-orchestrator path*. On AWS this **rules App Runner out for auth-api + frontend** unless the keyring moves off-filesystem (a code change); tms-api (no keyring) could still use App Runner. |
| **Managed Postgres (R5)** | Azure Database for PostgreSQL Flexible Server | RDS for PostgreSQL / Aurora PostgreSQL | First-class, SSL-capable, one-env-var swap on both. Effective parity. |
| **OTLP ingestion (R10)** | OTLP → Azure Monitor / App Insights (collector or AI OTLP endpoint) | OTLP → CloudWatch via the ADOT collector | Both need a collector hop; neither ingests raw OTLP into the native store unaided. Effective parity. |
| **Migration job (R6)** | **ACA Jobs** are a native one-shot primitive that a revision can wait on | **ECS RunTask** is the one-off primitive; gating is wired in the pipeline | Both express "run-once, gate rollout" cleanly; ACA's job is a first-class resource, ECS leans on the pipeline. |
| **Ingress / cert model** | ACA: managed ingress + managed cert built in (fewest moving parts) | ECS: assemble **ALB + target group + ACM + listener**; App Runner: built-in (fewest) | ACA and App Runner hide the ingress; ECS is the most explicit (more parts to stand up by hand on a first deploy). |
| **Cost shape** | ACA consumption billing; **scale-to-zero** possible | ECS Fargate per-vCPU/GB-hour (no scale-to-zero for always-on); App Runner per-request + provisioned | R8 + the OIDC/keyring warm-replica floor limit scale-to-zero savings **on both** — a like-for-like always-on small footprint is the realistic baseline. Exact cost depends on replica count + DB tier, not the platform alone. |
| **"No blind IaC" fit (ADR-0008 §8)** | ACA: a single app resource + a job — few parts to bring up by hand | ECS: cluster + service + task def + ALB + EFS — most parts; App Runner: fewest | All three support a **human-driven portal/console first deploy**; they differ only in how many resources that entails. |

**No recommendation is made here.** Both Azure and AWS meet the contract; the genuine
discriminators are (a) the documented Azure familiarity, and (b) the App-Runner-vs-ECS storage
caveat *within* AWS. The provider decision — and the IaC/walkthrough that follows it — is left to
the maintainer.

## Out of scope / deferred

- **The provider-specific deploy walkthrough** (click-by-click bring-up on the chosen cloud) and any
  **IaC template** are produced **only after the provider is chosen** (ADR-0008 §8: "the first
  deploy is human-driven"; no AI-generated IaC operated without understanding). A **follow-up ticket
  for that walkthrough is to be created post-decision** — it is not part of M6-12 and is not created
  now.
- **HA / backup topology** for Postgres is not committed here (ADR-0008 §5) — it belongs to the
  provider decision; the parity stack's self-hosted Postgres is a parity tool, not production-grade.
- **The forum-watcher egress (R9)** is forward-looking: the watcher itself is deferred (M2-18 /
  #85). The egress allowance is documented so the chosen platform isn't configured to block it later.

## See also

- [ADR-0008](../adr/0008-cloud-agnostic-deployment-and-environment-strategy.md) — the cloud-agnostic
  deployment & environment strategy this document maps to concrete services (esp. §1 OCI unit, §2
  neutrality contract, §5 Postgres, §6 migrations, §8 undecided provider).
- [runbook.md](runbook.md) — the operator layer below this one: the env-var matrix (per service ×
  environment), secret generation, the consistency rules, the bring-up sequence, and migrations.
- [`compose.prod.yaml`](../../compose.prod.yaml) — the production-parity stack that exercises every
  requirement here locally (Caddy ingress + forwarded headers, DP keyring volumes, SSL Postgres,
  the pre-deploy migrator gate).
- [`.github/workflows/cd.yml`](../../.github/workflows/cd.yml) — publishes the four GHCR images that
  are the deployment unit (R11).
