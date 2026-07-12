# Hetzner migration plan — executable playbook

> Decision: ADR-0034. Epic #486, tickets #487–#492. Written 2026-07-12, the day the Azure
> subscription died — the goal of Phase 0 is **prods back up the same day**. This document is
> the working script for the implementing session; tickets carry the acceptance criteria.

## State snapshot (2026-07-12)

- Azure subscription `Disabled` (credits exhausted, renewal refused). Both prods 404. Nothing
  on Azure is billing; nothing needs rescuing from it (try `az keyvault secret show` once for
  convenience — if it fails, every secret is reproducible, see §Secrets).
- Safe and untouched: Neon DBs (prod + staging, both projects), GHCR images, repo + IaC history,
  DNS zone at the external registrar, Brevo SMTP.
- Deployment blueprint that already works: `compose.prod.yaml` + `.docker/caddy/Caddyfile`
  (laptop parity stack — same Dockerfiles, Caddy TLS-per-origin topology, forwarded headers,
  one-shot migrator, DP keyring volumes).

## Phase 0 — TODAY: interactive bring-up (ticket #491, plus minimal #487/#488/#489 inline)

Run in an **interactive** session with the owner (server purchase, DNS panel, and secrets are
owner inputs — this phase is not loopable). Order matters; DNS early (ACME needs it resolving).

1. **Owner:** create Hetzner account → buy **CX32, amd64, Ubuntu 24.04 LTS, Falkenstein**
   (+ enable snapshots/backups) → add ssh public key → paste the server IP into the session.
2. **Owner (parallel):** registrar DNS panel — A records → server IP, TTL 300:
   apex `lotro-translator.pl` (+ `www` if configured today) and every app/staging hostname.
   **Source of truth for the exact hostname list: the env matrix in
   `docs/deployment/runbook.md`** (frontend/auth/tms × prod/staging — do not guess from memory,
   read the matrix; keep the hostnames identical to the ACA setup so no appsettings change).
3. **Session:** `scripts/hetzner/bootstrap.sh` over ssh (write it first if #487 hasn't landed —
   spec in the ticket: Docker + compose plugin, ufw 22/80/443, fail2ban, unattended-upgrades,
   `deploy` user, GHCR read-only PAT login).
4. **Session:** land #488's files (`compose.hetzner.yaml`, `.docker/hetzner/Caddyfile`,
   `.env.hetzner.example`) on a branch; scp/rsync to `/opt/lotro/`.
5. **Session + owner:** assemble `/opt/lotro/.env.hetzner.prod` and `.env.hetzner.staging`
   per §Secrets. `chmod 600`.
6. **Bring-up sequence (per env, staging first as the rehearsal):**
   migrator one-shot → apps → Caddy; watch ACME issue certs; then
   `scripts/smoke.sh` against the public origins + a manual OIDC register/login round-trip,
   translation list, `GET /translation-files/{lang}`.
7. Note Neon-resume behavior on first cold hit (auth's 20 s connection-open vs ~31 s resume —
   known pre-existing race; the always-on host shrinks the window but doesn't fix the bug).

**Done when:** prod + staging serve with valid LE certs, smoke green from an external network.

## Phase 1–6 — hardening the same thing into the repo (loopable after Phase 0)

| Phase | Ticket | Content |
|---|---|---|
| 1 | #487 | bootstrap.sh idempotent + hetzner-runbook.md server section |
| 2 | #488 | compose.hetzner.yaml + Caddyfile + .env.hetzner.example, PR-quality |
| 3 | #489 | secrets matrix documented; KV read attempt recorded |
| 4 | #490 | CD over ssh (staging auto, prod gated env), smoke in-pipeline, Azure legs disabled |
| 5 | #491 | close out with the live verification evidence |
| 6 | #492 | Azure retirement: iac/, seed-keyvault, workflows, runbook rewrite, ADR-0027/0029 status notes, CLAUDE.md M6 sweep |

TheKittySaver: twin epic in its repo after LOTRO prod is live — same recipe, `/opt/tks`,
uratujkota.pl vhosts appended to the same Caddy (slots pre-commented in #488's Caddyfile).
**One extra must-have LOTRO doesn't have: TKS stores cat gallery photos + announcement
thumbnails in Azure Blob** (`Infrastructure/FileStorage/AzureBlob/`, provisioned by its
`iac/storage.tf`; read links via `IImageReadLinkFactory`). The seam is interface + connection
string (`AzureBlobFileStorageRegistration` takes `ConnectionString`/`ServiceUri`), so the
decided path is a **fresh storage account on the owner's personal (pay-as-you-go) Azure
account** — config-only swap, cents/month at portfolio scale. Existing blobs on the dead
student subscription are test/seed data (retained ~90 days, recoverable only if the sub
revives) — plan for re-seed, don't block on recovery. The same personal storage account also
takes the nightly `pg_dump` backups + encrypted env copies (Neon free = 6 h PITR only).

## Secrets — source of truth and how to remint

> **Superseded by `docs/deployment/hetzner-runbook.md` §Secrets and env vars** (HETZ-03/#489), which
> carries the full per-variable matrix, the rotation commands and the create-if-missing reseed traps.
> The table below stays as the plan's summary. **The Key Vault shortcut was tried and is dead:**
> `secret list` returns the names, `secret show` is `Forbidden` while the subscription is disabled —
> no value is recoverable, everything was re-minted. Don't retry it.

| Secret | Source | How |
|---|---|---|
| `ConnectionStrings__*` (TMS + Auth, prod + staging) | Neon | owner mints fresh in Neon console (or API; project/branch IDs in memory `neon-pitr-topology`). Prefer fresh over recovering KV copies. |
| OpenIddict certs/keys (3 values) | regenerate | `scripts/gen-openiddict-keys.sh` → paste into env files. Invalidates old sessions — irrelevant pre-launch. |
| Brevo SMTP key | owner | Brevo dashboard (GH secret holds a copy but is not readable back). Sender stays `SMTP_SENDER_EMAIL` GH var. |
| `AUTH_ADMIN_*` seeds | owner | re-set on first bring-up (#210 flow). |
| `SMOKE_CLIENT_SECRET` | regenerate | new value in env + GH secret (mind the `--body` gotcha, memory `staging-env-shared-aca`). |
| `HETZNER_SSH_KEY` / host | new | generated for CD (#490); deploy-user key, not root. |

## Explicitly NOT changing

Neon as DB (ADR-0023/0024/0025 discipline intact) · GHCR image pipeline · Brevo · domain names
and OIDC topology (same public origins → no appsettings churn) · `compose.prod.yaml` laptop
parity stack · two-stage staging→prod promotion (ADR-0018 flow, new transport).

## Gotchas for the implementing session

- Images are amd64 — the box is amd64; never "fix" a pull error by enabling emulation.
- ACME will fail until DNS propagates — start DNS first (step 2), certs come up on retry alone.
- In Production, OpenIddict rejects plain HTTP: every authority/issuer URL must be the public
  `https://` origin (same rule as the parity stack — runbook gotchas section).
- `.github/workflows/*` pushes need the koniecdev token (memory `github-push-koniecdev-account`).
- `GET / -> 200` proves nothing (house rule) — smoke's fingerprint leg is the real check.
- Keep hostnames identical to ACA-era values; if the runbook matrix and reality disagree,
  the code/config in the repo wins — fix the doc.
