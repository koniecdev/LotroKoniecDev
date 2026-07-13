# ADR-0034: Single Hetzner VPS instead of Azure Container Apps

**Status:** Accepted
**Date:** 2026-07-12
**Decision-makers:** Solo maintainer
**Related:** deployment (all of `iac/`, `compose.prod.yaml`, CD workflows), epic #486 (HETZ-01..06:
#487–#492), ADR-0008 (cloud deployment), ADR-0018 (staging), ADR-0023/0024 (migrations), ADR-0027
(warm window — made obsolete), ADR-0029 (revision sweep — made obsolete)

## Context

On 2026-07-12 the Azure for Students subscription hosting BOTH projects (LotroKoniecDev and
TheKittySaver, prod + staging each) was disabled: `ReadOnlyDisabledSubscription` on every CD
write, both prods serving ACA's "Container App is stopped" 404. Cost Management shows EUR 51.36
burned 1–12 July (~EUR 6/day until the ADR-0027 FinOps fixes landed 9 July, ~EUR 1.8/day after —
a week too late). The portal refuses renewal ("You are not eligible to renew Azure for Students")
and offers only Students Starter, which has no credit and no Container Apps. Even if a retry
succeeds, the platform re-dies every year the verification fails.

Code facts that constrain the choice:

- **Nothing stateful lives on Azure.** DBs are Neon (ADR-0023/0024 discipline, PITR, MIGR-04
  snapshots), images are GHCR (built by `cd.yml`), secrets are reproducible
  (`scripts/gen-openiddict-keys.sh`, Neon console, Brevo), DNS is at an external registrar.
  Azure held only compute, Key Vault copies, and Log Analytics telemetry.
- **`compose.prod.yaml` already is a single-node deployment.** It was built (ADR-0008 §4 / M6-07)
  to reproduce the whole cloud topology on one machine: 4 app images + Caddy terminating TLS on
  one origin per app + one-shot migrator + DP keyring volumes. Its only laptop-isms are the local
  CA (`.docker/prod-https/`, `trust-ca-entrypoint.sh`) and an in-compose postgres — both of which
  a real host with real domains makes unnecessary.
- ACA-specific machinery that exists only to fight the platform: KEDA cron warm window +
  mandatory `http_scale_rule` (ADR-0027), single-active-revision sweep (ADR-0029), measured
  43–62 s cold starts with auth's 31 s dominating, LAW daily-quota juggling, the
  "deployed is not running" revision trap.
- CI images are `linux/amd64` only; no multi-arch buildx exists.
- Two projects × (prod + staging) ≈ 16 .NET containers + Caddy. Idle RSS ~150–250 MB each →
  a 4 GB box is a lie waiting to OOM; 8 GB fits comfortably.

## Decision

### 1. One Hetzner VPS hosts everything, split only on real users

A single **CX32 (4 vCPU, 8 GB, amd64, Ubuntu 24.04 LTS, Falkenstein)** — ~EUR 7/month + ~20% for
snapshot backups (verify price at checkout) — runs prod and staging of both projects. That is a
flat ~EUR 8–9/month versus ~EUR 55–180/month of Azure burn, with zero cold starts. The split
trigger is explicit: real player traffic on lotro-translator.pl, not a hypothetical. CX22 (4 GB)
is rejected as the "cheapest" option — see Context; amd64 over ARM (CAX) because the images are
amd64 and multi-arch buildx is pure added surface.

> **Amendment (2026-07-12, bring-up day):** the CX32 in Falkenstein was unavailable at checkout,
> so the fleet is **two CX23 boxes in Nuremberg** instead — `lotro-prod` (167.233.159.221, backups
> on) hosts BOTH prods, `lotro-staging` (91.98.74.228, no backups) hosts BOTH stagings. This
> **splits prod from staging by machine rather than deferring the split to real-user traffic**, and
> each box is 4 GB (CX23), not the 8 GB this section argued for — the "4 GB is a lie waiting to
> OOM" concern from Context now applies per box (~8–9 containers on prod), so the prod box watches
> memory and adds a swapfile if needed (see the runbook). The single-file / two-compose-projects
> design of §2 is unchanged; each box simply runs one project. The boxes run **Ubuntu 26.04 LTS**
> (image availability), not 24.04 — `scripts/hetzner/bootstrap.sh` (HETZ-01) is verified on both.
> Server facts, IPs and the (re)provisioning recipe live in `docs/deployment/runbook.md`.

### 2. `compose.hetzner.yaml` derived from `compose.prod.yaml`, minus the laptop-isms

The Hetzner stack is the parity stack with three deletions and one swap: **delete** the local CA
+ `trust-ca-entrypoint.sh` mounts (Let's Encrypt certs are OS-trusted), **delete** the in-compose
postgres (Neon stays the DB for prod AND staging — self-hosting the DB would trade a managed
PITR story for owning backups), **delete** mailpit/aspire from the critical path (profiles stay
optional), **swap** the Caddyfile to real domains with automatic ACME. `compose.prod.yaml` itself
is untouched — it remains the laptop parity stack. Prod and staging are two compose projects
(`-p lotro-prod` / `-p lotro-staging`) off one file, parametrized by env file + image tag.

### 3. CD becomes ssh + compose; two-stage promotion survives

`cd.yml`'s build→GHCR legs stay. The deploy legs become: ssh as the `deploy` user → migrator
one-shot → `docker compose pull && up -d` → `scripts/smoke.sh` (fingerprint assertion included).
Staging deploys automatically on merge; prod stays behind the same gated GitHub Environment as
today (ADR-0018 flow, new target). Terraform (`iac/`), Key Vault, and the Azure workflow legs are
retired. A deploy is seconds of container restart — acceptable pre-launch; zero-downtime rollouts
are a real-users problem, and ADR-0023's forward-only, N-1-compatible migration discipline stays
binding regardless (the migrator still runs before the new app containers).

### 4. Secrets live in git-ignored env files on the server

One `/opt/lotro/.env` per box (`chmod 600`; prod holds the prod secrets, staging the staging
ones — the per-box file is what compose auto-loads and what `COMPOSE_PROJECT_NAME` in it selects),
assembled once from: fresh Neon connection strings, regenerated OpenIddict keys (invalidates
sessions — free pre-launch), Brevo SMTP key and admin seeds from the owner. `.env.hetzner.example`
in-repo documents every variable; the runbook maps each to its source of truth and rotation
command. No vault service replaces Key Vault — for a solo-maintainer box, an 0600 env file plus
the existing GitGuardian/gitleaks gates is the right-sized answer (YAGNI).

### 5. LotroKoniecDev owns the server-level infra; TheKittySaver mounts alongside

Bootstrap script, shared Caddy, and the runbook live in this repo (precedent: the shared Log
Analytics workspace lived only in `iac/azure-law.tf`). TheKittySaver gets its own compose stack,
CD tickets, and a twin ADR in its own repo, deployed to `/opt/tks` behind the same Caddy —
after LOTRO prod is live.

### 6. ADR-0027 and ADR-0029 are obsolete-by-platform

The warm window, `http_scale_rule` rule, and revision sweep solved ACA economics and ACA revision
semantics. On an always-on VPS they have no referent. Their Status lines get an
"obsolete — platform retired by ADR-0034" note (HETZ-06); the daily GH Actions health ping stays
(free, still a useful uptime signal).

## Consequences

### Positive

- Flat ~EUR 8–9/month for everything; no annual credit-eligibility ruleta; no cold starts
  (43–62 s → 0), which also shrinks the auth cold-start × Neon-resume race window.
- The deployment model becomes the thing already proven on a laptop (`compose.prod.yaml`
  exercised the same Dockerfiles, Caddy topology, forwarded headers, DP keyrings).
- Terraform, Key Vault, KEDA, revision management, LAW quotas — all deleted, not maintained.

### Negative / Accepted Trade-offs

- Single point of failure: one box, one region. Accepted pre-launch; the split trigger is §1.
- Self-managed OS security (ufw, fail2ban, unattended-upgrades — HETZ-01) replaces a managed
  platform's patching.
- Deploys cause seconds of downtime (no rolling revisions). Accepted; revisit with real users.
- Observability shrinks: no LAW. OTLP endpoint stays configurable; aspire-dashboard remains an
  optional profile. A real telemetry sink is a later, deliberate decision.
- Neon stays a third-party dependency; its free-tier limits (storage cliff ~0.5 GB) are now the
  binding infra constraint.

## Alternatives Considered

### A. Retry/renew Azure for Students

Renewal currently refused; even on success it is a 12-month fuse with an eligibility check at the
end, and the ADR-0027 hackery exists only to make ACA affordable. Rejected as the platform; a
successful retry merely funds a calmer migration.

### B. PaaS free tiers (Fly.io, Railway, Render, Koyeb)

Free tiers are revocable marketing (both projects just experienced exactly that failure mode),
multi-app + custom-domain TLS + one-shot migrators fit awkwardly, and per-app pricing crosses a
VPS within months. Rejected.

### C. ARM (CAX11/21) for the lower price

Requires multi-arch buildx in CI and re-verifying native-dependency behavior for four images to
save ~EUR 2/month. Rejected — wrong side of the effort/price curve.

### D. Coolify/Dokploy on the VPS

A deploy UI would replace the exact compose + ssh mechanics the repo already has proven and
tested. New tool surface, same outcome. Rejected (YAGNI).

### E. Self-hosted postgres in the compose stack

compose.prod.yaml already carries the service, but it would trade Neon's PITR + MIGR-04 snapshot
machinery (ADR-0025) for hand-rolled backups on the same failure domain as the apps. Rejected.

## Implementation Notes

- New: `scripts/hetzner/bootstrap.sh` (no `.ps1` twin — runs on the server, the twins rule
  covers dev machines), `compose.hetzner.yaml`, `.docker/hetzner/Caddyfile`,
  `.env.hetzner.example`, `docs/deployment/runbook.md`
- Changed: `cd.yml`/`deploy.yml` (ssh deploy legs), `docs/deployment/runbook.md` (rewritten),
  `CLAUDE.md` (M6 section), ADR-0027/0029 status lines
- Retired: `iac/` Azure resources, `seed-keyvault.{sh,ps1}`, `infra.yml`, local-CA mounts in the
  Hetzner stack (parity stack keeps them)
- Execution: epic #486, tickets #487–#492; owner-assisted bring-up is #491;
  plan: `docs/deployment/hetzner-migration-plan.md`

## References

- Epic #486; tickets #487 (bootstrap), #488 (compose+Caddy), #489 (secrets), #490 (CD),
  #491 (bring-up + DNS), #492 (Azure retirement)
- ADR-0008 (cloud-agnostic deployment — its §4 parity stack is what made this migration cheap),
  ADR-0018, ADR-0023, ADR-0024, ADR-0025, ADR-0027, ADR-0029
- `docs/deployment/runbook.md` — env-var matrix the Hetzner stack must reproduce
- Hetzner cloud pricing: https://www.hetzner.com/cloud/
