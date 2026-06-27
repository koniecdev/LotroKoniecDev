# TMS on Azure Container Apps + Supabase — Bring-up Plan

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:subagent-driven-development`
> (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.
>
> This is the **provider-specific deploy walkthrough** ADR-0008 §8 deferred ("authored only after the
> provider is chosen"). The provider is now chosen for TMS: **Azure Container Apps + Supabase**. It
> supersedes ADR-0008's "provider undecided" stance for TMS only; TKS stays on shared hosting.

**Goal:** Get the four TMS images (auth-api, tms-api, frontend, migrator) live on Azure Container
Apps, backed by two free Supabase Postgres projects, reachable over HTTPS on `koniec.dev`
subdomains, with a green smoke test.

**Architecture:** ACA runs the three web apps (one external HTTPS ingress each) + the migrator as a
one-shot ACA Job that gates rollout. The database is **not** on Azure — two free Supabase projects
(one per logical DB: `translation`, `auth`), reached over the IPv4 **session pooler** with SSL. DP
keyrings persist on an Azure Files share mounted at `/keys`. Images pulled from GHCR. State in an
Azure Storage remote backend.

**Tech Stack:** Terraform (`azurerm` 4.7.0), Azure CLI, Azure Container Apps + Jobs, Azure Files,
Azure Log Analytics, Supabase (managed Postgres), GHCR, GitHub Actions (existing `cd.yml` + a new
keepalive cron), Brevo/SendGrid (SMTP).

## Global Constraints

- **No code changes to the apps.** App-readiness (forwarded headers M6-02, CORS-from-config M6-03,
  DP persistence M6-04, fail-fast M6-05, prod settings M6-06) is already done. The two DbContexts
  already use named schemas (`translation`, `auth`) — so two Supabase projects map 1:1 with **zero
  code change**.
- **`:8080` is never publicly reachable except through the ACA ingress** (forwarded-headers trust is
  blanket — runbook rule 5). ACA's per-app ingress satisfies this; never expose the container port
  another way.
- **One environment only** (`prod`). No staging — YAGNI for a zero-user portfolio app.
- **Secrets never hit git.** `*.tfvars` + `*.tfstate` are git-ignored (verify Phase 1); TF state goes
  to a remote Azure Storage backend; runtime secrets are ACA `secret`s.
- **Consistency rules that bite** (runbook §"Consistency rules that bite") are law: issuer ==
  token `iss`, redirect URIs registered exactly, CORS = bare frontend origin, authority is `https`.
- **Cost reality:** R8 (target-requirements) = ≥1 always-on replica per web app. Realistic Azure
  bill ≈ **$10–20/mo** (3 tiny replicas + Log Analytics capped at 0.16 GB/day + Azure Files cents);
  **DB = $0** (Supabase free). Scale-to-zero is *safe* (keyring persists on Files) if you want to cut
  further — the only cost is cold-start latency.

---

## Your quick questions, answered

- **GitHub Pro lowest plan — enough?** More than enough. TMS is a **public** repo, so GitHub Actions
  minutes are **free and unlimited** on standard runners, and **public GHCR packages are free**. CD
  + the keepalive cron + smoke all run at $0. (Pro only matters for *private* repos like TKS.)
- **ACR — delete?** Yes. CD already pushes to **GHCR**; ACR is redundant. `acr.tf` is deleted in
  Phase 1; ACA pulls from GHCR directly.
- **MSSQL Terraform — delete?** Yes. We stay on Postgres/Supabase. `azure-sql-db.tf` is deleted in
  Phase 1 (and this kills the whole "migrate EF to MSSQL" question — zero work).
- **Azure — just a new account?** Almost. New Azure account + subscription, then ~4 one-time
  external sign-ups and a CLI login (Phase 0). Not literally "just an account," but a half-hour of
  clicking, all enumerated below.

## Division of labour

- **👤 YOU** = anything needing a human: account/payment, OAuth/portal clicks, DNS records, reading
  values off dashboards, running `terraform apply` / `az` after review.
- **🤖 ME** = all code, Terraform, workflow YAML, connection-string assembly, the secrets worksheet
  template. I hand you exact commands; you run the ones that touch your accounts.

---

## Phase 0 — Prerequisites you create (👤 YOU, one-time, ~30 min)

**Files:** none (external accounts). Output: a filled-in `iac/secrets.worksheet.md` (I provide the
template in Phase 1; it is git-ignored).

- [ ] **0.1 Azure account + subscription.** Create at portal.azure.com. If eligible, **Azure for
  Students** ($100 credit / 12 mo, no card). Otherwise pay-as-you-go ($200 / 30-day trial). Note the
  **Subscription ID**.
- [ ] **0.2 Azure CLI.** `brew install azure-cli` → `az login` → `az account set --subscription <id>`.
  Terraform reuses this login (no service principal needed for a solo human-driven deploy).
- [ ] **0.3 Two Supabase projects.** supabase.com → new org → create **two** free projects, region
  **eu-central-1 (Frankfurt)** (closest free region to Poland Central; ~20 ms):
  - `lotro-tms-translation` → holds the `translation` schema
  - `lotro-tms-auth` → holds the `auth` schema
  For each: Project Settings → Database → **Connection string → "Session pooler"** (port **5432**,
  IPv4). Note the **pooler host** (`aws-0-eu-central-1.pooler.supabase.com`), the **username**
  (`postgres.<project-ref>`), and the **DB password** you set. ⚠️ Use the **session pooler**, NOT the
  direct connection (direct is IPv6-only on free; ACA egresses IPv4).
- [ ] **0.4 SMTP provider** (auth `/health/ready` probes SMTP and fails fast without it). Free tier:
  **Brevo** (300 mails/day) or SendGrid. Note host / port (587) / username / password / a verified
  sender address.
- [ ] **0.5 DNS access to `koniec.dev`.** Confirm you can add CNAME + TXT records (Phase 7).
- [ ] **0.6 Make the 4 GHCR packages public** (so ACA pulls anonymously, no secret): github.com →
  your packages → each of `lotrokoniecdev-{auth-api,tms-api,frontend,migrator}` → Package settings →
  Change visibility → Public. (Fine — TMS is OSS.)

---

## Phase 1 — Secure the state & clean `iac/` (🤖 ME writes, 👤 YOU apply)

**Files:**
- Modify: `.gitignore` (add `*.tfstate`)
- Delete: `iac/azure-sql-db.tf`, `iac/acr.tf`, `iac/terraform.tfstate`, `iac/terraform.tfstate.backup`
- Modify: `iac/setup.tf` (add remote backend), `iac/vars.tf` (env_id→prod, drop hardcoded subscription_id default + sql_pass)
- Create: `iac/secrets.worksheet.md` (git-ignored template), `iac/terraform.tfvars` (git-ignored)

- [ ] **1.1 🤖 Fix the secret leak first.** `.gitignore` ignores `*.tfstate.*` (the backup) but **not**
  `terraform.tfstate` — which contains the SQL admin password in cleartext. Add `*.tfstate`.
- [ ] **1.2 🤖 Delete the dead files:** `iac/azure-sql-db.tf` (no MSSQL), `iac/acr.tf` (GHCR), and the
  local `iac/terraform.tfstate*` (never commit; we move to remote state).
- [ ] **1.3 👤 Bootstrap the remote state backend** (chicken-and-egg: the storage account must exist
  before TF can use it). Run once:

```bash
az group create --name rg-lotrotms-tfstate --location polandcentral
az storage account create --name sttflotrotms$RANDOM --resource-group rg-lotrotms-tfstate \
  --sku Standard_LRS --encryption-services blob
# note the chosen account name, then:
az storage container create --name tfstate --account-name <that-name> --auth-mode login
```

- [ ] **1.4 🤖 Add the backend to `iac/setup.tf`** (fill `<that-name>`):

```hcl
terraform {
  required_providers {
    azurerm = { source = "hashicorp/azurerm", version = "4.7.0" }
  }
  backend "azurerm" {
    resource_group_name  = "rg-lotrotms-tfstate"
    storage_account_name = "<that-name>"
    container_name       = "tfstate"
    key                  = "prod.terraform.tfstate"
  }
}
```

- [ ] **1.5 🤖 Clean `iac/vars.tf`:** set `env_id` default to `prod`; **remove** the hardcoded
  `subscription_id` default (pass it via `terraform.tfvars`); **remove** the now-unused `sql_pass`.
- [ ] **1.6 👤 Create git-ignored `iac/terraform.tfvars`** (matched by `*.tfvars`):

```hcl
subscription_id = "<your-subscription-id>"
```

- [ ] **1.7 👤 Verify nothing secret is staged, then commit the cleanup:**

```bash
git -C ~/RiderProjects/LotroKoniecDev status            # confirm NO *.tfstate / *.tfvars listed
git add .gitignore iac/ && git commit -m "infra: drop MSSQL+ACR, remote tfstate backend, fix gitignore"
```

- [ ] **1.8 👤 `terraform init` (migrates state to the backend):** `cd iac && terraform init`.

---

## Phase 2 — DP keyring storage (R4) (🤖 ME)

**Files:** Create `iac/storage.tf`.

Azure Files share (ReadWriteMany) for the auth + frontend keyrings, registered as ACA environment
storage. Without it, every deploy/scale logs everyone out and breaks antiforgery + reset links.

- [ ] **2.1 🤖** `azurerm_storage_account` (Standard_LRS) + `azurerm_storage_share` `keys` (a few GB)
  + `azurerm_container_app_environment_storage` named `keys` mounting that share into the ACA env
  (`access_mode = "ReadWrite"`). (Code authored against the existing `app_env` in
  `azure_container_app_env.tf`.)
- [ ] **2.2 👤 `terraform plan`** — expect the storage account, share, and env-storage to be added.

---

## Phase 3 — The three Container Apps (R1/R2/R7/R8) (🤖 ME)

**Files:** Rewrite `iac/azure-container-apps.tf` (one placeholder → three real apps).

This is the load-bearing task. Per app: external ingress, **`target_port = 8080`** (the placeholder's
`80` is wrong — the apps listen on 8080), `min_replicas = 1` (R8), `max_replicas = 1`, GHCR image,
the full env set from the runbook matrix, plus the keyring volume for auth + frontend.

- [ ] **3.1 🤖 auth-api** — image `ghcr.io/koniecdev/lotrokoniecdev-auth-api:latest`, external ingress
  `target_port 8080`, `volume`/`volume_mounts` → env-storage `keys` at `/keys`, env per runbook
  §auth-api (incl. `DataProtection__KeyRingPath=/keys`, `ASPNETCORE_URLS=http://+:8080`).
- [ ] **3.2 🤖 tms-api** — image `…-tms-api:latest`, external ingress `target_port 8080`, env per
  runbook §tms-api (no keyring mount). Public origin is required (CLI/WPF download translation files).
- [ ] **3.3 🤖 frontend** — image `…-frontend:latest`, external ingress `target_port 8080`, keyring
  volume at `/keys`, env per runbook §frontend.
- [ ] **3.4 👤 First `terraform apply`** (apps come up on their default `*.azurecontainerapps.io`
  FQDNs; OIDC origins are wired to the *custom* domains in Phase 6, so login won't fully work until
  Phase 7 — that's expected):

```bash
cd iac && terraform apply
az containerapp list --resource-group rg-lotrotms-dev-polc-001 -o table   # note the 3 FQDNs
```

- [ ] **3.5 👤 Verify each container at least boots** (will 500 until secrets+DB exist — Phases 4–5;
  that's fine here):

```bash
az containerapp logs show -n lotrotms-auth-api-prod -g rg-lotrotms-dev-polc-001 --tail 50
```

---

## Phase 4 — Secrets wiring (R3) (🤖 ME assembles, 👤 YOU supply values)

**Files:** `iac/terraform.tfvars` (git-ignored) + `secret`/`env` blocks in the app + job TF.

- [ ] **4.1 👤 Generate the OpenIddict keys** (3 secrets):

```bash
cd ~/RiderProjects/LotroKoniecDev && scripts/gen-openiddict-keys.sh
# copy the 3 KEY=VALUE lines into iac/terraform.tfvars (as TF variables)
```

- [ ] **4.2 🤖 Assemble the two Supabase connection strings** (session pooler, SSL, capped pool) into
  tfvars vars:

```
ConnectionStrings__TranslationDatabase =
  Host=aws-0-eu-central-1.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.<translation-ref>;Password=<pass>;Ssl Mode=Require;Maximum Pool Size=10
ConnectionStrings__AuthDatabase =
  Host=aws-0-eu-central-1.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.<auth-ref>;Password=<pass>;Ssl Mode=Require;Maximum Pool Size=10
```

(`Ssl Mode=Require` only — drop `Trust Server Certificate`; Supabase presents a valid cert. Schema is
set by the app, so `Database=postgres` is correct for both.)

- [ ] **4.3 🤖 Map every `secret` var into ACA `secret` blocks + reference them from `env`** (connection
  strings, the 3 OpenIddict secrets, the SMTP password) across auth-api / tms-api / migrator job.
- [ ] **4.4 👤 Fill SMTP env** (auth-api) from Phase 0.4: `Email__Host/Port/Mode=StartTls/SenderEmail/
  Sender/Username/Password`.
- [ ] **4.5 👤 `terraform apply`** — secrets land, apps restart with a real config.

---

## Phase 5 — Migrations as an ACA Job (R6) (🤖 ME)

**Files:** Create `iac/migrator-job.tf`.

The compose migrator (`depends_on: service_completed_successfully`) has no ACA equivalent — express it
as an ACA **Job**. It runs the migrator image to completion against **both** Supabase projects before
the APIs serve real traffic.

- [ ] **5.1 🤖** `azurerm_container_app_job` — `replica_completion_count = 1`, `trigger_type = "Manual"`,
  image `ghcr.io/koniecdev/lotrokoniecdev-migrator:latest`, env = the two connection-string secrets +
  `ASPNETCORE_ENVIRONMENT=Production`.
- [ ] **5.2 👤 `terraform apply`** then start the job:

```bash
az containerapp job start -n lotrotms-migrator-prod -g rg-lotrotms-dev-polc-001
az containerapp job execution list -n lotrotms-migrator-prod -g rg-lotrotms-dev-polc-001 -o table
# tail logs of the latest execution; expect:
#   == TRANSLATION MIGRATOR DONE ==  /  == AUTH MIGRATOR DONE ==  /  == MIGRATOR COMPLETE ==
```

- [ ] **5.3 👤 Verify schema** in each Supabase project (SQL editor):
  `SELECT "MigrationId" FROM translation."__EFMigrationsHistory";` (translation project) and the
  `auth.` equivalent (auth project).

---

## Phase 6 — OIDC consistency wiring (the part that 401s you) (🤖 ME)

**Files:** env blocks across the three apps (set to the **custom-domain** origins of Phase 7).

Set these to the final public origins **now** so you configure them once (changing them later forces
re-registering redirect URIs). Target origins:
- frontend → `https://lotro.koniec.dev`
- auth → `https://auth.lotro.koniec.dev`
- tms → `https://tms.lotro.koniec.dev`

- [ ] **6.1 🤖 auth-api:** `OpenIddict__Issuer=https://auth.lotro.koniec.dev`,
  `OpenIddict__WebClient__RedirectUris__0=https://lotro.koniec.dev/callback`,
  `…PostLogoutRedirectUris__0=https://lotro.koniec.dev`, `Cors__AllowedOrigins__0=https://lotro.koniec.dev`.
- [ ] **6.2 🤖 tms-api:** `Auth__Issuer=https://auth.lotro.koniec.dev` (byte-identical to auth's
  Issuer), `Auth__Authority=https://auth.lotro.koniec.dev`, `Cors__AllowedOrigins__0=https://lotro.koniec.dev`.
- [ ] **6.3 🤖 frontend:** `AuthSystem__Authority` + `AuthSystem__BaseUrl=https://auth.lotro.koniec.dev(/)`,
  `TranslationSystem__BaseUrl=https://tms.lotro.koniec.dev/`, `AuthSystem__ClientId=lotrokoniecdev-web`.
- [ ] **6.4 🤖 Re-check the 4 consistency rules** (runbook): issuer==iss, redirect URIs exact (scheme/
  host/slash), CORS bare origin (lowercase, no trailing slash), authority is `https`.

---

## Phase 7 — Custom domains + managed certs (👤 YOU, 🤖 commands provided)

**Files:** none (DNS at registrar + `az` binding). Done via `az` (simpler than TF for the cert dance).

For each of `lotro` / `auth` / `tms`.lotro.koniec.dev → its ACA app:

- [ ] **7.1 👤 Add DNS records** at your `koniec.dev` provider: a `CNAME` from the subdomain to the
  app's `*.azurecontainerapps.io` FQDN, plus the `asuid.<sub>` `TXT` validation record ACA prints.
- [ ] **7.2 👤 Bind + provision the free managed cert:**

```bash
az containerapp hostname add  -n lotrotms-frontend-prod -g rg-lotrotms-dev-polc-001 --hostname lotro.koniec.dev
az containerapp hostname bind -n lotrotms-frontend-prod -g rg-lotrotms-dev-polc-001 --hostname lotro.koniec.dev \
  --environment lotrotmsenvprod --validation-method CNAME
# repeat for auth.lotro.koniec.dev → -auth-api, tms.lotro.koniec.dev → -tms-api
```

- [ ] **7.3 👤 Browser login smoke:** open `https://lotro.koniec.dev`, complete an OIDC login.

---

## Phase 8 — Keepalive + cold-start posture (🤖 ME)

**Files:** Create `.github/workflows/supabase-keepalive.yml`; repo secrets `SUPABASE_KEEPALIVE_TRANSLATION`, `SUPABASE_KEEPALIVE_AUTH`.

Supabase free pauses a project after **7 days** of DB inactivity (~30 s wake). A daily trivial query
resets the timer on **both** projects.

- [ ] **8.1 🤖 Workflow** (`schedule: cron daily`) runs `psql "$CONN" -c 'select 1'` against both
  connection strings (stored as repo secrets; public-repo Actions are free).
- [ ] **8.2 👤 Add the two repo secrets** (the same two pooler connection strings).
- [ ] **8.3 Decide replica floor.** Default `min_replicas = 1` (R8 — snappy, the ~$10–20/mo baseline).
  Optional cost lever: drop frontend/auth to `0` (keyring persists on Files, so it's *safe*) and eat
  cold starts — fine when idle, set back to `1` while actively interviewing.

---

## Phase 9 — Smoke test + seed (👤 YOU)

- [ ] **9.1 👤 Run the existing smoke test** against the real URLs:

```bash
cd ~/RiderProjects/LotroKoniecDev
SMOKE_CLIENT_SECRET="<OpenIddict__ApiClientSecret from tfvars>" scripts/smoke.sh \
  --auth-url https://auth.lotro.koniec.dev \
  --tms-url  https://tms.lotro.koniec.dev \
  --frontend-url https://lotro.koniec.dev
```

Expect ✓ on legs 1–3; **leg 4 may `⚠` 404** until you import data (next step). A **401 on leg 3 with a
valid token = issuer/audience/JWKS mismatch** → revisit Phase 6.

- [ ] **9.2 👤 Seed the first import** so the distribution endpoint serves a file (otherwise the app is
  empty): either set `Bootstrap__Enabled=true` + the `Bootstrap__*` paths on tms-api for one boot, or
  run the upload/import flow once. Re-run 9.1 → leg 4 should go green (200 + 304).

---

## Self-review (spec coverage)

- R1 ingress/TLS → Phase 3 (ingress) + Phase 7 (custom domain + managed cert). ✅
- R2 forwarded headers → app already does it; ACA sets `X-Forwarded-*`. ✅
- R3 secrets → Phase 4. ✅  R4 keyring RWX `/keys` → Phase 2 + mounts in Phase 3. ✅
- R5 Postgres SSL, two DBs → Supabase ×2 (Phase 0.3) + connection strings (Phase 4). ✅
- R6 pre-deploy migration job → Phase 5. ✅  R7 env injection → Phases 3/4/6. ✅
- R8 ≥1 replica → Phase 3 (`min_replicas=1`) + Phase 8.3. ✅
- R9 egress (SMTP/OTLP/DB/forum) → ACA managed egress; SMTP Phase 0.4/4.4. ✅
- R10 OTLP → optional, left disabled (empty endpoint); wire Azure Monitor later. ✅ (deferred, YAGNI)
- R11 GHCR pull + non-root + JSON logs + Log Analytics → Phase 0.6 + existing env/image. ✅
- Secret leak (tfstate) → Phase 1.1. ✅  MSSQL/ACR removal → Phase 1.2. ✅
- Supabase pause → Phase 8. ✅  OIDC consistency → Phase 6. ✅

**Open decision (yours, low-stakes):** custom domain scheme — `lotro.koniec.dev` (used throughout, to
match the runbook placeholders) vs `app.lotro.koniec.dev`. Pick before Phase 6 (it's the redirect/CORS
origin). Default assumed: `lotro.koniec.dev`.
