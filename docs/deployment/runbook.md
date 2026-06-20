# Deployment runbook

> Operator-facing procedures for running LotroKoniecDev on a container host. Provider-neutral by
> design (ADR-0008): everything here is "a 12-factor OCI image behind a TLS ingress" and applies to
> Azure Container Apps, AWS ECS/App Runner, or a plain Docker host alike.
>
> **Scope today:** database migrations (M6-10). The full bring-up walkthrough, secrets handling, and
> the Azure⇄AWS service mapping land with M6-11 / M6-12 and extend this file.

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
