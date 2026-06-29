# Prod DB migration: Supabase → Neon — Plan

> **Next-session execution plan.** Moves the production database off two free Supabase projects onto
> a single **Neon** project (Postgres **18**), with **zero data loss risk** — prod has no users and
> only the auto-seeded admin (re-created on `auth-api` startup from Key Vault). Mirrors the style of
> `azure-supabase-bring-up-plan.md`; defers to `runbook.md` for the standing mechanics.
>
> **Legend:** 👤 = operator (you, manual) · 🤖 = agent (next session, in-repo changes).
> **The migration itself is a Key Vault secret rotation + one migrator run** — the app layer is
> 12-factor (ADR-0008 §3) and does not care where Postgres lives (ADR-0013 already proven the swap is
> "change the two `ConnectionStrings__*`").

**Goal:** Production Postgres served by Neon (`lotro-translator-prod` project, PG 18), Supabase
decommissioned, all docs/ADRs consistent, green smoke test.

**Architecture:** ACA apps + migrator job (`rg-lotrotms-prod-polc-001`) read `connection-string-translation`
/ `connection-string-auth` from Key Vault `lotrotms-kv-prod` via the `lotrotms-aca-prod` managed
identity (ADR-0013). Versionless secret URIs mean a new secret **version** is picked up on the next
revision — **no Terraform change**. Both EF write contexts use disjoint schemas (`translation`,
`authsystem`), each with its own `__EFMigrationsHistory`, so they coexist safely; we keep them in
**two databases** (`lotro_translation` + `lotro_auth`) inside one Neon project to stay 1:1 with the
dev/parity compose stacks (`scripts/init-postgres.sh`).

**Tech stack:** Neon (managed Postgres 18) · Npgsql.EntityFrameworkCore.PostgreSQL 10.0.1 (PG-18-aware) ·
Azure Container Apps + Key Vault · `az` CLI.

---

## Global constraints (apply to every task)

- **Never write the Neon password into any tracked file.** This plan, ADRs, runbook, tfvars — all are
  git-tracked and gated by `gitleaks`. Use the placeholder `<NEON_PASSWORD>`; the real value lives
  **only** in Key Vault. The connection string given out-of-band is:
  `postgresql://neondb_owner:<NEON_PASSWORD>@ep-small-water-as8kmmo2.c-4.eu-central-1.aws.neon.tech/neondb?sslmode=require`
- **Zero-warnings build gate** is repo-wide; doc-only changes still run through `pr-verify`/`ci`.
- **Git house rules:** branch off `main`, rebase (never merge `main` in), squash-merge, **never delete
  branches**, PR body `Closes #<n>` if a ticket is cut.
- **Code wins over docs.** Auth schema is `authsystem` (`DatabaseSchemas.Auth`), **not** `auth` — the
  runbook's verify query is stale and is fixed in Task 2.3.

## Decisions taken (change before starting if you disagree)

1. **Two databases in one Neon project** — `lotro_translation` (TMS) + `lotro_auth` (Auth), not the
   default `neondb`. *Why:* exact parity with dev/parity compose (two DBs via `init-postgres.sh`);
   runbook + whole architecture say "two databases"; no dev↔prod drift. *Cost:* one `CREATE DATABASE`.
   The default `neondb` is left empty (or dropped).
2. **Direct (non-pooled) endpoint for everything** (apps + migrator), `Ssl Mode=Require`,
   `Maximum Pool Size=10`. *Why:* EF migrations are reliable only on a direct connection (Neon's
   PgBouncer transaction-mode pooler breaks some DDL/advisory paths), and migrator + apps share the
   same two secrets, so they must use the same endpoint. With no users, the direct cap (~112 conns on
   free 0.25 CU) dwarfs 2 apps × pool 10. *Trade-off, one line:* when real traffic arrives, switch
   apps to the `-pooler` host (needs splitting the secrets, or pooler-only-for-apps) — noted in ADR-0014.
3. **Rotate via `az keyvault secret set` ×2**, not a full `seed-keyvault.sh` re-run (which demands all
   8 `SEED_*`). Only the two connection strings change.
4. **No data migration.** Prod holds only the admin seed, re-created by `auth-api` on startup from
   `admin-password` in Key Vault. Empty Neon → migrator builds schema → `auth-api` auto-seeds admin.
5. **No keepalive needed.** Neon free *autosuspends* compute (wakes automatically, ~sub-second); it
   does **not** pause-and-require-wake after 7 days like Supabase free. There is no keepalive workflow
   in the repo to remove (Supabase bring-up Phase 8 was never implemented).
6. **New ADR-0014** records the provider swap; the Supabase bring-up doc gets a "superseded" banner
   (kept for history — ADR-0012 links it; do not rename/delete).

---

## Phase 1 — Provision Neon (👤)

- [ ] **1.1 Create the two databases** in the `lotro-translator-prod` project. Neon Console →
  *Databases* → **Add database** twice (owner `neondb_owner`): `lotro_translation`, then `lotro_auth`.
  Or via SQL (Neon SQL Editor / `psql` as `neondb_owner`):

```sql
CREATE DATABASE lotro_translation;
CREATE DATABASE lotro_auth;
```

  The host (compute endpoint) is shared across databases in a Neon branch; only `Database=` differs.

- [ ] **1.2 Confirm the direct host** (the one **without** `-pooler`):
  `ep-small-water-as8kmmo2.c-4.eu-central-1.aws.neon.tech`. (Pooler host
  `ep-small-water-as8kmmo2-pooler.c-4.eu-central-1.aws.neon.tech` is **not** used — see Decision 2.)

- [ ] **1.3 Assemble the two Npgsql connection strings** (keep `<NEON_PASSWORD>` only in your shell,
  never in a file):

```
ConnectionStrings__TranslationDatabase =
  Host=ep-small-water-as8kmmo2.c-4.eu-central-1.aws.neon.tech;Port=5432;Database=lotro_translation;Username=neondb_owner;Password=<NEON_PASSWORD>;Ssl Mode=Require;Maximum Pool Size=10

ConnectionStrings__AuthDatabase =
  Host=ep-small-water-as8kmmo2.c-4.eu-central-1.aws.neon.tech;Port=5432;Database=lotro_auth;Username=neondb_owner;Password=<NEON_PASSWORD>;Ssl Mode=Require;Maximum Pool Size=10
```

  `Ssl Mode=Require` only (Neon presents a publicly-trusted cert — no `Trust Server Certificate`).
  Npgsql 10 negotiates SNI, so no `options=endpoint=…` hack is needed.

- [ ] **1.4 (Optional) Quick reachability check** from your machine before touching prod:

```bash
psql "host=ep-small-water-as8kmmo2.c-4.eu-central-1.aws.neon.tech port=5432 dbname=lotro_translation user=neondb_owner sslmode=require" -c '\conninfo'
psql "host=ep-small-water-as8kmmo2.c-4.eu-central-1.aws.neon.tech port=5432 dbname=lotro_auth        user=neondb_owner sslmode=require" -c '\conninfo'
```

---

## Phase 2 — In-repo changes (🤖, one PR)

Branch: `git checkout main && git pull && git checkout -b migrate-prod-db-to-neon`.

- [ ] **2.1 Write ADR-0014** `docs/adr/0014-production-database-on-neon.md` (use `/adr`; mirror the
  house format of ADR-0013). Capture: Status Accepted / Date 2026-06-30 / Related ADR-0008 (§R5 DB),
  ADR-0013 (the secrets it rotates), ADR-0012, `azure-supabase-bring-up-plan.md`, `runbook.md`.
  - **Context:** Supabase free = two projects, 7-day pause needing keepalive, separate `<ref>`
    usernames; pre-release, no users, free swap (ADR-0002).
  - **Decision:** Neon (PG 18), **one project, two databases** (`lotro_translation` + `lotro_auth`)
    on the **direct** endpoint; secrets rotated in Key Vault (no TF change); no keepalive (autosuspend).
  - **Consequences:** dev/parity unchanged (still two DBs → parity holds); pooler is a documented
    future step; Npgsql 10.0.1 is PG-18-aware (uuidv7/virtual columns available, unused for now).

- [ ] **2.2 Update `runbook.md` — Supabase → Neon** (2 hits, lines ~398–399). Change the inline
  comments on the seed example from "Supabase TMS/Auth connection string" to "Neon TMS/Auth
  connection string". In the **Inputs** table (~line 460) the examples already use generic `Host=db`
  — leave them, but ensure the `Database=` values read `lotro_translation` / `lotro_auth`.

- [ ] **2.3 Fix the stale Auth schema in `runbook.md` (Verifying §, ~line 519).** The verify query
  reads `auth."__EFMigrationsHistory"` but the real schema is `authsystem` (`DatabaseSchemas.Auth`).
  Replace `auth.` → `authsystem.`:

```bash
# applied Auth migrations
psql "$ConnectionStrings__AuthDatabase" -c 'SELECT "MigrationId" FROM authsystem."__EFMigrationsHistory" ORDER BY "MigrationId";'
```

  Also correct the Strategy bullet ("schema `auth`") to `authsystem` in the same file.

- [ ] **2.4 Banner the Supabase bring-up doc.** Add a one-line note at the very top of
  `docs/deployment/azure-supabase-bring-up-plan.md` (do not rename — ADR-0012 links it):

```markdown
> **⚠️ Superseded for the database layer (2026-06-30):** prod Postgres moved to **Neon** — see
> `docs/deployment/neon-migration-plan.md` and ADR-0014. The ACA / Key Vault / CD mechanics below
> still hold; only the Supabase-specific DB steps (Phase 0.3, 4.2, 5.3, 8) are obsolete.
```

- [ ] **2.5 Build + commit + PR.** `dotnet build LotroKoniecDev.slnx` (doc-only, but keep the gate
  green), then `git add -A && git commit` (with `Co-Authored-By` footer) → `gh pr create --fill`.
  Merge with `gh pr merge --squash` (no `--delete-branch`). This PR is **independent of the cutover**
  and can land first.

---

## Phase 3 — Rotate the Key Vault secrets (👤)

> Needs `Key Vault Secrets Officer` on `lotrotms-kv-prod` (the seed script's caller role).

- [ ] **3.1 Log in** to the prod subscription: `az login`.

- [ ] **3.2 Set the two new versions** (the real `<NEON_PASSWORD>` is inline here in your shell only —
  it never enters a file). Mind the leading space trick so the command is **not** saved to shell
  history (or `export` the value first):

```bash
KV=lotrotms-kv-prod
NEON_HOST=ep-small-water-as8kmmo2.c-4.eu-central-1.aws.neon.tech

 az keyvault secret set --vault-name "$KV" --name connection-string-translation \
   --value "Host=$NEON_HOST;Port=5432;Database=lotro_translation;Username=neondb_owner;Password=<NEON_PASSWORD>;Ssl Mode=Require;Maximum Pool Size=10" -o none

 az keyvault secret set --vault-name "$KV" --name connection-string-auth \
   --value "Host=$NEON_HOST;Port=5432;Database=lotro_auth;Username=neondb_owner;Password=<NEON_PASSWORD>;Ssl Mode=Require;Maximum Pool Size=10" -o none
```

- [ ] **3.3 Confirm new versions exist** (values not printed):

```bash
az keyvault secret show --vault-name lotrotms-kv-prod --name connection-string-translation --query "attributes.updated" -o tsv
az keyvault secret show --vault-name lotrotms-kv-prod --name connection-string-auth        --query "attributes.updated" -o tsv
```

---

## Phase 4 — Migrate schema + cut over (👤)

Two paths — **A** is the surgical manual cutover (no image rebuild), **B** is "just run CD". Pick A
for control; B is fine since the secrets are already rotated.

### Path A — manual (recommended for a clean DB swap)

- [ ] **4.1 Run the migrator against Neon** (it reads the rotated secrets; builds both schemas):

```bash
RG=rg-lotrotms-prod-polc-001
az containerapp job start -n lotrotms-migrator-prod -g "$RG" -o none
# poll until Succeeded
az containerapp job execution list -n lotrotms-migrator-prod -g "$RG" \
  --query "sort_by([],&properties.startTime)[-1].properties.status" -o tsv
```

  Expect the latest execution `Succeeded`. (Logs tail: `== TRANSLATION MIGRATOR DONE ==` /
  `== AUTH MIGRATOR DONE ==` / `== MIGRATOR COMPLETE ==`.)

- [ ] **4.2 Roll the apps to a new revision** so they pick up the new secret version (same image):

```bash
RG=rg-lotrotms-prod-polc-001
for app in lotrotms-auth-api-prod lotrotms-tms-api-prod lotrotms-frontend-prod; do
  cur=$(az containerapp show -n "$app" -g "$RG" --query "properties.template.containers[0].image" -o tsv)
  echo "Re-rolling $app → $cur"
  az containerapp update -n "$app" -g "$RG" --image "$cur" -o none
done
```

  `auth-api`'s new revision seeds the admin into Neon on startup.

### Path B — CD (rebuilds images, also migrates)

- [ ] **4.B Re-run CD** (Actions → `cd.yml` → *Run workflow* on `main`, or push an empty commit).
  The R6 gate runs the migrator against Neon, then rolls all three apps. Approve the `production`
  environment gate when prompted.

### Verify schema landed on Neon (either path)

- [ ] **4.3** From your machine (uses the rotated strings — set them in your shell first, with the real
  password):

```bash
psql "$ConnectionStrings__TranslationDatabase" -c 'SELECT "MigrationId" FROM translation."__EFMigrationsHistory"  ORDER BY "MigrationId";'
psql "$ConnectionStrings__AuthDatabase"        -c 'SELECT "MigrationId" FROM authsystem."__EFMigrationsHistory" ORDER BY "MigrationId";'
```

  Both must list every migration in `src/**/Persistence/Migrations/` (Translation: InitialCreate …
  AddTranslatorAggregate; Auth: InitialAuthSchema).

---

## Phase 5 — Smoke test + validation (👤)

- [ ] **5.1 Run the post-deploy smoke** (`SMOKE_CLIENT_SECRET` = the `openiddict-api-client-secret`
  value):

```bash
scripts/smoke.sh \
  --auth-url     https://auth.lotro-translator.pl \
  --tms-url      https://tms.lotro-translator.pl \
  --frontend-url https://app.lotro-translator.pl \
  --client-secret "$SMOKE_CLIENT_SECRET"
```

  All four legs green (health · client-credentials token · tms accepts the token = 403-not-401 ·
  distribution ETag/304; leg 4 may WARN if no artifact is seeded — that is still "up").

- [ ] **5.2 Browser login** at `https://app.lotro-translator.pl` with the seeded admin
  (`admin-password` from Key Vault) — confirms `auth-api` re-seeded the admin into Neon.

- [ ] **5.3 Health probes:**

```bash
curl -fsS https://auth.lotro-translator.pl/health/ready && echo
curl -fsS https://tms.lotro-translator.pl/health/ready && echo
```

---

## Phase 6 — Decommission Supabase (👤, only after Phase 5 is green)

- [ ] **6.1** In the Supabase dashboard, **pause then delete** both projects (translation + auth).
  Keep them paused for a day or two if you want a fallback window (see Rollback).
- [ ] **6.2** Sanity grep — no live config points at Supabase (docs are handled in Phase 2):

```bash
rg -rni supabase --glob '!**/bin/**' --glob '!**/obj/**' --glob '!docs/**'   # expect: no hits
```

---

## Phase 7 — (Optional) bump compose Postgres 17 → 18 for parity (🤖)

Independent nicety — the prod-parity stack should match prod's major version. **Gotcha:** the official
`postgres:18` image moved default `PGDATA` to `/var/lib/postgresql/18/docker` and `VOLUME` to
`/var/lib/postgresql`; the current mounts target `/var/lib/postgresql/data`.

- [ ] **7.1** In `compose.yaml` and `compose.prod.yaml`: `image: postgres:17-alpine` → `postgres:18-alpine`,
  and change the volume mount target from `…:/var/lib/postgresql/data` to `…:/var/lib/postgresql`
  (the named volume is empty in dev anyway; `docker compose down -v` first to be safe).
- [ ] **7.2** Boot fresh (`docker compose down -v && scripts/up.sh`) and confirm the migrator + host
  Kestrels come up green. (Separate small PR; do not couple to the prod cutover.)

---

## Rollback

Supabase data is untouched until Phase 6, so rollback before then is trivial:

1. Re-`az keyvault secret set` the two secrets back to the Supabase connection strings (Path: same as
   Phase 3, old values).
2. Re-roll the apps (Phase 4.2). Supabase schema/data are intact; no migrator run needed.

After Phase 6 (Supabase deleted) the only path forward is fixing Neon (forward-only, ADR-0008 §6).

## Self-review / Definition of Done

- [ ] Neon hosts `lotro_translation` + `lotro_auth`; both `__EFMigrationsHistory` tables list all
      migrations (Phase 4.3).
- [ ] `smoke.sh` all-green against the public origins; admin browser login works (Phase 5).
- [ ] Key Vault holds Neon connection strings; **no** plaintext password in any tracked file
      (`rg -n 'npg_' .` → no hits).
- [ ] ADR-0014 merged; runbook says Neon + `authsystem` schema; bring-up doc bannered (Phase 2).
- [ ] Supabase projects deleted; `rg -rni supabase` outside `docs/` → no hits (Phase 6).
- [ ] Update memory `prod-azure-deploy-topology` + `prod-domain-and-email-brevo` siblings: prod DB is
      now Neon (one project, two DBs, direct endpoint), Supabase retired.
