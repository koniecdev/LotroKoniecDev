# CLAUDE.md — LotroKoniecDev

> Project memory — **self-contained**: a fresh clone has everything the AI needs, with no
> machine-local config required. When a doc and the code disagree, **the code wins**: read the
> file, use what's there, and fix or flag the stale doc.

## What this is

A **LOTRO Polish translation platform** on **.NET 10 / C# 13** — **two bounded contexts in one
repo**, integrating through a file contract:

1. **Patcher** (shipped, **frozen**) — CLI that exports English texts from the game's binary DAT
   file (`export`), injects `||`-format Polish translations back (`patch`), and launches the game
   (`launch`). A WPF player app (M4) will reuse its Application handlers.
2. **TMS — Translation Management System** (M2/M3, in progress) — PostgreSQL + Web API + Blazor
   SSR + self-hosted OpenIddict auth: translators import the CLI export, edit with review
   workflow, and export `polish.txt` back for patching.

**Architectural identity:** every TMS pattern is lifted **1:1 from TheKittySaver**
(`~/RiderProjects/TheKittySaver` — the canonical reference for Vertical Slice Architecture, DDD
domain, Result monad, the OpenIddict auth server, Docker/compose, testing discipline), with
**one repo-wide deviation: NO MEDIATOR (ADR-0001)**. KittySaver uses `Mediator.SourceGenerator`;
every lifted slice is de-mediatorized on entry (recipe below). `Mediator`/`MediatR` packages are
forbidden — never add them back.

## Project status — pre-release, no users

Active development, zero production users. **Breaking changes are free** — no back-compat shims,
no deprecation windows. M1 (patcher) is done and empirically proven. Current milestone: **M2 —
TMS backend** (ADR-0002 + the agreed spec 0001 record the pivot and the update-lifecycle domain).
Live backlog: `gh issue list`,
**but** issues are being re-cut after the 2026-06 architecture pivot — where an old issue body
conflicts with this file (MediatR, one shared Application for all UIs, auth postponed to M5),
**this file wins**; align the ticket before coding.

## Architecture — two bounded contexts, one file contract

```
src/
  LotroKoniecDev.{Primitives,Domain,Application,Infrastructure,Cli}                       ← PATCHER (exists, frozen)
  SharedKernel/LotroKoniecDev.SharedKernel                                                ← M2 (lift; TMS-side only)
  TranslationSystem/LotroKoniecDev.TranslationSystem.{Primitives,Domain,ReadModels,ReadModels.EntityFramework,Persistence,Contracts,API} ← M2 (new)
  AuthSystem/LotroKoniecDev.AuthSystem.{API,Domain,Infrastructure,Persistence,Contracts}  ← M2 (lift)
  Frontend/LotroKoniecDev.Frontend                                                        ← M3 (Blazor SSR, OIDC RP)
  Utilities/…                                                                             ← M2 (lift only what's used)
```

**The contexts share a data contract, not code: the `||` translation file.** CLI `export` →
`exported.txt` → TMS import; TMS export → `polish.txt` → CLI `patch`. Each context owns its own
parser/serializer; **golden fixture files + round-trip tests on both sides** guard against format
drift, and the format itself changes only via ADR. The TMS never references `datexport.dll`/DAT
code (it runs in Linux Docker); the patcher never touches the DB (it runs on a Windows gaming
box). Distribution is HTTP, not integration: the CLI launch flow auto-downloads the current
translation file from the TMS API (ETag-cached; M2-20, the sole freeze exception — ADR-0002
amendment), and the WPF app (M4) is a GUI over the same patcher handlers + download.

### Patcher — frozen (do not refactor)

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

**Frozen means:** bugfix tickets only — no renames, no extractions, no restructuring in service
of the TMS. The TMS deliberately duplicates the few tiny building blocks it needs (Result/Maybe/
Error shapes, messaging interfaces — they arrive inside the lifted SharedKernel); consolidating
that duplication is at most a post-MVP ticket. Any change here must keep every existing test
green without touching its assertions. One sanctioned additive exception (ADR-0002 amendment,
spec 0001): the M2-20 translation-file auto-download slice in the launch flow.

### TMS — the KittySaver lift map

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
| Docker / compose | `compose.yaml`, `Dockerfile.migrator`, `Dockerfile.tests` | postgres + migrator + auth-api + tms-api (+ mailpit/aspire-dashboard in dev), serving HTTPS. **Backend-only — the Frontend is NOT a compose service; it runs on the host via `dotnet run` like TheKittySaver (ADR-0006)** |

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
| Build/change a **TMS slice** | the nearest sibling slice in TheKittySaver (`AdoptionSystem.API/Features/…`) — mirror it, then apply the de-mediatorization recipe |
| Work a GitHub ticket end-to-end | run **`/ticket <number>`** (mind the pivot-supersedes rule in Project status) |
| Touch DAT binary parsing / writing / native interop | delegate to the **`dat-format-expert`** agent |
| Re-investigate update behavior, vnum, translation survival, launch flow | **don't** — empirically settled in `docs/knowledge-base/` (start at its README) |
| Make a non-trivial architectural/modeling decision | skim `docs/adr/`, then **write a new ADR** (`/adr`); anchors: 0001 (no mediator), 0002 (TMS pivot + freeze amendment), 0008 (cloud-agnostic deployment + env strategy — M6) |
| Touch the update lifecycle (GameVersion, import diff, invalidation, distribution, CLI sync) | `docs/specs/0001-game-update-lifecycle-and-translation-invalidation.md` — the agreed domain spec |
| Implement a feature whose business rules are fuzzy | **`/spec`** first (seed → questions → agreed spec in `docs/specs/`) |
| Review a finished change | the **`code-reviewer`** agent |
| Understand the backlog / milestones | `gh issue list` (being re-cut post-pivot) + Roadmap digest below |
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

# Run the CLI (Windows; needs LOTRO + admin for DAT write)
dotnet run --project src/LotroKoniecDev.Cli -- export                 # DAT → data/exported.txt
dotnet run --project src/LotroKoniecDev.Cli -- patch polish           # translations/polish.txt → DAT
dotnet run --project src/LotroKoniecDev.Cli -- launch polish          # hash-check → patch if changed → launch
# or the elevated .bat wrappers: export.bat / patch.bat / lotro.bat

# GitHub tickets (BRD/spec-driven flow)
gh issue list --state open                             # backlog; titles follow "M{milestone}-{nn}: Title"
gh issue view <n>                                      # body holds Context / Depends on / Tasks / Acceptance criteria
gh issue develop <n> --checkout                        # create + checkout the linked "{n}-{kebab-title}" branch
gh pr create --fill --body "Closes #<n>"               # PR title mirrors the ticket; body closes it

# TMS — EF Core migrations (write context owns them; --connection makes it work without appsettings/live DB)
dotnet ef migrations add <Name> \
  --project src/TranslationSystem/LotroKoniecDev.TranslationSystem.Persistence \
  --startup-project src/TranslationSystem/LotroKoniecDev.TranslationSystem.Persistence \
  --context ApplicationWriteDbContext \
  -- --connection "Host=localhost;Database=lotro_translation;Username=postgres;Password=changeme"

# TMS — Docker compose stack (BACKEND-ONLY: postgres + migrator + auth-api + tms-api + aspire-dashboard + mailpit)
docker compose up -d                                   # boots the M2 backend over HTTPS; requires a .env + dev cert — scripts/up.sh creates both
docker compose build [<service>]                       # rebuild the API/migrator images after code changes
docker compose logs -f migrator                        # watch the one-shot schema migration (TMS + Auth contexts)
docker compose down                                    # stop; add -v to also drop the postgres volume (fresh DB)
# Endpoints (HTTPS): tms-api :5002 · auth-api :5003 · aspire :18888 · mailpit :8025
#   (e.g. curl -k https://localhost:5002/health). In-network API↔API still uses http://…:8080.
# scripts/up.sh | up.ps1 = recommended boot — bootstraps .env from .env.example AND (via
#   scripts/init-dev-https.{sh,ps1}) the ASP.NET dev cert PFX into .docker/https/, then up.

# Frontend (Blazor SSR) — NOT in compose (ADR-0006); runs on the host like TheKittySaver, against the in-compose backend
dotnet run --project src/Frontend/LotroKoniecDev.Frontend   # https://localhost:7017 → hits auth-api :5003 + tms-api :5002 over HTTPS
# appsettings.Development targets the COMPOSE backend (tms :5002). For the all-local workflow (every API via
# its own `dotnet run`), tms-api's https launchSettings port is :5004 — point TranslationSystem:BaseUrl there.
# auth-api is :5003 in both workflows, so its Authority/BaseUrl + the token `iss` need no change.

# TMS — Production-PARITY stack (compose.prod.yaml; ADR-0008 §4 / M6-07) — SEPARATE from dev compose.
# ALL FOUR images + a Caddy reverse proxy run under ASPNETCORE_ENVIRONMENT=Production: real OpenIddict
# keys, DP keyring volumes (auth + frontend), self-hosted Postgres over SSL, the containerized Frontend.
# Catches prod-only breakage on a laptop before staging. Coexists with the dev stack (separate project).
scripts/up-prod.sh | up-prod.ps1                       # recommended boot — bootstraps .env.prod (with generated
                                                       #   OpenIddict secrets) + local CA/proxy/Postgres certs, maps the
                                                       #   *.lotro.test vhosts to loopback (idempotent; admin only the
                                                       #   first time), then up. Args pass through (e.g. --build, -d).
docker compose -f compose.prod.yaml --env-file .env.prod up --build      # raw command (after the bootstraps + a manual hosts entry)
docker compose -f compose.prod.yaml --env-file .env.prod --profile local-smtp --profile local-otel up  # + mailpit + aspire (all-local; needed for green auth /health/ready)
docker compose -f compose.prod.yaml --env-file .env.prod down            # add -v to drop prod volumes (fresh DB/keys)
# up-prod.{sh,ps1} runs both one-time bootstraps for you: gen-openiddict-keys (3 secrets → .env.prod) +
#   init-prod-https (local CA → .docker/prod-https/). It also auto-maps the hosts file (cross-platform:
#   sudo on macOS/Linux, UAC on Windows); manual equivalent: 127.0.0.1 app.lotro.test auth.lotro.test tms.lotro.test
# Browser OIDC login: https://app.lotro.test. Health (trust the local CA):
#   curl --cacert .docker/prod-https/rootCA.crt https://auth.lotro.test/health/ready
```

The compose stack is **backend-only** (ADR-0006). Each API serves **HTTPS** on its host port (dev cert
mounted into Kestrel; `ASPNETCORE_URLS` is `https://+:8081;http://+:8080`, host port → :8081), while
in-network API↔API calls (e.g. tms-api → auth-api JWKS) keep using `http://…:8080`. The Frontend is **not**
a compose service: like TheKittySaver it runs on the host via `dotnet run` (`https://localhost:7017`), and
both it and the browser reach the in-compose backend at `https://localhost:5003` (auth) + `https://localhost:5002`
(tms) — so a single OIDC `Authority` serves the browser and the server-side back-channel and the token `iss`
matches. The cert is bootstrapped once by `scripts/init-dev-https.{sh,ps1}` (run automatically by
`scripts/up.{sh,ps1}` when `.docker/https/aspnetapp.pfx` is missing; password from `.env`
`ASPNETCORE_KESTREL_CERT_PASSWORD`). Dev uses **ephemeral** OpenIddict keys; production-like runs supply
real keys via env (see `.env.example`). The migrator is a one-shot container (TMS migrates through its
Persistence project, Auth through its API — only those carry EF Core Design); both APIs wait for it to
complete.

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
file_id||gossip_id||translated_text||args_order||args_id||approved
620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1
620756992||1002||Tekst z <--DO_NOT_TOUCH!--> argumentem||1||1||1
```

- `<--DO_NOT_TOUCH!-->` = argument placeholder
- `args_order`: `NULL` or `1-2-3` (1-indexed in file, 0-indexed internally)
- `\r`, `\n` in content are unescaped by parser
- Results sorted by FileId then GossipId for sequential DAT I/O
- **Changing this format requires an ADR + updated golden fixtures in BOTH contexts** (patcher
  parser tests and TMS import/export tests).

## Game update behavior (empirically proven — do not re-test, see knowledge base)

- **Forum version** (regex `Update\s+(\d+(?:\.\d+)*)\s+Release\s+Notes` on lotro.com) is the
  reliable game-version identifier. **DAT vnum is useless as a content version** (112/3 unchanged
  across 45.x→48.0 even while the DAT was actively patched).
- Launcher patches the DAT **chunk-based**; **translations survive updates** — proven across
  6 live tests incl. the 47.2→48.0 major update. `attrib +R` protection is unnecessary.
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
- **CQRS read/write split, day 1 (ADR-0002 amendment).** Query handlers read POCO `ReadModels`
  through `IApplicationReadDbContext`; command handlers load and mutate aggregates via
  repositories + `IUnitOfWork`. The write model never serves list/search queries; every new
  aggregate ships with its read model + EF configuration in the same change.
- **Patcher is frozen** (see Architecture) — never refactor it to serve the TMS.
- **TMS ships with auth from day 1.** Endpoints are authorized by default (public ones are
  explicit) and edited rows carry user attribution: `Translation.SubmittedById` is stamped on
  upsert (added in M2-11; persisted via the `IdentityId` converter in `TranslationSystem.Persistence`),
  and `ApprovedById` lands with the approve slice (#101 / M2-12). No auth-less interim state to
  retrofit later.
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
- **Right-size the design — YAGNI by default.** Before proposing an abstraction, cache, config
  knob, queue, or new infra, check it solves a **real, present** need from the current
  spec/ticket — not a hypothetical future. Pick the simple path and note the trade-off in one line.
- **Frontend is Static SSR — enforced, not just documented.** No WebAssembly, no SignalR circuit,
  no per-user server state; forms post via `<form method="post" @formname @onsubmit>` (the SSR
  `@onsubmit` special-case) or `<EditForm OnValidSubmit>` — never interactive `@on*` handlers,
  `@rendermode`, `StateHasChanged`, or `AddInteractive*`. `scripts/check-ssr-purity.sh` (with a
  `.ps1` twin for local Windows devs) gates this in **both** `pr-verify` and `ci`, before
  `setup-dotnet`. Genuinely need interactivity? That's an ADR-first architecture change.
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

### Patcher slice (frozen — reference only for bugfixes)

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
- Platform honesty: tests must pass on macOS AND Windows — `Path.Combine`, never hardcoded `C:\`.
- **TMS test projects mirror KittySaver naming:**
  `tests/LotroKoniecDev.TranslationSystem.Domain.Tests.Unit` (pure),
  `tests/LotroKoniecDev.TranslationSystem.API.Tests.Integration` (real PostgreSQL — never in a
  Unit project), Frontend unit tests in M3. Patcher test projects stay exactly as they are.

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
   Ask before pushing.
6. **Feed the flywheel.** Reusable correction → persist it: agent lesson →
   `.claude/agent-memory/<agent>/`; global rule → **this file**; real decision → new ADR;
   empirical DAT/update finding → `docs/knowledge-base/` (dated). The same mistake made twice
   means a rule is missing.

### Loop mode — one ticket = one closed PR, in its own fresh context

Working the backlog autonomously has **two non-negotiables: one ticket = one closed PR (git
hygiene), and one ticket = one fresh context (cost + quality).** Different rules; both must hold.

**Context isolation — the canonical loop is `/backlog`, not `/loop /ticket`.** A self-paced
`/loop /ticket` runs every ticket in the *same* growing session: ticket N's full transcript stays
in context while ticket N+1 works, per-turn cache-read cost climbs, lossy auto-compaction
eventually fires, and signal gets diluted — the opposite of "sharp context per ticket." Instead the
**`/backlog` orchestrator** spawns one **`ticket-worker`** subagent per ticket; each worker gets a
fresh, isolated context, does the whole slice, and returns only a compact summary. The orchestrator
stays thin (N summaries, not N transcripts) and never reads diffs or implements — so per-ticket
cost is bounded and predictable, with no context bleed between tickets. (Parallelism across
*independent* tickets is a separate opt-in move: background agents + a worktree per ticket; the
default loop is serial.)

**Git hygiene — fully close each ticket before the next; never let two tickets' work share an
uncommitted working copy.** Division of labour:

- **`ticket-worker`** (own context), per ticket: branch (`gh issue develop <n> --checkout`) →
  implement (green build, zero warnings) → unit/integration tests → **`code-reviewer`** agent
  (+ **`/security-review`** for native interop / file protection / auth) until APPROVE → commit
  (ticket ref + Co-Authored-By footer) → push → `gh pr create --fill --body "Closes #<n>"`. It does
  **not** merge. On an open *business* question, unmet dependency, mis-scope, or an unreachable
  green build / clean review, it returns **BLOCKED** instead of guessing.
- **`/backlog`** (thin orchestrator): merges each clean PR (`gh pr merge <n> --squash` — **no
  `--delete-branch`**; branches are kept, see house rules — then `git checkout main && git pull`),
  surfaces every BLOCKED ticket's questions
  to the user verbatim, and picks the next ready ticket.

Entering loop mode (`/backlog`, or an explicit "work through the backlog") **is** the standing
authorization for commit → push → PR → merge — the interactive "ask before pushing" rule (§5 above)
is waived for the duration of the loop. A single wholesale lift (e.g. the AuthSystem module) is
**one ticket**: a large diff there is expected and fine — what's not fine is two tickets' worth of
files sitting uncommitted at once, or two tickets sharing one context.

## Roadmap (digest — details land as re-cut GitHub issues)

- **M2 — TMS backend (core loop + update lifecycle — spec 0001).** ADR-0002 (record this pivot)
  → SharedKernel lift → AuthSystem lift → TranslationSystem
  (Primitives/Domain/ReadModels(+EF)/Persistence/Contracts/API) with exactly these slices: version-bound import of `exported.txt` + diff/invalidation (upload),
  list translations (search/filter/paginate, incl. `NeedsReview`), get one, upsert, approve
  (clears invalidation + regenerates the artifact), translation-file distribution (pre-built
  artifact + ETag/304, `GET /translation-files/{lang}`), GameVersion endpoints, forum watcher
  (creates unprocessed `GameVersion`) → compose (postgres + migrator + auth-api + tms-api) →
  integration tests → CLI auto-download (M2-20, the freeze exception).
  **DoD:** the full loop works: CLI `export` → TMS import (diff) → edit/approve → CLI `launch`
  auto-downloads → `patch` → texts visible in game; a simulated game update invalidates changed
  rows and the distributed file excludes them.
- **M3 — Frontend (Blazor SSR).** Lifted OIDC infra; pages: translation list, side-by-side
  editor with `<--DO_NOT_TOUCH!-->` placeholder validation, approve flow, import/export,
  mini-dashboard (progress counters). **DoD:** a translator completes the whole loop in the
  browser, authenticated.
- **M4 — WPF player app** (later): GUI over the patcher handlers + the same TMS auto-download
  the CLI ships in M2-20.
- **Post-MVP backlog (deliberately cut from MVP):** LOTRO Companion XML context import, glossary,
  quest browser, `TranslationHistory`, bulk operations, keyboard shortcuts, AI review, Discord
  notifications, public API versioning, crowdsourced game-version reports, per-language roles.

## Proactive command use

The `/ticket`, `/spec`, `/feature`, `/adr` workflows are model-invocable — reach for them yourself
when the request matches, without waiting for the user to type the slash:

- User references **a ticket number or pastes an issue** → run **`/ticket`**.
- User floats a **rough feature idea** with unclear business rules → **`/spec`** first.
- User describes a **concrete new use case** → mirror the nearest sibling slice (note:
  **`/feature` scaffolds the patcher-style Application slice** — for TMS slices mirror
  TheKittySaver + the de-mediatorization recipe until the skill is updated).
- User is **settling an architecture/modeling choice** → **`/adr`** first, then implement.
- Any **DAT binary format work** → hand off to the **`dat-format-expert`** agent.
- User says **"kontynuuj pracę w pętli" / "continue the loop" / "work through the backlog" /
  "jazda dalej"** (any keep-grinding-tickets phrasing, in a fresh or current session) → invoke
  **`/backlog`** (one isolated `ticket-worker` per ticket). NEVER grind tickets inline in the
  current session — that balloons one context, the exact anti-pattern Loop mode forbids — and
  never route to `/loop`.

Don't narrate "I'll run the command" — just follow the workflow and report results. Never scaffold
off a vague one-liner: if a business rule is unclear, ask once, then proceed.
