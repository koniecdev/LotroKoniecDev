# CLAUDE.md — LotroKoniecDev

> Project memory — **self-contained**: a fresh clone has everything the AI needs, with no
> machine-local config required. When a doc and the code disagree, **the code wins**: read the
> file, use what's there, and fix or flag the stale doc.

## What this is

A **LOTRO Polish translation platform** on **.NET 10 / C# 14** (`Directory.Build.props` is
authoritative) — **two bounded contexts in one repo**, integrating through a file contract:

1. **Patcher** (shipped, **stable**) — CLI that exports English texts from the game's binary DAT
   file (`export`), injects `||`-format Polish translations back (`patch`), and launches the game
   (`launch`). An Avalonia player app (M4, ADR-0033) will reuse its Application handlers.
2. **TMS — Translation Management System** (built — M2 backend + M3 frontend delivered, deployed
   via the M6 pipeline) — PostgreSQL + Web API + Blazor SSR + self-hosted OpenIddict auth:
   translators import the CLI export, edit with review workflow, and export `polish.txt` back
   for patching.

**Architectural identity:** every TMS pattern is lifted **1:1 from TheKittySaver**
(`~/RiderProjects/TheKittySaver` — the canonical reference for Vertical Slice Architecture, DDD
domain, Result monad, the OpenIddict auth server, Docker/compose, testing discipline), with
**one repo-wide deviation: NO MEDIATOR (ADR-0001)**. KittySaver uses `Mediator.SourceGenerator`;
every lifted slice is de-mediatorized on entry (recipe below). `Mediator`/`MediatR` packages are
forbidden — never add them back.

## Project status — deployed, pre-launch (no real users yet)

Active development. **Done:** M1 (patcher, empirically proven), **M2 — TMS backend** (all slices
incl. CLI auto-download M2-20; the forum watcher M2-18 / #85 was **deliberately cut to post-MVP**
— owner decision 2026-06, recorded in ADR-0030; game-version registration stays manual, and
neither the loop nor a contributor should pick #85 up), **M3 — Blazor SSR frontend** (manual
QA pass QA-FE / #275 still open), **M6 — deployment** (CD over ssh to a Hetzner VPS + Neon,
staging + prod — ADRs 0008–0029, hosting moved off Azure by ADR-0034). **Open fronts:** M7
game-content catalog (epic #362, spec 0008
agreed, not started), LEGAL/GDPR pack (epic #459 — 01/02/03 landed, incl. the two-phase account
deletion of ADR-0031; 04–07 open), QA-FE manual pass (#275), M4 desktop player app (Avalonia —
ADR-0033), post-MVP TP
backlog (epic #377).

No real users yet, so **API/code breaking changes are free** — no back-compat shims, no
deprecation windows. The one exception is the **database schema**: the stack is deployed with
zero-downtime CD, so migrations follow ADR-0023 (forward-only, N-1 backward-compatible,
expand→backfill→contract) regardless of user count. Live backlog: `gh issue list`; where an
issue body conflicts with this file, **this file wins** — align the ticket before coding.

## Architecture — two bounded contexts, one file contract

```
src/
  Patcher/LotroKoniecDev.{Primitives,Domain,Application,Infrastructure,Cli}               ← PATCHER (stable)
  SharedKernel/LotroKoniecDev.SharedKernel                                                ← TMS-side building blocks (lifted)
  TranslationSystem/LotroKoniecDev.TranslationSystem.{Primitives,Domain,ReadModels,ReadModels.EntityFramework,Projections,Persistence,Contracts,API}
  AuthSystem/LotroKoniecDev.AuthSystem.{API,Domain,Infrastructure,Persistence,Contracts}  ← self-hosted OpenIddict (lifted)
  Frontend/LotroKoniecDev.Frontend                                                        ← Blazor Static SSR, OIDC RP
  Utilities/LotroKoniecDev.{Hateoas,Hateoas.Abstractions,Logging,Options}
```

(`TranslationSystem.Projections` is the in-house precomputed-translation-file store behind the
distribution endpoint — not part of the KittySaver lift map below.)

**The contexts share a data contract, not code: the `||` translation file.** CLI `export` →
`exported.txt` → TMS import; TMS export → `polish.txt` → CLI `patch`. Each context owns its own
parser/serializer; **golden fixture files + round-trip tests on both sides** guard against format
drift, and the format itself changes only via ADR. The TMS never references `datexport.dll`/DAT
code (it runs in Linux containers — Docker Compose on a Hetzner VPS in prod); the patcher never touches the
DB (it runs on a Windows gaming box). Distribution is HTTP, not integration: the CLI launch flow
auto-downloads the current translation file from the TMS API (ETag-cached; M2-20), and the
Avalonia app (M4) is a GUI over the same patcher handlers + download.

### Patcher — stable (shipped & empirically proven)

Strict Clean Architecture; dependency rule: **Cli / Infrastructure → Application → Domain →
Primitives**.

| Project | Role |
|---|---|
| `LotroKoniecDev.Cli` | Spectre.Console commands; resolves paths, reports, maps `Error` → exit code |
| `LotroKoniecDev.Application` | feature slices (`Features/<Area>/`): command/query records + slim handlers + services; `Abstractions/` ports incl. in-house `Messaging/` interfaces |
| `LotroKoniecDev.Domain` | `Result`/`Maybe` monads, `Error` + `DomainErrors`, DAT models (`SubFile`, `Fragment`, `Translation`), `VarLenEncoder` |
| `LotroKoniecDev.Infrastructure` | native interop (`datexport.dll`, x86 Windows), DAT handler, forum fetcher, launcher |
| `LotroKoniecDev.Primitives` | constants + enums, zero dependencies |
| `tests/LotroKoniecDev.Tests.{Unit,Infrastructure,E2E}` | patcher tests (E2E Windows-only via `SkippableFact`) |

**Stable means:** the patcher is shipped and empirically proven, so the bar for touching it is
high — but it is **no longer frozen** (ADR-0002 amendment, 2026-06-25). Refactors, renames and
restructuring are allowed when they earn their keep (the `src/Patcher/` grouping was the first);
**any change must keep every existing test green without touching its assertions**, and behavior
proven in `docs/knowledge-base/` must not regress. The TMS still deliberately duplicates the few
tiny building blocks it needs (Result/Maybe/Error shapes, messaging interfaces — they arrive
inside the lifted SharedKernel); consolidating that duplication is an opt-in cleanup, not a
mandate. The DAT/`||` file format still changes only via ADR + updated golden fixtures.

### TMS — the KittySaver lift map

The lift itself is **done** — every row below exists in the repo. The map stays as the pattern
reference: a **new** TMS slice mirrors the nearest existing sibling slice in this repo first,
and falls back to the KittySaver original (+ the de-mediatorization recipe) when no sibling fits.

| Building… | Mirror from `~/RiderProjects/TheKittySaver` | Lift notes |
|---|---|---|
| `SharedKernel` | `src/SharedKernel/TheKittySaver.SharedKernel` | Drop the `Mediator.Abstractions` package; add `Messaging/` with in-house `ICommand(Handler)`/`IQuery(Handler)` (same shapes as patcher `Application/Abstractions/Messaging/`). Keep monads, BuildingBlocks, `Ensure`, `StronglyTypedId` |
| `TranslationSystem.Primitives` | `…AdoptionSystem.Primitives` | Strongly-typed ID types + enums per aggregate (`Aggregates/<X>Aggregate/`), shared by Domain, ReadModels and Contracts; the `StronglyTypedId` base stays in SharedKernel (ADR-0002 amendment 2026-06-12) |
| `TranslationSystem.Domain` | `src/AdoptionSystem/…AdoptionSystem.Domain` | `Aggregates/<X>Aggregate/{Entities,ValueObjects,Repositories}` + `Core/Errors`; our aggregates are far simpler than `Cat` — don't inflate them |
| `TranslationSystem.ReadModels` + `…ReadModels.EntityFramework` | `…AdoptionSystem.ReadModels` + `…ReadModels.EntityFramework` | POCO read models per aggregate (`IReadOnlyEntity<TId>`) + their EF configurations; query handlers read them via `IApplicationReadDbContext` — never the write model (ADR-0002 amendment 2026-06-12) |
| `TranslationSystem.Persistence` | `…AdoptionSystem.Persistence` | Write + read DbContexts (`ApplicationWriteDbContext` = the UoW + owns migrations; `ApplicationReadDbContext` behind `IApplicationReadDbContext`, applies the ReadModels.EntityFramework configurations) + design-time factory; EF house rules below |
| `TranslationSystem.Contracts` | `…AdoptionSystem.Contracts` | Request/response DTOs per feature; referenced by Frontend |
| `TranslationSystem.API` | `…AdoptionSystem.API` | `IEndpoint` + assembly-scan `AddEndpoints`/`MapEndpoints`; slices in `Features/<Area>/<Action>.cs`; `ExceptionHandlers/`, `Auth/` (JwtBearer + policies + `CurrentUserAccessor` + ownership guards), health checks, Serilog + OTel bootstrap |
| `AuthSystem` (whole module) | `src/AuthSystem/*` | Self-hosted OpenIddict + Identity server — lift wholesale. **Do NOT lift the synchronous `RegisterUser`→`CreatePersonAsync` saga**: provision the translator profile lazily & idempotently on first authenticated TMS request (pattern: KittySaver ADR-0007 §4) |
| `Frontend` (infra) | `src/Frontend/TheKittySaver.Frontend` | Lift `Infrastructure/` (OIDC RP, `CookieTokenRefresher`, `DiscoveryCache`, `ApiResult`, typed HttpClients, error pages); pages are written fresh for translations; reference `TranslationSystem.Contracts` directly |
| Docker / compose | `compose.yaml`, `Dockerfile.migrator`, `Dockerfile.tests` | **Infra-only dev stack (ADR-0006 as amended by #190/M6-14): postgres + migrator + mailpit + aspire-dashboard.** All three apps (auth-api, tms-api, frontend) run on the HOST via `dotnet run` / the Rider compound `.run/TMS dev (all hosts)` — like TheKittySaver. `compose.prod.yaml` is the separate containerized/parity stack |

**Deliberate non-lifts (YAGNI — revisit only on a real, present need):** `Calculators`, domain
events (KittySaver dispatches them via Mediator notifications; the TMS core loop doesn't need
them — if a need appears, design an in-house dispatcher via ADR first). `ReadModels(+EF)` and
per-system `Primitives` were on this list and are now lifted from day 1 (ADR-0002 amendment
2026-06-12).

### De-mediatorization recipe (apply to every lifted slice)

A KittySaver slice is one file: `internal sealed class <Action> : IEndpoint` containing a nested
`Command`/`Query` record + nested `Handler`; the endpoint dispatches via `ISender`. Transform:

1. The record implements in-house `ICommand<Result<TResponse>>` / `IQuery<Result<TResponse>>`
   from `SharedKernel.Messaging`.
2. `Handler` implements `ICommandHandler<Command, Result<TResponse>>` — explicit constructor DI,
   `ValueTask Handle(...)`.
3. Register the **closed** interface explicitly in the system's DI:
   `services.AddScoped<ICommandHandler<<Action>.Command, Result<TResponse>>, <Action>.Handler>();`
4. The endpoint's route delegate takes the closed handler interface as a parameter (instead of
   `ISender`) and calls `handler.Handle(request, cancellationToken)`.
5. Pipeline behaviours don't exist here: validation — **command** handlers inject
   `IValidator<TCommand>` and map failures to `Result` (queries validate inline); logging —
   `ILogger<Handler>` inside the handler.

## Read-first routing (do this BEFORE touching the area)

| You're about to… | Read first |
|---|---|
| Build/change a **TMS slice** | the nearest existing sibling slice in `TranslationSystem.API/Features/…`; no fitting sibling → the KittySaver original (`AdoptionSystem.API/Features/…`) + the de-mediatorization recipe |
| Work a GitHub ticket end-to-end | run **`/ticket <number>`** (mind the this-file-wins rule in Project status) |
| Run the backlog autonomously (Loop mode) | **`/backlog`** → `scripts/claude/backlog-loop.sh` — one fresh headless session per ticket; manual: `docs/claude-loop.md` |
| Touch DAT binary parsing / writing / native interop | delegate to the **`dat-format-expert`** agent |
| Re-investigate update behavior, vnum, translation survival, launch flow | **don't** — empirically settled in `docs/knowledge-base/` (start at its README) |
| Make a non-trivial architectural/modeling decision | skim `docs/adr/`, then **write a new ADR** (`/adr`); anchors: 0001 (no mediator), 0002 (TMS pivot + freeze/unfreeze amendments), 0008 (cloud-agnostic deployment + env strategy — M6), 0009 (browser E2E via Testcontainers + Playwright) |
| Deploy/operate the stack, or set env vars per environment | `docs/deployment/runbook.md` — env-var matrix (service × environment), secret generation, the issuer/redirect/authority/CORS gotchas, bring-up sequence + DB migrations. Ingress/routing shape: ADR-0034 (Caddy) + **ADR-0041** (why there is no gateway behind it) |
| Add a proxy/gateway, expose a new service, or wire a client to an API path | **ADR-0041** — there is no API gateway: Caddy owns transport, the discovery document owns semantics, the frontend BFF owns aggregation. Clients resolve endpoints by **rel name** (ADR-0040's vocabulary), never by hardcoded path |
| Touch the update lifecycle (GameVersion, import diff, invalidation, distribution, CLI sync) | `docs/specs/0001-game-update-lifecycle-and-translation-invalidation.md` — the agreed domain spec |
| Touch update-day behavior on the client (sentinel, orchestrator, any new DAT write path) | `docs/specs/0012-update-resilience.md` (Tier 0/1 rules as amended 2026-08-17) + **ADR-0047** — every write goes through the per-row source guard; the no-masking invariant has no path exceptions |
| Touch the game-content catalog (CatalogEntry/TextSlot, Companion zip import, catalog browser, memberships) | `docs/specs/0008-game-content-catalog-layer.md` (agreed) + `docs/knowledge-base/lotro-companion-data-model.md` (the verified `key:<FileId>:<GossipId>` join — never join on text). Naming rule: **never "entity"** in this layer (DDD-Entity misconception) |
| Implement a feature whose business rules are fuzzy | **`/spec`** first (seed → questions → agreed spec in `docs/specs/`) |
| Write or hand over a **manual QA ticket** (external tester) | **`/qa-ticket`** — every scenario verified against HEAD first; `/qa-ticket #<n>` re-baselines an existing one |
| Review a finished change | the **`code-reviewer`** agent |
| Understand the backlog / milestones | `gh issue list` + Roadmap digest below |
| Compare with the Russian sister project | `docs/RUSSIAN_PROJECT_RESEARCH.md` + `docs/knowledge-base/russian-project.md` |

## Commands

```bash
# Build — zero-warnings gate: TreatWarningsAsErrors is on repo-wide; any warning IS a failing build
dotnet build LotroKoniecDev.slnx

# Tests
dotnet test                                            # everything runnable on this OS
dotnet test tests/LotroKoniecDev.Tests.Unit            # fast, pure unit (must always be green)
dotnet test tests/LotroKoniecDev.Tests.E2E             # full pipeline — auto-skips off-Windows
dotnet test --filter "FullyQualifiedName~Fragment"     # filter by name

# Run the CLI (Windows; needs LOTRO. `export` reads and needs NO admin — #629; only DAT writes do)
dotnet run --project src/Patcher/LotroKoniecDev.Cli -- export                 # DAT → data/exported.txt
dotnet run --project src/Patcher/LotroKoniecDev.Cli -- patch polish           # translations/polish.txt → DAT
dotnet run --project src/Patcher/LotroKoniecDev.Cli -- launch polish          # hash-check → patch if changed → launch
# or the .bat wrappers: export.bat (no elevation) / patch.bat, lotro.bat (self-elevate)

# GitHub tickets (BRD/spec-driven flow)
gh issue list --state open                             # backlog; titles follow "M{milestone}-{nn}: Title"
gh issue view <n>                                      # body holds Context / Depends on / Tasks / Acceptance criteria
gh issue develop <n> --checkout                        # create + checkout the linked "{n}-{kebab-title}" branch
gh pr create --fill --body "Closes #<n>"               # PR title mirrors the ticket; body closes it

# Autonomous backlog loop (Loop mode) — bash conductor + one FRESH headless session per ticket
scripts/claude/backlog-loop.sh                         # drain every ready ticket, serially
scripts/claude/backlog-loop.sh -n 3                    # at most 3 tickets
scripts/claude/backlog-loop.sh 123 130                 # exactly these tickets, in order
caffeinate -is scripts/claude/backlog-loop.sh          # overnight run on macOS (blocks sleep)
scripts/claude/next-ticket.sh                          # print the next READY ticket (priority + deps)
scripts/claude/work-ticket.sh 123                      # one ticket, one fresh headless session
# defaults: opus (Opus 5) · effort high (reviews at high too) · permission-mode auto — override via LOOP_MODEL /
# LOOP_EFFORT / LOOP_PERMISSION_MODE / LOOP_UNSAFE=1 · full manual: docs/claude-loop.md

# TMS — EF Core migrations (write context owns them; --connection makes it work without appsettings/live DB)
# dotnet-ef is a pinned local tool (dotnet tool restore). No --startup-project: it would equal
# --project, and dotnet-ef 10.0.9 mis-parses the pair when both carry the identical value.
dotnet ef migrations add <Name> \
  --project src/TranslationSystem/LotroKoniecDev.TranslationSystem.Persistence \
  --context ApplicationWriteDbContext \
  -- --connection "Host=localhost;Database=lotro_translation;Username=postgres;Password=changeme"

# TMS dev — INFRA-ONLY compose (postgres + migrator + mailpit + aspire) + THREE host Kestrels (ADR-0006, amended #190/M6-14)
docker compose up -d                                   # boots infra + runs the one-shot migrator; NO app images (the apps run on host)
docker compose up --build migrator                     # rebuild the migrator image only after adding an EF migration
docker compose logs -f migrator                        # watch the one-shot schema migration (TMS + Auth contexts)
docker compose down                                    # stop; add -v to also drop the postgres volume (fresh DB)
# scripts/up.sh | up.ps1 = recommended boot — bootstraps .env from .env.example, then `docker compose up` (no cert, no API build).
# One-time host prereq so the host Kestrels serve HTTPS:  dotnet dev-certs https --trust
# The three apps run on the HOST (hot reload, breakpoints, no image rebuild) — all three at once via the Rider
# compound ".run/TMS dev (all hosts)", or each via its own `dotnet run` (each uses its `https` launchSettings profile):
dotnet run --project src/AuthSystem/LotroKoniecDev.AuthSystem.API                 # auth-api → https://localhost:5003
dotnet run --project src/TranslationSystem/LotroKoniecDev.TranslationSystem.API   # tms-api  → https://localhost:5002
dotnet run --project src/Frontend/LotroKoniecDev.Frontend                         # frontend → https://localhost:7017
# Endpoints (HTTPS): tms-api :5002 · auth-api :5003 · frontend :7017 · aspire :18888 · mailpit :8025
#   (e.g. curl -k https://localhost:5002/health). The browser, the host RP and the host resource server all resolve
#   localhost:5003/:5002 identically, so one OIDC Authority/Issuer serves every leg + the token `iss`. tms falls back
#   from Auth:Authority to Auth:Issuer (https://localhost:5003) to reach the host auth Kestrel; no config differs by run mode.

# TMS — Production-PARITY stack (compose.prod.yaml; ADR-0008 §4 / M6-07) — SEPARATE from dev compose.
# ALL FOUR images + a Caddy reverse proxy run under ASPNETCORE_ENVIRONMENT=Production: real OpenIddict
# keys, DP keyring volumes (auth + frontend), self-hosted Postgres over SSL, the containerized Frontend.
# Catches prod-only breakage on a laptop before staging. Coexists with the dev stack (separate project).
scripts/up-prod.sh | up-prod.ps1                       # recommended boot — bootstraps .env.prod (with generated
                                                       #   OpenIddict secrets) + local CA/proxy/Postgres certs, maps the
                                                       #   *.lotro.test vhosts to loopback (idempotent; admin only the
                                                       #   first time), then up. Args pass through (e.g. --build, -d).
docker compose -f compose.prod.yaml --env-file .env.prod up --build      # raw command (after the bootstraps + a manual hosts entry)
docker compose -f compose.prod.yaml --env-file .env.prod --profile local-smtp --profile local-otel up  # + mailpit + aspire (all-local; needed for a green deep auth /health)
docker compose -f compose.prod.yaml --env-file .env.prod down            # add -v to drop prod volumes (fresh DB/keys)
# up-prod.{sh,ps1} runs both one-time bootstraps for you: gen-openiddict-keys (3 secrets → .env.prod) +
#   init-prod-https (local CA → .docker/prod-https/). It also auto-maps the hosts file (cross-platform:
#   sudo on macOS/Linux, UAC on Windows); manual equivalent: 127.0.0.1 app.lotro.test auth.lotro.test tms.lotro.test
# Browser OIDC login: https://app.lotro.test. Health (trust the local CA):
#   curl --cacert .docker/prod-https/rootCA.crt https://auth.lotro.test/health/ready
```

The dev stack is **infra-only** (ADR-0006, amended by #190 / M6-14): `compose.yaml` runs postgres +
migrator + mailpit + aspire-dashboard, and the **three apps run on the host** as the canonical dev loop —
auth-api (`https://localhost:5003`), tms-api (`https://localhost:5002`), frontend (`https://localhost:7017`),
each via its `https` `launchSettings` profile (`dotnet run`, or the Rider compound `.run/TMS dev (all hosts)`).
Host Kestrels serve HTTPS with the **native** ASP.NET Core dev cert (one-time `dotnet dev-certs https --trust`)
— no PFX, no mount. Because the browser, the host RP and the host resource server all resolve
`localhost:5003`/`:5002` identically, a single OIDC `Authority`/`Issuer` serves every leg and the token `iss`
matches; tms-api's back-channel uses the `Auth:Authority`→`Auth:Issuer` fallback (`AuthSettings.EffectiveAuthority`)
to reach the host auth Kestrel. The containerized `auth-api`/`tms-api` services were retired from dev because they
were neither the fast inner loop (host Kestrels give hot reload + breakpoints + no image rebuild) nor prod-parity —
`compose.prod.yaml` is the sole containerized/parity stack and exercises the very same Dockerfiles. The migrator is
a one-shot container (TMS migrates through its Persistence project, Auth through its API — only those carry EF Core
Design); it runs to completion against both DBs so the host Kestrels hit a migrated schema. Dev uses **ephemeral**
OpenIddict keys; production-like runs supply real keys via env (see `.env.example`).

`compose.prod.yaml` is the **separate production-parity stack** (ADR-0008 §4 / M6-07; the dev
`compose.yaml` is left untouched). It runs all four images **plus a Caddy reverse proxy** under
`ASPNETCORE_ENVIRONMENT=Production`, reproducing the cloud topology locally so prod-only breakage
surfaces before staging. Caddy terminates TLS on one origin per app — `app|auth|tms.lotro.test` —
reachable **identically** from the browser (hosts file — `.test` is not auto-resolved) and from the in-stack Frontend
container (Caddy network aliases). That single shared origin is what lets one OIDC `Authority` serve
both the browser front-channel and the Frontend's back-channel, so the containerized-RP two-legs
problem of ADR-0006 dissolves behind the proxy (this folds in the M6-08 ingress). The proxy forwards
`X-Forwarded-Proto/Host/For`, exercising `UseForwardedHeaders` (M6-02). Production specifics: real
OpenIddict keys (`scripts/gen-openiddict-keys.{sh,ps1}`), DP keyring **volumes** for auth + frontend
(M6-04 / ADR-0005), self-hosted Postgres with `ssl=on` (`Ssl Mode=Require;Trust Server Certificate=true`;
swap to a managed DB = change just the two `ConnectionStrings__*` in `.env.prod`), real SMTP + OTLP from
env (mailpit/aspire only behind `--profile local-smtp|local-otel`). In Production OpenIddict rejects
plain-HTTP requests, so **both** tms-api (OIDC metadata + JWKS for token validation) and the Frontend
(OIDC discovery/token/userinfo) reach auth **through the proxy** over `https://auth.lotro.test` —
`Auth:Authority` / `AuthSystem:Authority` is that proxy origin (matching the token `iss`), not the
in-network `http://auth-api:8080`. .NET validates the proxy's leaf cert against the **OS trust store**
(it ignores `SSL_CERT_FILE`), so a shared mount-only entrypoint (`.docker/trust-ca-entrypoint.sh`)
installs the local CA via `update-ca-certificates` and drops back to the non-root app user — no app or
Dockerfile change (real prod uses a publicly-trusted ingress cert, so the shim is parity-stack-only).
Secrets live in the git-ignored `.env.prod`; TLS material in `.docker/prod-https/` (also git-ignored),
both bootstrapped by `scripts/up-prod.{sh,ps1}`.

Exit codes (CLI): `0` success, `1` invalid arguments (incl. `ErrorType.Validation`), `2` file not
found, `3` operation failed, `4` cancelled.

## DAT binary format (digest — full notes in `docs/knowledge-base/`)

```
SubFile (text, FileId high byte = 0x25):
  FileId (4B) | Unknown1 (4B) | Unknown2 (1B) | FragCount (VarLen)
  Fragment[]:
    FragmentId (8B ulong = GossipId) | PieceCount (int)
    Piece[]: VarLen length + UTF-16LE bytes
    ArgRefCount (int) | ArgRef[]: 4B each
    ArgStringGroupCount (byte) | Group[]: Count(int) + VarLen UTF-16LE strings

VarLen: 0-127 = 1 byte; 128-32767 = 2 bytes (high bit flag)
```

## Translation file format — THE inter-context contract

```
# Comments start with #
file_id||gossip_id||translated_text||args_order||args_id||approved||source_digest
620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1||3f9a1c0e7b2d4a55
620756992||1002||Tekst z <--DO_NOT_TOUCH!--> argumentem||1||1||1||9c02e4d1a7f0b366
```

- `<--DO_NOT_TOUCH!-->` = argument placeholder
- `args_order` / `args_id`: `NULL` or `1-2-3` (1-indexed in file, 0-indexed internally). Anything
  else **rejects the row and is reported** (ADR-0042) — the CLI prints it as a patch warning, the
  import fails the whole upload. Whether the positions FIT the fragment is checked downstream in
  `Fragment.TryReorderArgRefs`, the only place that knows how many argument references there are.
- **Content escape (ADR-0039): `\`→`\\`, CR→`\r`, LF→`\n`.** It escapes its own escape character, so
  it is injective and `Unescape(Escape(x)) == x`. **Every writer escapes and every reader unescapes**
  — all four ends (patcher exporter + parser, TMS serializer + import parser), each via its context's
  own `TranslationLineEscaper`. Text held anywhere else — in the DAT, in `TranslationSource.Text`, in
  `TranslatedText` — is always the RAW form; the escape exists only between `Serialize` and `Parse`.
  A sequence no writer can produce (`\t`, a trailing lone `\`) reads back verbatim.
- **Content is bounded by the DAT, not by the file (ADR-0043).** A text piece is written behind a
  2-byte VarLen prefix, so it cannot exceed **32767 UTF-16 code units**. The TMS refuses a longer
  `TranslatedText` at the API (`UpsertTranslation.Validator` + a `CHECK` constraint — never a
  `varchar(n)` narrowing, which would rewrite the ~780k-row table under `ACCESS EXCLUSIVE`); the
  patcher warn-skips such a row **before** it loads or mutates a subfile
  (`Fragment.IsWritablePiece`). `Fragment.Write` still throws — deliberately, as the last resort —
  and `PatchingService` never catches mid-write.
- **Stale Polish never lands over changed English — an INVARIANT, enforced per row at write time
  (ADR-0047, #659).** Owner's rule (2026-08-17): if SSG changed a row's English in version N+1 and
  the newest approved translation is still for N, the player sees English — *whatever path writes
  the DAT* (routine `launch` hash-patch, `patch`, the spec-0012 sentinel, the update-day
  orchestrator). TMS-side exclusion of invalidated rows (spec 0001) holds only after the new export
  is imported; the client closes the pre-import window: the artifact carries per row a
  `source_digest` (7th column — 16 hex of the framed SHA-256 the TMS computes as `SourceHash`), and
  the patcher writes a row only when the fragment holds that English or what the patcher itself
  last wrote there (`<file>.ledger` sidecar); anything else is skipped and reported as
  `source moved`. No verdict, registration or watcher is load-bearing for this, and there is no
  operator override. A six-column translation file is not patchable. Digest parity between the two
  contexts is a golden fixture pinned on both sides.
- **The `||` separator is deliberately NOT escaped — the line is CARVED, never `Split` (ADR-0042).**
  Each context's `TranslationLineCarver` scans **forward** for the two id separators and **backward**
  for the trailing ones — three or four, sniffed from the last field: `0`/`1` is `approved`, 16 hex
  is `source_digest` (ADR-0047) — slicing before each backward search, so content may contain `||` and
  may end in any run of `|`. `string.Split` resolves every boundary greedily left to right, which
  silently ate a trailing pipe into the args column (#597); never reintroduce it here. Nothing but
  content can hold a `|`, so both boundaries are recoverable by construction — no escape needed.
- Results sorted by FileId then GossipId for sequential DAT I/O
- **Changing this format requires an ADR + updated golden fixtures in BOTH contexts** (patcher
  parser tests and TMS import/export tests).

## Game update behavior (empirically proven — do not re-test, see knowledge base)

- **Forum version** (regex `Update\s+(\d+(?:\.\d+)*)\s+Release\s+Notes` on lotro.com) is the
  reliable game-version identifier. **DAT vnum is useless as a content version** (112/3 unchanged
  across 45.x→49.1 — six cycles incl. two majors — even while the DAT was actively patched).
- Launcher patches the DAT **chunk-based**; **translations survive updates per-SubFile** — proven
  across 9 live tests incl. the 48.0 and 49 majors. Fragments in untouched SubFiles survive
  byte-for-byte; an update that modifies a SubFile replaces the whole chunk and **reverts our
  fragments inside it** (first observed 48.8→49.1: 1/8; repair = normal re-patch, TMS-side =
  the spec-0001 invalidation loop). `attrib +R` protection is unnecessary either way.
- Simplified launch flow (translation-hash check → patch only if changed → fire-and-forget launch)
  is fully validated.

## Project house rules

- **Zero warnings.** `TreatWarningsAsErrors` is repo-wide. Fix it; don't suppress it (a scoped
  `.editorconfig` exception requires a stated reason, like the `Result._value` guarded getter).
- **Errors are values, not exceptions.** Business failures → `Result.Failure(Error)` via
  `DomainErrors.*` factories / `Error.Validation(...)`. Guards (`Ensure`,
  `ArgumentNullException.ThrowIfNull`) are for **programmer** errors only. The API's
  `ExceptionHandlers/` are safety nets, not a control-flow mechanism.
- **No mediator — slim SRP handlers (ADR-0001), repo-wide.** One use case = one record + one
  handler implementing the in-house `ICommandHandler<,>`/`IQueryHandler<,>`. Consumers inject the
  closed handler interface directly. Lifted KittySaver code is de-mediatorized on entry.
- **DI validation is always on (#572).** Every composition root enables `ValidateScopes` +
  `ValidateOnBuild` in ALL environments (`builder.Host.UseDefaultServiceProvider` on the three web
  hosts; `ServiceProviderOptions` in the CLI `TypeRegistrar`) — a new host copies the pattern. It
  validates registered services only, so endpoint integration tests stay the guard for a forgotten
  closed handler registration. **In the CLI, Spectre command types are registered `AddScoped` and
  resolved through a single process-lifetime scope in `TypeResolver`** — they inject scoped
  handlers, so the old `AddSingleton` + root-provider resolve is a captive dependency that refuses
  to build. A new CLI command follows suit. Nothing in CI covers that graph (the CLI is
  `net10.0-windows/win-x86`), so changes to it need a Windows `export`/`patch`/`launch` smoke.
- **CQRS read/write split, day 1 (ADR-0002 amendment).** Query handlers read POCO `ReadModels`
  through `IApplicationReadDbContext`; command handlers load and mutate aggregates via
  repositories + `IUnitOfWork`. The write model never serves list/search queries; every new
  aggregate ships with its read model + EF configuration in the same change.
- **Patcher is stable, not frozen** (ADR-0002 amendment 2026-06-25) — refactor it when it earns
  its keep, but every change must keep its existing tests green (assertions untouched) and must not
  regress behavior proven in `docs/knowledge-base/`.
- **TMS ships with auth from day 1.** Endpoints are authorized by default (public ones are
  explicit) and edited rows carry user attribution: `Translation.SubmittedById` is stamped on
  upsert (added in M2-11; persisted via the `IdentityId` converter in `TranslationSystem.Persistence`),
  and `ApprovedById` lands with the approve slice (#101 / M2-12). No auth-less interim state to
  retrofit later.
- **Hypermedia links are authorization-aware, not role-branched (ADR-0040).** `ILinkFactory` replays
  the *target* endpoint's own policy (`IAllowAnonymous` → `AuthorizationPolicy.CombineAsync` incl.
  the fallback → `IAuthorizationService`) before emitting a link, so a rel is never advertised to a
  caller who would get 401/403 following it. Never restate a role rule inside a link factory — a
  link factory encodes *state* rules only (removed, `Draft`/`NeedsReview`, `Unprocessed`). The TMS
  service document (`GET /`) is the one deliberate `AllowAnonymous` hole in authorized-by-default:
  it advertises only what the caller may already reach, and it is what lets the CLI (and M4) boot
  without hardcoded paths. Only parameterless entry points belong in it; id-keyed affordances
  (`approve`, `delete`, `import`) live on the representation carrying the id.
- **No API gateway — the discovery document IS the client contract surface (ADR-0041).** Caddy owns
  transport, each API's discovery root owns semantics, the SSR frontend owns aggregation; nothing
  goes in the request path between them. So **rel names are a frozen public contract** — additions
  are cheap, renames break every client — and a client takes one root URL per service as config and
  resolves everything else by rel (#610 frontend, #611 CLI). A service split does **not** justify a
  gateway: the departing service hosts its own root and the one it left links to it with a single
  configured absolute URI. Reopening triggers are listed in the ADR.
- **Validation:** FluentValidation **for commands only** — the command handler injects
  `IValidator<TCommand>` and maps failures to `Result` (never throws). Queries validate inline
  in their handler. Every validator must be registered in DI.
- **Handlers are orchestrators.** Business logic lives in domain/application services; handlers
  validate, delegate, return.
- **No primitive obsession in the domain layer.** Every domain concept that carries a constraint
  or identity is a `ValueObject` — never a raw `string`, `int`, `Guid`, etc. passed or stored
  directly. Golden templates:
  `TranslationSystem.Domain/Aggregates/GameVersionAggregate/ValueObjects/LotroNotationVersion.cs`
  (constrained-string VO) and
  `TranslationSystem.Domain/Aggregates/GameVersionAggregate/Entities/GameVersion.cs`
  (an aggregate that models its constrained version concept as a VO — its timestamp and enum
  stay primitive, because they carry no extra invariant).
- **EF Core (`TranslationSystem.Persistence`):** Fluent API only (never attributes), `nameof()`
  for column names, `MaxLength`/`Precision`+`Scale` over `HasColumnType`, no needless
  `IsRequired()` (value types & non-null strings are already required), FK property names
  parametrized with `nameof()`.
- **VO persistence mapping — `ComplexProperty` by default, `OwnsOne` when index is needed.**
  `ComplexProperty` is the semantically correct mapping for VOs (pure value type, no identity
  tracking). Switch to `OwnsOne` only when the property requires a DB index — `ComplexProperty`
  cannot be indexed in EF Core 10 (limitation removed in EF Core 11). With `OwnsOne`, define
  `HasColumnName` explicitly and put `HasIndex` inside the owned builder.
- **Migrations are forward-only and N-1 backward-compatible (ADR-0023).** The deploy gate commits
  the schema **before** traffic moves, and rollback reverts code, never schema — so the
  currently-running app revision must survive every migration. Never rely on `Down()` outside
  local dev; recovery is roll-forward or a Neon restore — PITR or the MIGR-04 pre-migration
  auto-snapshot branch (runbook). Destructive operations
  (drop/rename a column or table, change a type, add `NOT NULL`, tighten a constraint / unique
  index over existing data) ship as **expand → backfill → contract across ≥ 2 deploys**; a
  deliberate destructive step carries an in-file `MIGRATION-SAFETY: acknowledged — <reason>`
  comment (CI gate: #338 / MIGR-03). Migration-touching PRs additionally run the executable
  N-1 proof: the previous release's integration suites against the HEAD schema
  (`n1-compat.yml` / `scripts/n1-compat.sh` — ADR-0024; the factories' seam is
  `N1_COMPAT_SCHEMA_SCRIPTS_DIR`, inert in normal test runs).
- **Right-size the design — YAGNI by default.** Before proposing an abstraction, cache, config
  knob, queue, or new infra, check it solves a **real, present** need from the current
  spec/ticket — not a hypothetical future. Pick the simple path and note the trade-off in one line.
- **Agent fan-out is budgeted; agents inherit the session's model and effort** (the repo pins no
  model tier of its own — see the 2026-08-14 prune; the churn it ended: Fable switch 2026-07-09→11,
  same-day Opus revert #497, Fable re-enable #503, Opus for reviews 2026-07-13, Fable again
  2026-07-17, Opus everywhere 2026-08-05). Hard cap:
  **max 4 subagents in parallel**, no chained waves by default; a small diff gets reviewed
  **inline, zero agents**. The committed `code-reviewer` agent carries **`model: inherit`** — it
  runs on whatever model and effort the session runs on, so a fresh clone reviews with the
  contributor's own tier instead of a model this repo has no business pinning for them;
  `/code-review` and `/security-review` follow the session model/effort the same way; loop-mode
  worker sessions run at **effort high** (`LOOP_EFFORT` default) — unless the prompt for that run
  explicitly says otherwise. Applies to interactive sessions and loop-mode workers alike. The
  concrete values live in agent frontmatter and in the loop scripts' fallback defaults; the
  maintainer pins their own tier machine-locally (a central model policy outside the repo), which
  is deliberately **not** something a clone inherits. If this prose and the frontmatter ever
  disagree, the frontmatter wins.
- **Frontend is Static SSR — enforced, not just documented.** No WebAssembly, no SignalR circuit,
  no per-user server state; forms post via `<form method="post" @formname @onsubmit>` (the SSR
  `@onsubmit` special-case) or `<EditForm OnValidSubmit>` — never interactive `@on*` handlers,
  `@rendermode`, `StateHasChanged`, or `AddInteractive*`. `scripts/check-ssr-purity.sh` (with a
  `.ps1` twin for local Windows devs) gates this in **both** `pr-verify` and `ci`, before
  `setup-dotnet`. Genuinely need interactivity? That's an ADR-first architecture change.
- **No client hardcodes an API path — enforced, not just documented (#610 frontend, #611 CLI).**
  There is no gateway (ADR-0041), so every entry point is resolved by **rel name** — the Frontend
  through `IDiscoveryCache.ResolveTranslationSystemHrefAsync(Rels.<Name>)`, the CLI through
  `ITranslationFileEndpointResolver` — and every id-keyed action follows the href the loaded
  resource advertises. A missing rel is a failure (`ProblemDetails` in the Frontend, a `Result`
  error in the CLI) — never a locally composed path, because an absent rel means the server does
  not offer that affordance to this caller. `scripts/check-client-hypermedia.sh` (with a `.ps1`
  twin) flags an API path in any string literal under `src/Frontend/` **and** `src/Patcher/` and
  gates it in **both** `pr-verify` and `ci`, alongside the SSR guard; prose mentions in comments
  stay allowed. The one bounded exception is the editor's detail URI
  (`{discovered translations href}/{id}`) — the `/editor/{id}` route hands over an id, not a link,
  and it is documented as such in `TranslationEditorLoader`.
- **The CLI resolves its download URL from discovery, and degrades without guessing (#611).**
  `SyncTranslationFileCommand` still takes one input, `TmsBaseUrl`; everything else comes from
  `GET {baseUrl}/` with the HATEOAS vendor `Accept` (links are opt-in — a plain-JSON request gets a
  link-less document). Discovery is the primary path and the `.endpoint` sidecar next to
  `polish.txt`/`.etag` is the **outage** fallback only: a server that answers but does not advertise
  `translation-file`, or advertises an href off the configured origin, is a refusal, not an outage —
  no fallback, no composed path. A resolved href is re-validated (absolute, https except loopback,
  same origin as the base URL) whether it came from the wire or from disk. Because the launch must
  never block on the network (spec 0001 Q5), an unresolvable endpoint reports
  `EndpointUnresolvedUsedCache` and lets the launch continue on the local file; with no local file
  the launch path reports it and exits 2, exactly as it does today. Same reasoning downgrades a
  failed `.endpoint` write to a logged warning — that sidecar is a hint, and the next run
  re-resolves it — while a failed `Save` of the downloaded file stays fatal. The TMS adapters use
  their own keyed `HttpClient` with **`AllowAutoRedirect = false`**: the origin check on the
  resolved href is worthless if a 302 can carry the request off it (the redirect target would serve
  both the body and the ETag that hashes it, so the integrity check would confirm the wrong file).
  The forum fetcher keeps redirects — it targets a third-party site.
  **The patcher's one allowed shared reference is `Utilities/LotroKoniecDev.Hateoas.Abstractions`**
  (`MediaTypes.HateoasJson`, and `LinkDto` in tests only). It is not the TMS side, and the vendor
  media type is centralised there precisely so the two ends cannot drift; the CLI still re-types the
  link envelope rather than linking `TranslationSystem.Contracts`, with a parity test standing in
  for the compiler. Note `BoundedContextIsolationTests` covers patcher Primitives/Domain/Application
  only — Infrastructure and Cli are `net10.0-windows`, so a `net10.0` test project cannot reference
  them and nothing mechanically blocks the next reference added there.
- **Docker restore layers are loud and gated (ADR-0028, amended).** Every Dockerfile that lists
  `.csproj` files must COPY the **full transitive closure** of the projects it restores.
  `dotnet restore` treats a missing project file as `Skipping project … because it was not found`
  and still **exits 0**, so a stale list silently caches an incomplete restore layer. Two defenses:
  image builds run `dotnet build`/`publish` with **`--no-restore`**, turning the gap into a hard
  `NETSDK1004`; and `scripts/check-dockerfile-restore-graph.sh` (with a `.ps1` twin) gates it in
  **both** `pr-verify` and `ci` — `ci` builds no images, and pr-verify's image job (CI-01 / #403)
  fires only on Dockerfile/.dockerignore/workflow edits, never on the project-graph changes that
  actually stale a COPY list. A new project must join every Dockerfile whose restore graph reaches it.
  **The Blazor exception (#414):** the frontend image's `dotnet build` runs **without**
  `--no-restore`. `blazor.web.js` ships in `Microsoft.AspNetCore.App.Internal.Assets`, which the SDK
  references only once it sees `.razor` files — never during the `.csproj`-only restore — so
  `--no-restore` there silently emits a static-web-assets manifest with no `_framework/*` and every
  asset 404s at runtime. Never add the flag back to that `dotnet build`; `publish` keeps it.
- **CI runs what the diff can actually break — nothing more (`scripts/ci/classify-changes.sh`).**
  Every PR gets three **independent** verdicts: `code` (restore + Release build + unit + integration
  tests), `guards` (the cheap bash gates CI *executes*: SSR purity, Dockerfile restore graph,
  migration safety, loop provenance) and `images`. Keep them independent — collapsing "CI executes
  this script" into "run the whole .NET gate" is what made a `.claude/` + docs + loop-scripts PR pay
  for a full Release build and both suites. The failure mode that matters is the other direction:
  putting a **build input** (`.editorconfig`, `Directory.*.props`, `global.json`, a fixture) into the
  inert list buys a silent false green, so `scripts/tests/classify-changes.tests.sh` pins both
  directions and runs **unconditionally** in the `changes` job, before any verdict is trusted.
  `ci.yml` (main) deliberately does **not** self-skip its .NET steps — CD is triggered by CI
  concluding success, so "CI was green" must keep meaning the build and both suites really ran; it
  filters cheap content with `paths-ignore` instead (no CI ⇒ no CD).
- **`GET / -> 200` never proves the Blazor frontend works.** A `[StreamRendering]` page returns 200
  with its spinner frame before it fetches anything. The signature of a healthy image is the
  **fingerprint**: `@Assets[]` renders `_framework/blazor.web.<hash>.js` only when `MapStaticAssets`
  resolved its manifest. `scripts/smoke.{sh,ps1}` leg 2 asserts exactly that, and CD smokes the
  0%-traffic candidate before any traffic shift.
- **Git is rebase-based, and branches are never deleted.** Integrate a feature branch off `main`
  with `git rebase main` — never `git merge main`; no merge commits in feature branches (remote
  `main` is squash-only, so history stays linear). After a PR's squash commit lands on `main`,
  **keep both the local and the remote branch** — merge with plain `gh pr merge --squash` (never
  `--delete-branch`), and never run `git branch -d/-D` or `git push origin --delete`.

## Code style (C#) — repo-authoritative

- **Sealed** all types unless there is explicit inheritance.
- **Explicit constructors** in classes — no primary constructors for a `class` (records are fine).
- `var` **only for anonymous types**; explicit types everywhere else.
- **LINQ methods**, never query syntax. **Pattern matching** — except inside a query expression.
- **File-scoped namespaces**, **Allman braces**, no `#region`.
- **Documentation uses `/// <summary>` XML doc comments** — never plain `//` comments to
  document a type or member. Omit entirely when the name already explains the intent; reserve
  plain `//` for the non-obvious *why* inline in logic.
- Code & identifiers in **English**.
- **Domain class member order — mirror TheKittySaver exactly** (golden refs:
  `…AdoptionSystem.Domain/Aggregates/CatAggregate/Entities/Cat.cs`, `…/Vaccination.cs`). Top to
  bottom: (1) `public const`s, (2) `private readonly` backing fields (e.g. child collections),
  (3) public properties, (4) public/internal behavior methods, (5) public/internal `static`
  factory method(s) (`Create`), (6) private constructors (domain ctor, then the parameterless EF
  ctor), (7) private helper methods. The `Create` factory sits **after** the behavior methods and
  **immediately before** the constructors — not at the top.

## Anatomy of a feature slice

### Patcher slice (stable — the reference shape; bugfixes & deliberate refactors)

`Application/Features/<Area>/`: `<Action>Command.cs` (sealed record `: ICommand<Result<T>>`) +
`<Action>CommandHandler.cs` (internal sealed, explicit ctor DI) + validator (commands only) +
response record. Wired in `ApplicationDependencyInjection`; CLI injects the closed interface and
maps failures via `ErrorMapper.MapErrorToExitCode`. Canonical examples: `Features/Patching/`,
`Features/PreflightChecking/`.

### TMS slice (the shape going forward — VSA in the API project)

```
TranslationSystem.API/Features/<Area>/<Action>.cs

internal sealed class <Action> : IEndpoint
{
    internal sealed record Command(…) : ICommand<Result<TResponse>>;          // or Query : IQuery<…>

    internal sealed class Handler : ICommandHandler<Command, Result<TResponse>>
    {
        // explicit ctor DI — command: repositories + IUnitOfWork + IValidator<Command>;
        // query: IApplicationReadDbContext (read models only)
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder) { … } // injects the closed
}                                                                             // handler interface
```

Wire the rest — **all three steps, every time**: (1) explicit DI registration of the closed
handler interface, (2) request/response DTOs in `TranslationSystem.Contracts`, (3) tests —
domain/handler unit tests + endpoint integration test against real PostgreSQL.
**Mirror the nearest existing sibling slice** (here or in TheKittySaver) rather than inventing
structure.

## Testing philosophy — repo-authoritative

- **Black box over the public seam — never the implementation.** Assert observable behavior:
  inputs in → `Result`/persisted state out. NSubstitute stubs **genuine boundaries**
  (`IDatFileHandler`, `IForumPageFetcher`), never internals you own.
- **`.Received()` policy:** only for side effects invisible in the return value (resource cleanup,
  "destructive op was NOT called on validation failure"). If the return value already proves it,
  `.Received()` is forbidden — a behavior-preserving refactor must never break a test.
- **Unit tests are pure:** no filesystem, no network, no DB, no order dependence. Real-resource
  verification belongs to integration projects.
- **Edge cases are first-class.** Happy path is the floor. `[Theory]` + `[InlineData]` for the
  unhappy-path/boundary matrix (empty, max, malformed, already-in-state).
- **AAA always; assertions inline in the test method.** DRY the Arrange (builders), never the
  Assert. One reason to fail per test.
- **Tooling: xUnit + Shouldly + NSubstitute only.** Naming: `MethodName_Scenario_ExpectedResult`.
  (`Architecture.Tests.Unit` additionally uses **NetArchTest.Rules** — architecture rules only;
  the two snapshot suites use **Verify.Xunit** — see the next bullet.)
- **Snapshots pin shape; they never replace an assert (#571).** Three tools, three jobs: **golden
  fixtures** own the `||` file contract on both sides (a snapshot adds nothing there and must not
  replace them), **plain asserts** own behavior across many inputs, and a **Verify snapshot** owns
  "did anything about this large payload change" — TMS API response bodies (JSON incl. HATEOAS links
  and ProblemDetails) and Blazor SSR rendered markup, where hand-written asserts only ever cover a
  corner. The behavioral suites stay; deleting an assert because "the snapshot covers it" is the
  wrong move. `*.verified.*` files are committed and ARE the pinned contract, `*.received.*` is
  git-ignored scratch, and **re-accepting a verified file is a deliberate, reviewed act** — read the
  diff, then land it in the same PR as the change that caused it. One shared scrubber set
  (`tests/Shared/VerifyModuleInitializer.cs`, linked into every snapshot suite) keeps runs
  deterministic; a snapshot that churns is worse than no snapshot. Details: `tests/CLAUDE.md`.
- **The structural house rules are a TEST, not review memory.** `tests/LotroKoniecDev.Architecture.Tests.Unit`
  mechanically enforces the patcher dependency rule, no-mediator (ADR-0001), patcher/TMS bounded-context
  isolation, the Frontend's contracts-only reach, the persistence direction, the CQRS read/write split and
  the sealed/`internal`-handler/commands-only-validator conventions — over assembly IL, in the normal unit
  gate, on every OS. **A new production project must join `ProductionAssemblies.All`** or it escapes every
  rule (a self-test fails until it does). Changing a rule is an architecture decision: fix the code, or
  write the ADR first — never weaken the test to green. Details: `tests/CLAUDE.md`.
- Platform honesty: tests must pass on macOS AND Windows — `Path.Combine`, never hardcoded `C:\`.
- **TMS test projects mirror KittySaver naming.** Unit (pure):
  `TranslationSystem.Domain.Tests.Unit`, `TranslationSystem.API.Tests.Unit`,
  `AuthSystem.API.Tests.Unit`, `SharedKernel.Tests.Unit`, `Logging.Tests.Unit`,
  `Frontend.Tests.Unit`. Integration (real
  PostgreSQL — never in a Unit project): `TranslationSystem.API.Tests.Integration`,
  `AuthSystem.API.Tests.Integration`. Browser/E2E (Testcontainers + Playwright — ADR-0009;
  Docker-required, off the PR gate by name): `TranslationSystem.E2E.Tests`,
  `Frontend.E2E.Tests`. All under `tests/LotroKoniecDev.<name>`. Patcher test projects
  (`Tests.Unit`, `Tests.Infrastructure`, `Tests.E2E`) stay exactly as they are.

## Workflow (the loop that compounds)

1. **Ticket before code.** Work flows from GitHub issues (`M{milestone}-{nn}: Title`, labels
   `critical/high/medium/low` + `bug/refactor/infra/feature/test`). Run **`/ticket <n>`**.
2. **Spec before code.** Anything non-trivial gets `docs/specs/NNNN-*.md` (via `/ticket` or
   `/spec`). Open questions are **extracted for the user, never invented**. Implementation starts
   only at **Status: Agreed**.
3. **Decision before code.** Non-trivial modeling/architecture choice → **`/adr`** first.
4. **Slice, mirror, test, review.** Branch via `gh issue develop <n> --checkout`. Implement by
   mirroring the nearest sibling slice, add tests, then run the **`code-reviewer`** agent on the
   diff (**`/security-review`** for anything touching native interop, file protection, or auth).
   **Green build + zero warnings + clean review = "done" — not before.**
5. **PR closes the ticket.** Title mirrors the ticket; body contains `Closes #<n>`.
   Ask before pushing. **No merge with open CodeQL alerts:** green checks are not enough — the
   CodeQL check succeeds even when it uploads findings. Before any merge (interactive or loop),
   list `gh api "repos/{owner}/{repo}/code-scanning/alerts?ref=refs/pull/<n>/merge&state=open"`
   and fix every alert (dismiss only with a stated reason); the loop's merge gate enforces this
   mechanically.
6. **Feed the flywheel.** Reusable correction → persist it: global rule → **this file**; real
   decision → new ADR; empirical DAT/update finding → `docs/knowledge-base/` (dated). The same
   mistake made twice means a rule is missing.
   A lesson too narrow for any of those goes to `.claude/agent-memory/<agent>/`, which is
   **gitignored** — this repo is public, and one maintainer's notes about their own runs are not
   something a stranger should pull. So treat agent-memory as a scratchpad: if a lesson is worth
   keeping, it is worth promoting to a shared, checkable place from the list above.

### Manual QA — external testers execute what you write, literally

Manual QA runs on deployed **staging**, browser-only, by external testers with no repo access, no
Docker, no CLI — and no way to tell whether a ticket is still true. They pair with a general-purpose
LLM that has none of this repo's context, so a wrong scenario line does not get questioned: it gets
executed and reported as a bug. **Write QA tickets with `/qa-ticket`; re-baseline an existing one
with `/qa-ticket #<n>` before handing it over.**

- **An unverified scenario is a defect.** Every line must be backed by something you actually read
  in `src/` (the `.AllowAnonymous()`, the validator rule, the razor condition, the literal message
  string) or by a live query against staging. Of the first QA batch's 8 bug reports only 2 were
  valid findings; the misses trace to ticket lines that were stale (#603), unexecutable on staging
  (#547), contradicted by the code (#546), or invited browser-side fault injection (#602, #604).
  Tickets are written from `docs/specs/`, the product then moves, and nothing re-checks them — #271
  claimed the `polish.txt` download was auth-gated for a month after #309 made it public.
- **Classify every scenario:** plain (browser-only), _(owner-assisted — SKIP unless the owner runs
  it)_, or **blocked: what is missing**. Never "optional" — optional invites improvisation, and
  improvisation is what produced #602/#604. DevTools "Offline" is not API downtime: it cuts
  browser→frontend, and nothing server-rendered can render at all.
- **Every QA ticket carries the read-first block** (the SSR/fault-injection/do-not-improvise briefing
  — verbatim copy lives in `.claude/commands/qa-ticket.md`). It is the tester briefing, delivered
  where they actually read it.
- **Hand over at most 3 tickets at a time**, and give every filed bug a verdict the same day
  (`valid` / `by design + why` / `needs retest`). A 15-ticket batch rots; without the feedback loop a
  tester keeps applying a wrong mental model and each wrong report is paid for twice.
- **A QA ticket closes when the run is reported, not when the bugs are fixed (owner rule, 2026-08-20).**
  A QA ticket records a *test execution*, so its done is: every scenario has a status, every non-pass
  has a same-day verdict, and every `valid` finding has its **own** ticket. Then it closes — with a
  summary comment linking the results sheet. The retest obligation moves **onto the bug ticket**
  (an acceptance criterion naming the scenario, e.g. "re-run QA-FE-01 / `S01_PUBLIC` TC01"), because
  that is where someone will actually look. Never reopen a closed QA ticket: the product moved, so
  the next pass is a fresh, re-baselined run (`/qa-ticket #<n>`), not a resumed one. **The exception is
  `blocked:`** — a scenario that could not be executed produced no information, so that coverage does
  not exist yet and the QA ticket (or a follow-up run) stays open. `FAILED` = information obtained,
  work done; `blocked:` = work not done. Worked example: #262 closed at 6/7 with the failures carved
  out to #670 and #672.
- **Preconditions are the owner's job, not the tester's.** Non-default row states come from
  `scripts/qa/seed-staging.sql` (staging only; requires one approve in the UI afterwards to rebuild
  the artifact). Sample `exported.txt` files and the admin login are owner-provided — a scenario
  without its precondition ships as `blocked:`, never as a hopeful checkbox.

### Loop mode — one ticket = one closed PR, in its own fresh headless process

Working the backlog autonomously has **two non-negotiables: one ticket = one closed PR (git
hygiene), and one ticket = one fresh context (cost + quality).** Different rules; both must hold.

**The loop is a SCRIPT, not a session — `scripts/claude/backlog-loop.sh` (the conductor).**
Deterministic bash picks the next ready ticket (`next-ticket.sh`: priority labels + the
`Depends on #X` gate + skip rules for qa/post-mvp/audit/Windows-only work) and runs it to completion in
a **fresh headless process** (`work-ticket.sh` → `claude -p "/work-ticket <n>"`). This repo is
public, so **only maintainer-written tickets may drive the loop**: `issue-trust.sh` refuses any
issue whose author *or any commenter* lacks write access, fails closed, and is enforced in front of
the session — naming a ticket explicitly cannot bypass it (ADR-0026). The per-ticket
session does the whole slice — spec weight, branch, implement, tests, `code-reviewer` gate,
commit → push → PR — then **dies**; the runner judges only its final `STATUS: DONE|BLOCKED` block,
waits for pr-verify, squash-merges (**never `--delete-branch`** — branches are kept), syncs main,
and moves on. No LLM context outlives a ticket, so per-ticket cost stays flat no matter how many
tickets run overnight. Earlier designs kept an orchestrator *session* alive across tickets (first
`/loop /ticket`, then a subagent-spawning `/backlog` orchestrator) — both accumulate N tickets'
returns in one context and re-read it every turn; that anti-pattern is retired. (Parallelism
across *independent* tickets would be a separate opt-in move — worktree per ticket; the loop is
deliberately serial.)

**Git hygiene — fully close each ticket before the next; never let two tickets' work share an
uncommitted working copy.** The runner enforces it mechanically: it refuses to start on a dirty
working copy, runs strictly serially, commits (never deletes, never stashes) anything a failed or blocked run
leaves behind on a dedicated `loop-salvage/<n>-<timestamp>` branch, and returns to a
freshly-pulled main between tickets. The worker (`/work-ticket`) never merges — the runner owns
the merge gate, and that gate also refuses any PR with **open CodeQL alerts** (fail closed; the
worker clears them first, see §5). BLOCKED tickets get the
`loop-blocked` label plus the open questions posted as an issue comment — triage is
`gh issue list --label loop-blocked` (raw per-ticket session logs stay in
`logs/claude-loop/<run>/` for debugging only). Business questions are **extracted for the user,
never invented** — that rule binds the worker and the conductor alike.

Entering loop mode (`/backlog`, or an explicit "work through the backlog") **is** the standing
authorization for commit → push → PR → merge — the interactive "ask before pushing" rule (§5 above)
is waived for the duration of the loop. A single wholesale lift (e.g. the AuthSystem module) is
**one ticket**: a large diff there is expected and fine — what's not fine is two tickets' worth of
files sitting uncommitted at once, or two tickets sharing one context. Full manual (overnight
runs, env knobs, triage, troubleshooting): **`docs/claude-loop.md`**.

## Roadmap (digest — details live as GitHub issues; `gh issue list`)

- **M2 — TMS backend — DONE** (core loop + update lifecycle — spec 0001): version-bound import
  of `exported.txt` + diff/invalidation, list/get/upsert/approve translations (approve clears
  invalidation + regenerates the artifact), translation-file distribution (pre-built artifact +
  ETag/304, `GET /translation-files/{lang}`), GameVersion endpoints, CLI auto-download (M2-20 —
  `Features/TranslationFileSyncing` + `UpdateChecking`). **The forum watcher (M2-18 / #85) is
  deliberately post-MVP** — owner decision (2026-06, ADR-0030): game-version registration stays
  manual, like the export→import pipeline. Don't work #85 without an explicit owner go-ahead.
- **M3 — Frontend (Blazor Static SSR) — DONE** (manual QA pass **QA-FE / #275 still open**):
  lifted OIDC infra; pages: dashboard, translation list, editor with `<--DO_NOT_TOUCH!-->`
  placeholder validation + approve flow, import/export, game-versions admin, "Moje konto"
  (data export + account-deletion UX — LEGAL-02) and terms of service (LEGAL-03).
- **M6 — Deployment — DONE:** CD over ssh + `docker compose` to a **Hetzner VPS** (Caddy ingress,
  Let's Encrypt TLS) + Neon Postgres, staging + prod two-stage promotion, secrets in a `chmod 600`
  `/opt/lotro/.env` per box, daily health ping (ADRs 0008–0029 for the pipeline; **ADR-0034** moved
  the hosting off Azure Container Apps on 2026-07-12, retiring the Terraform IaC, Key Vault, warm
  window and revision sweep with it — epic #486). Ops details: `docs/deployment/runbook.md`.
- **M4 — desktop player app (Avalonia — ADR-0033)** (not started; #41–#46): GUI over the patcher
  handlers + the same TMS auto-download the CLI ships in M2-20. MVP is Windows-only, but the
  framework choice keeps the Steam Deck/Proton path open (WPF was dropped — it's the one .NET UI
  framework that closes it; the Russian project's Elanor→Qt rewrite is the cautionary tale).
- **LEGAL — GDPR/compliance pack (epic #459, cut 2026-07-11):** two-phase account deletion,
  data export, ToS + privacy policy, cookie banner, self-hosted fonts.
- **M7 — Game-content catalog — NEXT UP (spec 0008, agreed 2026-07-06; epic #362, tickets
  #363–#375 cut, not started).** LOTRO Companion's
  lore XML imported as a catalog lens over the flat rows: catalog entries (quest/deed +
  registry-driven long tail incl. items) with role-tagged text slots joined **by
  `(FileId, GossipId)` keys, never text** (verified —
  `docs/knowledge-base/lotro-companion-data-model.md`); admin zip import (replace-per-kind, COPY
  idiom), catalog browser + per-entry/per-category Approved-based progress, atomic quest
  translation UX (entry page + editor context + entry bulk approve), translation→entry
  memberships. The lens never mutates translations and never triggers the artifact. Naming rule:
  **CatalogEntry, never "entity"** (DDD-Entity misconception — user decision 2026-07-06).
  **DoD:** import fixture → browse `/catalog` → translate a whole quest via editor context →
  entry bulk approve → artifact contains the rows (E2E).
- **Post-MVP backlog (deliberately cut from MVP):** glossary, `TranslationHistory`, bulk
  operations, keyboard shortcuts, AI review, Discord notifications, public API versioning,
  crowdsourced game-version reports, per-language roles, Companion data auto-fetch from GitHub.
  (The former "LOTRO Companion XML context import + quest browser" items were promoted to M7.)
  **Epic TP-00 (#377)** parks the post-M7 productivity/ecosystem pack with its evidence:
  TM-lite duplicate propagation (45% of corpus measured), Companion reference labels (named
  `${PLAYER}` placeholders + RU/DE/FR reference panel), per-patch worklist, launch sentinel
  (DAT-repair gap), lotro-data version watcher, glossary seed, quest arcs, `labels/pl` reverse
  export — promotion order on the epic; TP-01/TP-10 are `/spec`-first.

## Proactive command use

The `/ticket`, `/spec`, `/feature`, `/adr`, `/qa-ticket` workflows are model-invocable — reach for
them yourself when the request matches, without waiting for the user to type the slash:

- User references **a ticket number or pastes an issue** → run **`/ticket`**.
- User wants a **manual QA scenario written, refreshed or handed to a tester** → **`/qa-ticket`**.
  Same when triaging a tester's bug report: verify the claim against the code before answering, and
  re-baseline the QA ticket that produced it.
- User floats a **rough feature idea** with unclear business rules → **`/spec`** first.
- User describes a **concrete new use case** → mirror the nearest sibling slice (note:
  **`/feature` scaffolds the patcher-style Application slice** — for TMS slices mirror
  TheKittySaver + the de-mediatorization recipe until the skill is updated).
- User is **settling an architecture/modeling choice** → **`/adr`** first, then implement.
- Any **DAT binary format work** → hand off to the **`dat-format-expert`** agent.
- User says **"kontynuuj pracę w pętli" / "continue the loop" / "work through the backlog" /
  "jazda dalej"** (any keep-grinding-tickets phrasing) → invoke **`/backlog`**, which launches
  `scripts/claude/backlog-loop.sh` in the background — one fresh headless `claude -p` process per
  ticket. NEVER grind tickets inline in the current session and never spawn per-ticket subagents
  from it — both balloon one context, the exact anti-pattern Loop mode retires — and never route
  to `/loop`.

Don't narrate "I'll run the command" — just follow the workflow and report results. Never scaffold
off a vague one-liner: if a business rule is unclear, ask once, then proceed.
