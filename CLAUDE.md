# CLAUDE.md — LotroKoniecDev

> Project memory, **self-contained**: a fresh clone has everything the AI needs, with no
> machine-local config. When a doc in this repo and the code disagree, **the code wins**: read the
> file, use what's there, and fix or flag the stale doc. The **wiki** is the one exception and
> outranks both (see "Source of truth").

## What this is

A **LOTRO Polish translation platform** on **.NET 10 / C# 14** (`Directory.Build.props` is
authoritative): **two bounded contexts in one repo**, integrating through a file contract:

1. **Patcher** (shipped, **stable**): a CLI that exports English texts from the game's binary DAT
   file (`export`), injects `||`-format Polish translations back (`patch`), and launches the game
   (`launch`). An Avalonia player app (M4, ADR-0033) will reuse its Application handlers.
2. **TMS — Translation Management System** (built and deployed): PostgreSQL + Web API + Blazor
   SSR + self-hosted OpenIddict auth. Translators import the CLI export, edit with a review
   workflow, and export `polish.txt` back for patching.

**Architectural identity:** Vertical Slice Architecture, a DDD domain, the Result monad, a
self-hosted OpenIddict sign-in server, Docker Compose and the testing discipline below. **This
repo's own code is the only reference:** a new slice mirrors its nearest sibling here, and nothing
outside the repo is a template. The TMS began as a copy of the patterns of TheKittySaver, the
owner's earlier project, which is why older ADRs name it; that copy is finished and the two code
bases have gone their own ways (ADR-0002 §4, amended 2026-10-11). **NO MEDIATOR, repo-wide
(ADR-0001):** in-house `ICommand`/`IQuery` handlers; the `Mediator`/`MediatR` packages are
forbidden. Never add them back.

## Project status — deployed, pre-launch (no real users yet)

Active development; what is done and what is open is in "Roadmap" below. No real users yet, so
**API/code breaking changes are free**: no back-compat shims, no deprecation windows. The one
exception is the **database schema**: the stack is deployed with zero-downtime CD, so migrations
follow ADR-0023 (forward-only, N-1 backward-compatible, expand→backfill→contract) whatever the user
count. Live backlog: `gh issue list`. Where an issue body conflicts with this file, **this file
wins**: align the ticket before coding.

## Architecture — two bounded contexts, one file contract

```
src/
  Patcher/LotroKoniecDev.{Primitives,Domain,Application,Infrastructure,Cli}               ← PATCHER (stable)
  SharedKernel/LotroKoniecDev.SharedKernel                                                ← TMS-side building blocks
  TranslationSystem/LotroKoniecDev.TranslationSystem.{Primitives,Domain,ReadModels,ReadModels.EntityFramework,Projections,Persistence,Contracts,API}
  AuthSystem/LotroKoniecDev.AuthSystem.{API,Domain,Infrastructure,Persistence,Contracts}  ← self-hosted OpenIddict
  Frontend/LotroKoniecDev.Frontend                                                        ← Blazor Static SSR, OIDC RP
  Utilities/LotroKoniecDev.{Hateoas,Hateoas.Abstractions,Logging,Options}
```

(`TranslationSystem.Projections` is the in-house precomputed translation-file store behind the
distribution endpoint.)

**The contexts share a data contract, not code: the `||` translation file.** CLI `export` →
`exported.txt` → TMS import; TMS export → `polish.txt` → CLI `patch`. Each context owns its own
parser/serializer; **golden fixture files + round-trip tests on both sides** guard against format
drift, and the format changes only via ADR. The TMS never references `datexport.dll`/DAT code (it
runs in Linux containers); the patcher never touches the DB (it runs on a Windows gaming box).
Distribution is HTTP, not integration: the CLI launch flow auto-downloads the current translation
file from the TMS API (ETag-cached; M2-20), and the M4 app is a GUI over the same patcher handlers
and download.

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
| `tests/LotroKoniecDev.Tests.{Unit,Infrastructure,E2E}` | patcher tests (Infrastructure and E2E run on Windows only) |

**Stable, not frozen** (ADR-0002 amendment, 2026-06-25): the bar for touching the patcher is high,
but refactors, renames and restructuring are allowed when they earn their keep. **Any change must
keep every existing test green without touching its assertions**, and behaviour proven in
`docs/knowledge-base/` must not regress. The TMS deliberately duplicates the few tiny building
blocks it needs (Result/Maybe/Error shapes, messaging interfaces, in the SharedKernel);
consolidating that is an opt-in cleanup, not a mandate. The DAT/`||` file format still changes only
via ADR + updated golden fixtures.

### TMS — vertical slices in the API project

A **new** TMS slice mirrors the nearest existing sibling slice, and "Anatomy of a feature slice"
below gives its shape: a record on the in-house `ICommand`/`IQuery`, a handler whose closed
interface is registered in DI explicitly, and validation and logging inside the handler.

**Deliberately absent (YAGNI; revisit only on a real, present need):** domain events. If a need
appears, design an in-house dispatcher via ADR first.

## Source of truth — the wiki outranks specs, ADRs and code

The GitHub wiki (`koniecdev/LotroKoniecDev.wiki`, cloned **beside** the repo, never inside it) is
where the owner writes down how the product is meant to behave. It is the **highest authority in
the project**: above `docs/specs/`, above `docs/adr/`, above the code. The code-wins rule above
settles a repo doc against the code; it does not outrank the wiki.

The wiki is also the only source a manual tester has. Testers write and run QA tickets from it with
no repo access, so a wiki that disagrees with the product produces false bug reports that someone
pays to close.

**When the wiki and the product disagree, never fix it silently and never "just align the wiki".**
The order is fixed:

1. **Open a ticket for the inconsistency.** State what the wiki says, what the product does, and
   where each is asserted (file, ADR section, wiki line). Do not decide it yourself: the wiki holds
   business intent and only the owner rules on intent.
2. **The verdict lands in the wiki first.** Until the wiki says it plainly, the question is not
   settled, and nothing downstream may be built on the answer.
3. **Then open a second ticket to bring code, specs and ADRs in line with the corrected wiki,**
   citing the wiki as its authority. An ADR that now contradicts the wiki is amended or superseded;
   it does not win because it was written first.

Worked example (#726 → #728): the wiki said a LOTRO version has one to three segments, while
`LotroNotationVersion` accepted any count and ADR-0003 §3 called that deliberate. The fix was not
to soften the wiki: the owner ruled, the wiki stated it, and #728 amended ADR-0003 and the
validator.

## Read-first routing (do this BEFORE touching the area)

| You're about to… | Read first |
|---|---|
| Build/change a **TMS slice** | the nearest existing sibling slice in `TranslationSystem.API/Features/…`; no fitting sibling → "Anatomy of a feature slice" below |
| Work a GitHub ticket end-to-end | **`/ticket <number>`** (this file wins over the ticket — see Project status) |
| Triage tickets before working them (premise check, no implementation) | **`/preflight <numbers…>`**: wiki-first READY/CLARIFY/BOUNCE verdict comment + lane per ticket; then `/ticket` per READY one, each in a fresh session |
| Make a PR body, report or comment readable for a non-native reader | **`/b2-english <PR# / comment URL / file>`**; `/ticket` and `/work-ticket` run it on the PR body before `gh pr create`, so a PR opens with its final text (#1083) |
| Touch the `\|\|` translation file (parser, serializer, a column) | `README.md` → "Translation file format", the rules digest below, ADRs 0039/0042/0043/0047; golden fixtures on both sides; the format changes only via ADR |
| **File** an issue, label one, or title one | `docs/labels.md` *before* `gh issue create`: the five axes, the title convention, the three-signal epic rule and one bug class per ticket. It is shared 1:1 with the maintainer's other repo; a change in one is ported to the other in the same session |
| Run the backlog autonomously (Loop mode) | **`scripts/claude/start-loop.sh <numbers>` in a plain terminal**, never from a session (#969); **`/backlog <numbers>`** prints that line, `/backlog` after the run reports the roll-up. Manual: `docs/claude-loop.md` |
| Merge reviewed PRs | **`/merge-train`**: merges only PRs the owner approved by assigning themselves after the last push, and fixes an approved PR's conflicts itself (ADR-0060) |
| Touch DAT binary parsing / writing / native interop | delegate to the **`dat-format-expert`** agent |
| Re-investigate update behaviour, vnum, translation survival, launch flow | **don't**: empirically settled in `docs/knowledge-base/` (start at its README) |
| Make a non-trivial architectural/modeling decision | skim `docs/adr/`, then **write a new ADR** (`/adr`); anchors: 0001 (no mediator), 0002 (TMS pivot + freeze/unfreeze amendments), 0008 (cloud-agnostic deployment + env strategy), 0009 (browser E2E via Testcontainers + Playwright) |
| Deploy/operate the stack, or set env vars per environment | `docs/deployment/runbook.md`: env-var matrix, secret generation, the issuer/redirect/authority/CORS gotchas, bring-up sequence, DB migrations. Ingress: ADR-0034 (Caddy) + **ADR-0041** (no gateway behind it) |
| Add a proxy/gateway, expose a new service, or wire a client to an API path | **ADR-0041**: no API gateway. Clients resolve endpoints by **rel name** (ADR-0040's vocabulary), never by hardcoded path |
| Touch the update lifecycle (GameVersion, import diff, invalidation, distribution, CLI sync) | `docs/specs/0001-game-update-lifecycle-and-translation-invalidation.md` |
| Touch update-day behaviour on the client (sentinel, orchestrator, any new DAT write path) | `docs/specs/0012-update-resilience.md` (Tier 0/1 rules as amended 2026-08-17) + **ADR-0047**: every write goes through the per-row source guard; the no-masking invariant has no path exceptions |
| Touch the game-content catalog (CatalogEntry/TextSlot, Companion zip import, catalog browser, memberships) | `docs/specs/0008-game-content-catalog-layer.md` + `docs/knowledge-base/lotro-companion-data-model.md` (the verified `key:<FileId>:<GossipId>` join; never join on text). Naming rule: **never "entity"** in this layer |
| Implement a feature whose business rules are fuzzy | **`/spec`** first (seed → questions → agreed spec in `docs/specs/`) |
| Write or hand over a **manual QA ticket** (external tester) | **`/qa-ticket`**: every scenario verified against HEAD first; `/qa-ticket #<n>` re-baselines an existing one |
| Review a finished change | the **`code-reviewer`** agent |
| Sort review findings, file one, or triage an `audit` ticket | **ADR-0067 §8** (what is not a finding) + ADR-0008 §3 (the bar for a startup check) + `docs/labels.md` → "One bug class per ticket" |
| Understand the backlog / milestones | `gh issue list` + Roadmap below |
| Compare with the Russian sister project | `docs/RUSSIAN_PROJECT_RESEARCH.md` + `docs/knowledge-base/russian-project.md` |

## Commands

```bash
# Build — zero-warnings gate: TreatWarningsAsErrors is on repo-wide; any warning IS a failing build
dotnet build LotroKoniecDev.slnx

# Tests — the unfiltered run is the gate before EVERY push; the filtered ones are for iterating only.
# The TMS and auth integration suites and both *.E2E.Tests suites need Docker. Every suite and what
# it needs: tests/CLAUDE.md.
dotnet test                                            # everything runnable on this OS; exits 0 when it all passes
dotnet test tests/LotroKoniecDev.Tests.Unit            # fast, pure unit (must always be green)
dotnet test --filter "FullyQualifiedName~Fragment"     # filter by name

# Run the CLI (Windows; needs LOTRO. `export` only reads and needs NO admin — #629; DAT writes do)
dotnet run --project src/Patcher/LotroKoniecDev.Cli -- export                 # DAT → data/exported.txt
dotnet run --project src/Patcher/LotroKoniecDev.Cli -- patch polish           # translations/polish.txt → DAT
dotnet run --project src/Patcher/LotroKoniecDev.Cli -- launch polish          # hash-check → patch if changed → launch
# or the .bat wrappers: export.bat (no elevation) / patch.bat, lotro.bat (self-elevate)

# GitHub tickets
gh issue list --state open                             # backlog; titles follow "M{milestone}-{nn}: Title"
gh issue view <n>                                      # body holds Context / Depends on / Tasks / Acceptance criteria
gh issue develop <n> --checkout                        # create + checkout the linked "{n}-{kebab-title}" branch
gh pr create --fill --body "Closes #<n>"               # PR title mirrors the ticket; body closes it

# Backlog loop — start it in a plain TERMINAL, never from a Claude Code session (#969). Manual: docs/claude-loop.md
scripts/claude/start-loop.sh 123 130 131               # exactly these tickets (the normal use); -j 1 = one at a time; -n 3 = next 3 ready
scripts/claude/next-ticket.sh                          # print the next READY ticket

# TMS — EF Core migrations (the write context owns them; --connection works without appsettings or a live DB).
# dotnet-ef is a pinned local tool (dotnet tool restore). No --startup-project: dotnet-ef 10.0.9
# mis-parses it when it equals --project.
dotnet ef migrations add <Name> \
  --project src/TranslationSystem/LotroKoniecDev.TranslationSystem.Persistence \
  --context ApplicationWriteDbContext \
  -- --connection "Host=localhost;Database=lotro_translation;Username=postgres;Password=changeme"

# TMS dev — infra-only compose + three host Kestrels (ADR-0006, amended #190/M6-14)
scripts/up.sh                                          # (up.ps1 on Windows) creates .env from .env.example, then `docker compose up`:
                                                       #   postgres + rabbitmq + migrator + mailpit + aspire; NO app images
docker compose up --build migrator                     # rebuild the migrator image after adding an EF migration
docker compose down                                    # add -v to also drop the postgres volume (fresh DB)
dotnet dev-certs https --trust                         # one-time, so the host Kestrels serve HTTPS
# The apps run on the HOST (hot reload, breakpoints): Rider compound ".run/TMS dev (all hosts)", or each one:
dotnet run --project src/AuthSystem/LotroKoniecDev.AuthSystem.API                 # auth-api → https://localhost:5003
dotnet run --project src/TranslationSystem/LotroKoniecDev.TranslationSystem.API   # tms-api  → https://localhost:5002
dotnet run --project src/Frontend/LotroKoniecDev.Frontend                         # frontend → https://localhost:7017
# aspire :18888 · mailpit :8025

# TMS — production-PARITY stack (compose.prod.yaml; ADR-0008 §4) — separate from the dev compose
scripts/up-prod.sh                                     # (up-prod.ps1) bootstraps .env.prod (generated secrets), the local CA and
                                                       #   certs, maps *.lotro.test to loopback, then up. Args pass through (--build, -d)
docker compose -f compose.prod.yaml --env-file .env.prod --profile local-smtp --profile local-otel up  # + mailpit + aspire (all-local)
docker compose -f compose.prod.yaml --env-file .env.prod down            # add -v to drop prod volumes (fresh DB/keys)
# Browser OIDC login: https://app.lotro.test
```

**Dev stack:** `compose.yaml` runs infrastructure only, and the three apps run on the host with the
native ASP.NET Core dev cert. The browser, the host RP and the host resource server all reach
`localhost:5003`/`:5002` the same way, so one OIDC `Authority`/`Issuer` serves every leg and the
token `iss` matches (tms-api falls back from `Auth:Authority` to `Auth:Issuer`,
`AuthSettings.EffectiveAuthority`). Dev uses ephemeral OpenIddict keys. `compose.prod.yaml` is the
only compose stack that runs the apps themselves in containers, from the same Dockerfiles as the
boxes: all four images plus Caddy under `ASPNETCORE_ENVIRONMENT=Production`, one origin per app
(`app|auth|tms.lotro.test`), real OpenIddict keys, DP keyring volumes, Postgres over SSL. Both
tms-api and the Frontend reach auth **through the proxy** (OpenIddict rejects plain HTTP in
Production), and `.docker/trust-ca-entrypoint.sh` installs the local CA into their OS trust store,
because .NET ignores `SSL_CERT_FILE` (parity stack only: real prod has a publicly trusted cert).
Secrets live in the git-ignored `.env.prod`, TLS material in `.docker/prod-https/`. Full procedure:
`docs/deployment/runbook.md` → "Bringing the stack up".

Exit codes (CLI): `0` success, `1` invalid arguments (incl. `ErrorType.Validation`), `2` file not
found, `3` operation failed, `4` cancelled.

## DAT binary format

The SubFile / Fragment / VarLen layout is catalogued in the **`dat-format-expert`** agent (every DAT
parse, serialize or interop change goes through it) and lives in the patcher's Domain parsers; the
empirical findings (chunk patching, survival, vnum) are in `docs/knowledge-base/`. Texts are
SubFiles with FileId high byte `0x25`.

## Translation file format — THE inter-context contract

Format, columns and worked examples: `README.md` → "Translation file format" (the one copy;
`<--DO_NOT_TOUCH!-->` is the argument placeholder). Each context owns its own parser/serializer, and
**golden fixtures + round-trip tests on both sides** pin the contract; results are sorted by FileId
then GossipId for sequential DAT I/O. The rules below stay in force; each ADR holds the reasoning:

- `args_order` / `args_id`: `NULL` or `1-2-3` (1-indexed in the file, 0-indexed internally);
  anything else **rejects the row and is reported** (ADR-0042): a CLI patch warning, a failed TMS
  upload. Whether the positions fit is checked downstream in `Fragment.TryReorderArgRefs`.
- **The line is CARVED, never `Split`** (ADR-0042, #597): `||` inside content and a trailing run of
  `|` are legal; each context's `TranslationLineCarver` scans forward for the id separators and
  backward for the tail (three or four fields, sniffed from the last field). Never reintroduce
  `Split`.
- **Content escape `\`→`\\`, CR→`\r`, LF→`\n`, injective** (ADR-0039): every writer escapes, every
  reader unescapes, via each context's `TranslationLineEscaper`; text held anywhere else is RAW.
- **Content ≤ 32767 UTF-16 code units, bounded by the DAT, not the file** (ADR-0043): refused at
  the TMS API (`UpsertTranslation.Validator` + a `CHECK` constraint, never a `varchar(n)`
  narrowing), warn-skipped by the patcher before any write (`Fragment.IsWritablePiece`);
  `Fragment.Write` still throws as the last resort.
- **Stale Polish never lands over changed English: an INVARIANT enforced per row at write time**
  (ADR-0047, #659). The 7th column `source_digest` (16 hex of the framed SHA-256 `SourceHash`) must
  match the fragment's English or what the patcher last wrote there (`<file>.ledger`), whatever path
  writes the DAT; otherwise the row is skipped and reported as `source moved`. No operator override;
  a six-column file is not patchable; digest parity is a golden fixture pinned on both sides.
- **Changing this format requires an ADR + updated golden fixtures in BOTH contexts.**

## Game update behavior — empirically settled, do not re-test

`docs/knowledge-base/` (README index) holds the proof: the **forum version** is the reliable game
version (DAT vnum 112/3 is dead as a content signal), the launcher patches **chunk-based** so
translations survive updates per SubFile (9 live tests incl. two majors; a modified SubFile reverts
its fragments, and repair is the normal re-patch / the spec-0001 invalidation loop), and the
simplified hash-check → patch → launch flow is validated. Re-investigating any of it is a BOUNCE.

## Project house rules

- **Zero warnings.** `TreatWarningsAsErrors` is repo-wide. Fix it; don't suppress it (a scoped
  `.editorconfig` exception needs a stated reason, like the `Result._value` guarded getter).
- **Errors are values, not exceptions.** Business failures → `Result.Failure(Error)` via
  `DomainErrors.*` factories / `Error.Validation(...)`. Guards (`Ensure`,
  `ArgumentNullException.ThrowIfNull`) are for **programmer** errors only. The API's
  `ExceptionHandlers/` are safety nets, not a control-flow mechanism.
- **A Warning means someone should look (#977).** Log a Warning only for what should never happen
  (a token with no user id, a failed database read) or may be an attack (a wrong password, a reused
  refresh token, a wrong PKCE verifier). A refusal that is the normal result of what the user or an
  admin did, or of time passing (an expired token, a token refused because the account was locked or
  deleted earlier), is Information. Production keeps Information, so nothing is lost; Debug is too
  low, because production drops it. When one log line covers both kinds, it is a Warning. A ticket
  that asks for "a warning" means "a log line": pick the level by this rule (#944 got it wrong).
- **No mediator; slim SRP handlers (ADR-0001), repo-wide.** One use case = one record + one handler
  implementing the in-house `ICommandHandler<,>`/`IQueryHandler<,>`. Consumers inject the closed
  handler interface directly.
- **DI validation is always on (#572).** Every composition root enables `ValidateScopes` +
  `ValidateOnBuild` in ALL environments (`builder.Host.UseDefaultServiceProvider` on the three web
  hosts; `ServiceProviderOptions` in the CLI `TypeRegistrar`); a new host copies the pattern. It
  validates registered services only, so endpoint integration tests stay the guard for a forgotten
  closed handler registration. **In the CLI, Spectre command types are registered `AddScoped` and
  resolved through a single process-lifetime scope in `TypeResolver`**, because they inject scoped
  handlers (`AddSingleton` + a root-provider resolve is a captive dependency that refuses to
  build). A new CLI command follows suit. No CI covers that graph (the CLI is
  `net10.0-windows/win-x86`), so changes to it need a Windows `export`/`patch`/`launch` smoke.
- **CQRS read/write split, day 1 (ADR-0002 amendment).** Query handlers read POCO `ReadModels`
  through `IApplicationReadDbContext`; command handlers load and mutate aggregates via
  repositories + `IUnitOfWork`. The write model never serves list/search queries; every new
  aggregate ships with its read model + EF configuration in the same change.
- **TMS ships with auth from day 1.** Endpoints are authorized by default (public ones are
  explicit), and edited rows carry user attribution: `Translation.SubmittedById` (stamped on
  upsert) and `ApprovedById` (on approve), persisted via the `IdentityId` converter in
  `TranslationSystem.Persistence`.
- **Who checks what: the standard OAuth roles (ADR-0067, #1098).** The website is the client, the
  sign-in server issues the tokens, and the translation API and the account API are resource
  servers. The website checks only what OpenID Connect asks of a client, and the OIDC handler does
  that. The access token is opaque to the website: no check of its shape, signature, size, `exp`
  claim or characters, because the translation API checks every token it receives. A renewal
  answer the website cannot use ends the session cleanly, or counts as temporary (429, 5xx, a
  network error, a timeout). Guards on where our secrets go stay; checks on whether our own server
  told the truth go. Built-in OpenIddict and ASP.NET Core behaviour comes first, and own protocol
  code carries a written reason. **A review finding that asks for a new check of what one of our
  own parts sends, for an answer that part never sends by design (a token of the wrong shape from
  our sign-in server, our own settings already checked at startup), is not a defect of the part
  that receives it.** Sort it as already decided, with a pointer to ADR-0067 §8. If a real path
  makes our part send that answer, file it against that part, with the path. Always still findings
  (ADR-0067 §7): our own part being down, slow or failing (a 5xx, a timeout, a failed save); the
  one clean path for an answer the code cannot use; a guard on where a secret goes (another host,
  plain HTTP, a page, an e-mail, a log line); a resource server's check of a token it receives; and
  input from outside our trust boundary (browser, user, imported files, DAT bytes, the CLI and its
  settings, third-party services). Least privilege between our parts (#1086) is a separate, valid
  question. This ends the chain #974 → … → #1067: eight tickets in thirteen days, each adding a
  check the next review audited.
- **Hypermedia links are authorization-aware, not role-branched (ADR-0040).** `ILinkFactory` replays
  the *target* endpoint's own policy (`IAllowAnonymous` → `AuthorizationPolicy.CombineAsync` incl.
  the fallback → `IAuthorizationService`) before it emits a link, so a rel is never advertised to a
  caller who would get 401/403 following it. Never restate a role rule inside a link factory: a
  link factory encodes *state* rules only (removed, `Draft`/`NeedsReview`, `Unprocessed`). The TMS
  service document (`GET /`) is the one deliberate `AllowAnonymous` hole in authorized-by-default:
  it advertises only what the caller may already reach, and it lets the CLI (and M4) boot without
  hardcoded paths. Only parameterless entry points belong in it; id-keyed affordances (`approve`,
  `delete`, `import`) live on the representation that carries the id.
- **No API gateway; the discovery document IS the client contract surface (ADR-0041).** Caddy owns
  transport, each API's discovery root owns semantics, the SSR frontend owns aggregation; nothing
  sits in the request path between them. So **rel names are a frozen public contract**: additions
  are cheap, renames break every client. A client takes one root URL per service as config and
  resolves everything else by rel (#610 frontend, #611 CLI). A service split does **not** justify a
  gateway: the departing service hosts its own root, and the one it left links to it with one
  configured absolute URI. Reopening triggers are listed in the ADR.
- **Validation:** FluentValidation **for commands only**: the command handler injects
  `IValidator<TCommand>` and maps failures to `Result` (never throws). Queries validate inline in
  their handler. Every validator must be registered in DI.
- **Handlers are orchestrators.** Business logic lives in domain/application services; handlers
  validate, delegate, return.
- **No primitive obsession in the domain layer.** Every domain concept that carries a constraint
  or identity is a `ValueObject`, never a raw `string`, `int`, `Guid`, etc. Golden templates:
  `TranslationSystem.Domain/Aggregates/GameVersionAggregate/ValueObjects/LotroNotationVersion.cs`
  (constrained-string VO) and
  `TranslationSystem.Domain/Aggregates/GameVersionAggregate/Entities/GameVersion.cs` (an aggregate
  that models its constrained version concept as a VO; its timestamp and enum stay primitive,
  because they carry no extra invariant).
- **EF Core (`TranslationSystem.Persistence`):** Fluent API only (never attributes), `nameof()` for
  column names, `MaxLength`/`Precision`+`Scale` over `HasColumnType`, no needless `IsRequired()`
  (value types & non-null strings are already required), FK property names parametrized with
  `nameof()`.
- **VO persistence mapping: `ComplexProperty` by default, `OwnsOne` when an index is needed.**
  `ComplexProperty` is the right mapping for a VO (pure value, no identity tracking), but it cannot
  be indexed in EF Core 10 (fixed in EF Core 11). With `OwnsOne`, define `HasColumnName` explicitly
  and put `HasIndex` inside the owned builder.
- **Migrations are forward-only and N-1 backward-compatible (ADR-0023).** The deploy gate commits
  the schema **before** traffic moves, and rollback reverts code, never schema, so the running app
  revision must survive every migration. Never rely on `Down()` outside local dev; recovery is
  roll-forward or a Neon restore (PITR or the MIGR-04 pre-migration snapshot branch; runbook).
  Destructive operations (drop/rename a column or table, change a type, add `NOT NULL`, tighten a
  constraint / unique index over existing data) ship as **expand → backfill → contract across ≥ 2
  deploys**; a deliberate destructive step carries an in-file
  `MIGRATION-SAFETY: acknowledged — <reason>` comment (CI gate: #338 / MIGR-03). Migration-touching
  PRs also run the N-1 proof: the previous release's integration suites against the HEAD schema
  (`n1-compat.yml` / `scripts/n1-compat.sh`, ADR-0024; the factories' seam is
  `N1_COMPAT_SCHEMA_SCRIPTS_DIR`, inert in normal test runs).
- **Right-size the design: YAGNI by default.** Before proposing an abstraction, cache, config knob,
  queue, or new infra, check it solves a **real, present** need from the current spec/ticket. Pick
  the simple path and note the trade-off in one line. **YAGNI prices both sides (#1115).** A
  convention every writer must remember is a cost too, and so is a guard or a test that polices it.
  When a ticket asks for a guard around a hand-kept convention, put the mechanism that would remove
  the convention next to it, with rough sizes, and pick the smaller and more readable one (PR #952's
  IL scanner was bigger than the interceptor it stood in for, and the owner closed it; ADR-0038 §6
  amendment).
- **Agent fan-out is budgeted; agents inherit the session's model and effort.** The repo pins no
  model tier of its own. Hard cap: **max 4 subagents in parallel**, no chained waves by default; a
  small diff gets reviewed **inline, zero agents**. The committed `code-reviewer` agent carries
  **`model: inherit`**, so a fresh clone reviews with the contributor's own tier; `/code-review`
  and `/security-review` follow the session model/effort the same way. Loop-mode worker sessions
  take model and effort from the maintainer's machine-local policy (Opus 5.5 at **xhigh** since
  2026-09-22; without it the scripts fall back to **effort high**, the `LOOP_EFFORT` default),
  unless the prompt for that run says otherwise. This applies to interactive sessions and loop
  workers alike. The values live in agent frontmatter and in the
  loop scripts' defaults; if this prose and the frontmatter disagree, the frontmatter wins.
- **Token discipline.** About half the spend is cache reads (context length × turn count), and this
  repo boots the largest context of the owner's projects, so the levers are fewer turns and shorter
  contexts. Batch independent tool calls into one message; chain dependent shell steps with `&&`
  when only the final result gates (build + the whole suite = one call). One ticket = one session:
  `/clear` after the PR, because a second ticket in the same context pays for the first one's whole
  conversation on every turn. Questions to the user belong in `/ticket`'s step-2 gate and nowhere later:
  the prompt cache dies after 1h idle, so deliver on documented assumptions and fix from the Ticket
  report instead. Auto-compaction stays at its ~967k default on purpose: never set
  `autoCompactWindow`, `CLAUDE_CODE_AUTO_COMPACT_WINDOW` or `CLAUDE_CODE_DISABLE_1M_CONTEXT`. Past
  ~500k in `/context`, finish, report, `/clear`. Pick model and effort before the first prompt:
  `/model` mid-session is a full cache miss on every model, and `/effort` too on every model except
  Fable 5.1 (assume a miss on Opus 5.5). Back-to-back sessions reuse only the tool layer of the
  cache, so batch them for focus, not for cache.
- **Frontend is Static SSR, enforced.** No WebAssembly, no SignalR circuit, no per-user server
  state; forms post via `<form method="post" @formname @onsubmit>` (the SSR `@onsubmit` special
  case) or `<EditForm OnValidSubmit>`, never interactive `@on*` handlers, `@rendermode`,
  `StateHasChanged`, or `AddInteractive*`. **No inline `<script>` either (#670):** the Frontend's
  CSP sends `script-src 'self'`, so an inline script is dead and only the browser console says so.
  Put the code in a file under `wwwroot` and load it with `src=`; if one truly must be inline, add a
  nonce in `SecurityHeadersMiddleware`, never `'unsafe-inline'`. That is also why `App.razor` has
  no import-map component. `scripts/check-ssr-purity.sh` (+ `.ps1` twin) gates all of it in **both**
  `pr-verify` and `ci`, and its self-test proves the guard still fires; smoke leg 2 fails if a
  deployed page serves an inline script its own CSP blocks. Real interactivity or an inline script
  is an ADR-first architecture change. A page that draws a named form only after its data loaded
  puts `<PostedFormTarget>` in its load-failure branch (#1122).
- **Never touch the request inside a `HybridCache` factory (#825).** For a token that can be
  cancelled, `HybridCache` runs the factory on the thread pool without the request's context, so
  `IHttpContextAccessor.HttpContext` is `null` there: a typed client sends no bearer and no caller
  headers (ADR-0054), and nothing fails loudly. Read the cache with `DisableUnderlyingData`, make
  the call in the request on a miss, and `SetAsync` only a good answer (`DiscoveryCache` is the
  template). A factory may compute only from its `state` argument: even when it runs inline, every
  caller that joins it gets the first caller's answer.
- **Auth Razor Pages: never put a caller-supplied value in an `asp-route-*` attribute (#681,
  #682).** Razor writes a tag-helper attribute through `WriteLiteral`, the page-output sink CodeQL's
  `cs/web/xss` recognises, so `asp-route-returnUrl="@Model.ReturnUrl"` raised two high alerts that
  blocked every merge. It was never exploitable, but that does not clear an alert, and neither does
  a sanitizer that returns its input. Use a plain attribute:
  `<input type="hidden" name="returnUrl" value="@Model.ReturnUrl" />` is HTML-encoded and not a
  sink. A `<form>` with no `asp-*` attribute still emits its antiforgery token and posts to the
  current URL, and handler binding reads the form field before the query string.
  `LocalReturnUrl.Sanitize` only answers "is this target local"; the encoding at each print site
  keeps these pages safe. (`Html.Raw` is a sink of its own.)
- **The auth origin sends its own CSP, so an auth page has no inline `<style>` or script (#693,
  #833).** `AuthSystem.API/Middleware/SecurityHeadersMiddleware` adds CSP, `X-Frame-Options: DENY`,
  nosniff and `Referrer-Policy: no-referrer` outside Development. `style-src` and `script-src` are
  plain `'self'`, with no nonce. The account pages share `wwwroot/account.css`, and a rule only one
  page needs goes at its end under that page's body class (`page-login`, `page-register`,
  `page-privacy`); the error pages that `BrowserErrorPage` writes use `wwwroot/error-page.css`. Page
  script goes in a file under `wwwroot` too (`login.js`). An inline `<style>` is dropped and the page
  loads unstyled. `form-action` lists the web client's origins, because the login POST ends in a
  redirect to the frontend's `/callback` and Chrome checks every redirect of a form. That makes the
  frontend's `ResponseMode.Query` load-bearing: OpenIddict's form_post page uses an inline script
  this CSP blocks. Guards: `check-ssr-purity` scans `src/AuthSystem/**/*.cshtml` (inline script and
  event handlers, any `<style>`, a `style=` attribute), smoke leg 6, the integration suite on every
  Razor page endpoint, and the Frontend E2E suite (auth runs in Testing there, with the CSP on)
  fails on any `securitypolicyviolation` or a stylesheet that did not load.
- **Auth Razor Pages are rate limited by default; opting out is the explicit act (#692).**
  `app.MapRazorPages().RequireRateLimitingByDefault("auth-page-limit")` covers every account page,
  so a new page needs no attribute to be covered. A page that needs a different budget says
  `[EnableRateLimiting]`, one that needs none says `[DisableRateLimiting]`, and the convention
  leaves both alone. **Never replace it with a plain `RequireRateLimiting` on that group:**
  `GetMetadata` returns the last match, and a group convention is appended *after* a PageModel
  attribute, so the group would silently eat the page's own policy (on a minimal-API endpoint the
  winner is the other way round). A page budget counts **POSTs only**: a Razor Page is one endpoint
  for GET and POST, so a send-sized budget would be spent on page views (ADR-0046's lesson, also in
  `forgot-password-limit`).
  - **Mail sent to a caller-typed address gets a second budget keyed by the recipient's inbox**, not
    the IP, because an attacker rotating IPs gets a fresh IP budget every time (ADR-0055;
    registration, password reset, resend confirmation and e-mail change each have one). The key is a
    **`MailboxKey`** (ADR-0057, #835): it folds a `+tag`, Gmail's dots and googlemail.com, since
    those spellings reach one inbox. Build it from Identity's normalized form, never from the typed
    text: `ApplicationUser.NormalizedEmail` where an account exists, `UserManager.NormalizeEmail` in
    the handler where none does (the normalizer is scoped).
  - **The one exception is a confirmed account's password reset, which keys on the account id:**
    only the inbox owner can confirm an account, so every permit a stranger spends mails her a
    working link, and an inbox key would let a stranger's unconfirmed `anna+x@` account use up her
    recovery budget.
  - Take the permit as the last check before the send, never in the dispatch leg.
- **An anonymous answer that hides whether an address has an account waits for a time floor
  (ADR-0059, #840).** Login's general-failure answers, forgot-password, reset-password,
  confirm-email and cancel-deletion (500 ms), registration (1 s, #860) and resend-confirmation
  (3 s), page and API alike, hand the work from the address lookup onward to
  `IResponseTimeFloor.HoldAsync`, which starts the clock before the work and waits on every exit, a
  throw included. Every path after the lookup still verifies exactly one password hash (a dummy one
  where there is no real one) as the second layer. "Make every branch do the same work" failed three
  times (#314, ADR-0056 §4, #840): a database write or a live mail has no cheap twin. A new
  anonymous page or endpoint that looks an account up by a typed address and hides the result joins
  the list, with a test in `ResponseTimeFloorEndpointTests`, in the same change. The integration
  host runs a no-op floor, so only those tests see the real one.
- **A per-address rate limit the frontend can reach is keyed on the visitor, never on the frontend
  container (ADR-0054, #819, #823).** Every frontend→API call arrives through Caddy from the
  frontend container, so `Connection.RemoteIpAddress` there is one address for every user.
  - The TMS API's `fixed-by-ip` and the three auth policies that traffic reaches (`fixed-by-ip`,
    `auth-endpoint-limit`, `change-email-limit`) take their key from each API's own copy of
    `RateLimitPartitionKeyResolver`: the visitor's address the frontend forwards in
    `X-LOTRO-Client-Address`, honoured only next to the per-environment `X-LOTRO-Frontend-Key`
    (SHA-256 digests, constant-time compare, exactly one value each, the address must parse);
    otherwise the connection's own address.
  - Every address becomes a key through that class's `KeyFor`: an IPv6 address counts as its /64
    and `::ffff:a.b.c.d` as plain IPv4 (#831). A new per-address policy keys through it too, never
    through `RemoteIpAddress.ToString()`.
  - The browser-facing page policies and `register-limit` stay on the connection's address on
    purpose: the frontend never posts to a Razor page nor to `auth/register`, and the key must never
    become a bypass for the login brake or a fresh mail budget per invented address.
  - A new outgoing path from the frontend to either API carries `FrontendCallerDelegatingHandler`
    with that API's key, outside any resilience handler, so a retry never repeats a header. The
    header names live once, in `Hateoas.Abstractions`.
  - The key is one `FRONTEND_CALLER_KEY` per box `.env`, read by all three services. Compose
    refuses to render without it and every app refuses to boot without it outside
    Development/Testing, so it lands on the staging box before a change that needs it merges.
- **Every user-visible date is Poland time, never raw UTC (#736).** Static SSR has no reader time
  zone: the container's zone is UTC and the request carries none. A stored instant is converted to
  `Europe/Warsaw` when it is printed (page, e-mail or download file name) through the context's own
  extension (`Frontend.Infrastructure.Formatting.DateTimeOffsetExtensions`,
  `AuthSystem.API.Extensions.DateTimeOffsetExtensions`). A timestamp that stands alone carries the
  words `czasu polskiego`; a sentence that already names the zone uses the unlabelled overload. UTC
  stays the wire and storage format, and the runtime images ship `tzdata`.
- **No client hardcodes an API path, enforced (#610 frontend, #611 CLI).** There is no gateway
  (ADR-0041), so every entry point is resolved by **rel name** (the Frontend through
  `IDiscoveryCache.ResolveTranslationSystemHrefAsync(Rels.<Name>)`, the CLI through
  `ITranslationFileEndpointResolver`), and every id-keyed action follows the href the loaded
  resource advertises. A missing rel is a failure (`ProblemDetails` in the Frontend, a `Result`
  error in the CLI), never a locally composed path: an absent rel means the server does not offer
  that affordance to this caller.
  - An href off the client's configured origin is refused: by the CLI's resolver, and in the
    frontend by `SameOriginDelegatingHandler`, the first handler on the TMS and auth typed clients
    (#830) and on the token client, whose sign-out revoke follows the discovery document's
    `revocation_endpoint` (#964). No frontend client to either API follows a redirect, so no bearer
    token, refresh token or caller key leaves for another host. These guards decide where our
    secrets go (ADR-0067 decision 4); they never judge what our own server answered.
  - Every client `IHttpClientFactory` builds in the frontend starts on `CreatePrimaryHandler`
    (`AddHttpClientDefaults`, #922), so a new client needs no setting of its own. A client given its
    own `SocketsHttpHandler` or `HttpClientHandler` keeps it, with redirects and cookies switched off.
    The OIDC back-channel (#899) starts on `CreatePrimaryHandler` too; a back-channel handler that
    other code set first is kept, and the standard transport at its end gets the same switch (a
    test's stand-in server is left as it is). `CreatePrimaryHandler` keeps no cookies (#924): one
    handler serves every visitor.
  - `scripts/check-client-hypermedia.sh` (+ `.ps1` twin) flags an API path in any string literal
    under `src/Frontend/` **and** `src/Patcher/`, in **both** `pr-verify` and `ci`, right after its
    self-test; prose in comments stays allowed. The one bounded exception is the editor's detail URI
    (`{discovered translations href}/{id}`): the `/editor/{id}` route hands over an id, not a link,
    as `TranslationEditorLoader` documents.
- **The CLI resolves its download URL from discovery, and degrades without guessing (#611).**
  `SyncTranslationFileCommand` takes one input, `TmsBaseUrl`; everything else comes from
  `GET {baseUrl}/` with the HATEOAS vendor `Accept` (a plain-JSON request gets a link-less
  document).
  - Discovery is the primary path. The `.endpoint` sidecar next to `polish.txt`/`.etag` is the
    **outage** fallback only: a server that answers but does not advertise `translation-file`, or
    advertises an href off the configured origin, is a refusal, not an outage (no fallback, no
    composed path). A resolved href is re-validated (absolute, https except loopback, same origin as
    the base URL) whether it came from the wire or from disk.
  - The launch must never block on the network (spec 0001 Q5), so an unresolvable endpoint reports
    `EndpointUnresolvedUsedCache` and the launch continues on the local file; with no local file the
    launch path reports it and exits 2. A failed `.endpoint` write is a logged warning (the next run
    re-resolves it); a failed `Save` of the downloaded file stays fatal.
  - The TMS adapters use their own keyed `HttpClient` with **`AllowAutoRedirect = false`**: a 302
    could carry the request off the checked origin, and the redirect target would serve both the
    body and the ETag that hashes it. The forum fetcher keeps redirects: it targets a third-party
    site.
  - **The patcher's one allowed shared reference is `Utilities/LotroKoniecDev.Hateoas.Abstractions`**
    (`MediaTypes.HateoasJson`, and `LinkDto` in tests only), so the vendor media type cannot drift
    between the two ends. The CLI still re-types the link envelope rather than linking
    `TranslationSystem.Contracts`, with a parity test standing in for the compiler.
    `BoundedContextIsolationTests` covers patcher Primitives/Domain/Application only: Infrastructure
    and Cli are `net10.0-windows`, so nothing mechanically blocks the next reference added there.
- **Docker restore layers are loud and gated (ADR-0028, amended).** Every Dockerfile that lists
  `.csproj` files must COPY the **full transitive closure** of the projects it restores, because
  `dotnet restore` skips a missing project file and still **exits 0**. Two defences: image builds
  run `dotnet build`/`publish` with **`--no-restore`**, which turns the gap into `NETSDK1004`; and
  `scripts/check-dockerfile-restore-graph.sh` (+ `.ps1` twin) runs in **both** `pr-verify` and
  `ci` (the image job fires only on Dockerfile/workflow edits, never on the project-graph changes
  that stale a COPY list). A new project must join every Dockerfile whose restore graph reaches it.
  - Every `scripts/check-*` guard exits 2, never passes, on a missing folder, a folder with nothing
    to scan, or a file it cannot read (#1079), and its self-test runs first in both workflows; a new
    guard copies that. The one deliberate pass on an empty input is `check-migration-safety` on a
    diff that adds no migration.
  - Every bash guard runs `export LC_ALL=C` (#1162): on the Linux runner GNU grep hides a matching
    line that is not valid UTF-8. That makes `[[:space:]]` ASCII only, so a pattern for a space that
    C#, Razor or Docker reads uses `[^!-~]` (any byte that is not visible ASCII). In a pattern that
    flags something, its extra matches cost only a false alarm; in one that allows a line or ends a
    scan, they can hide one (`void ÜDown(` ended the scan of `Up()`), so keep `[[:space:]]` there
    unless nothing valid can stand in that place. macOS grep and sed act differently, so prove a
    guard change on Linux with `LANG=C.UTF-8`.
  - **The Blazor exception (#414):** the frontend image's `dotnet build` runs **without**
    `--no-restore`, because `blazor.web.js` comes from a package the SDK references only once it
    sees `.razor` files, never in the `.csproj`-only restore; with the flag every `_framework/*`
    asset 404s at runtime. Never add the flag back to that `dotnet build`; `publish` keeps it.
- **Required PR checks always report, and decide inside themselves what to run (#791).** The
  `main` ruleset requires "Pull Request Verification", and GitHub counts a **skipped** job as
  passed. So never path-filter a required check (`on.paths` / `paths-ignore`): it never reports and
  the PR deadlocks (#285). Never put it behind a helper job either: when a job in its `needs:` fails
  or never starts, the required job is skipped and the PR merges with no build and no tests. The
  required job has no `needs:` and no job-level `if:`; it classifies the diff in its own first
  steps, and every later step has no `if:` or exactly `steps.diff.outputs.<verdict> != 'false'`, so
  a missing or garbled verdict runs the gate. A non-required job may take its verdict from a small
  classify job of its own; the required job never waits on one. Exactly one job in all workflows
  carries the required name (a second, skipped one would report the check as passed).
  `scripts/tests/pr-verify-required-check.tests.sh` pins all of this
  (and the `gitleaks` job's start checks), runs in pr-verify, `actionlint.yml` and `ci.yml`, and a
  new required check joins it.
- **CI runs what the diff can break, nothing more (`scripts/ci/classify-changes.sh`).** Every PR
  gets three **independent** verdicts: `code` (restore + Release build + unit + integration tests),
  `guards` (the cheap bash gates CI executes: SSR purity, client hypermedia, Dockerfile restore
  graph, migration safety, loop provenance) and `images`. Keep them independent: a change to a
  script CI merely runs must not pay for the .NET gate. The failure that matters is the other
  direction: a **build input** (`.editorconfig`, `Directory.*.props`, `global.json`, a fixture, a
  script a test reads: #1163) in the inert list buys a silent false green, so `scripts/tests/classify-changes.tests.sh` pins both
  directions and runs **unconditionally** in the required job before any verdict is trusted.
  `ci.yml` (main) does **not** self-skip its .NET steps, because CD triggers on CI success and
  "CI was green" must mean the build and both suites ran; it filters cheap content with
  `paths-ignore` instead (no CI ⇒ no CD).
- **`GET / -> 200` never proves the Blazor frontend works.** A `[StreamRendering]` page returns 200
  with its spinner frame before it fetches anything. The sign of a healthy image is the
  **fingerprint**: `@Assets[]` renders `_framework/blazor.web.<hash>.js` only when
  `MapStaticAssets` resolved its manifest. `scripts/smoke.{sh,ps1}` leg 2 asserts that, and CD
  smokes the 0%-traffic candidate before any traffic shift.
- **Git is rebase-based, and branches are never deleted.** Bring a feature branch up to date with
  `git rebase main`, never `git merge main`; no merge commits in feature branches, from any tool
  (remote `main` is squash-only). `/merge-train` updates a branch with GitHub's rebase, never falls
  back to a merge, and never merges a branch that holds a merge commit. An approved branch in
  conflict, or one with a merge commit, is the train's own job: it rebases locally, fixes the
  conflict, runs the full gate and pushes with `--force-with-lease`. A rebase keeps the owner's
  approval when its result is exactly the approved code on top of `main`, and so does the train's
  own recorded conflict fix (ADR-0060, amended 2026-09-28 and 2026-10-05). After a squash merge,
  **keep both the local and the remote branch**: plain `gh pr merge --squash` (never
  `--delete-branch`), and never `git branch -d/-D` or `git push origin --delete`.

## Code style (C#) — repo-authoritative

- **Sealed** all types unless there is explicit inheritance.
- **Explicit constructors** in classes; no primary constructors for a `class` (records are fine).
- `var` **only for anonymous types**; explicit types everywhere else.
- **LINQ methods**, never query syntax. **Pattern matching**, except inside a query expression.
- **File-scoped namespaces**, **Allman braces**, no `#region`.
- **Documentation uses `/// <summary>` XML doc comments**, never plain `//` comments, to document a
  type or member. Omit it when the name already explains the intent; keep plain `//` for the
  non-obvious *why* inline in logic.
- **Comments are written in simple English, level B1-B2 (#675).** Short sentences, common words,
  one idea per sentence. No chains of em-dashes, no stacked clauses, no vocabulary a non-native
  reader has to look up. A comment nobody can read on the first pass is a defect, even when every
  word in it is true.
- **A comment explains the *why*, never the *what*.** If the name and the code already say it,
  write nothing: `MarkAsProcessed` does not need "Marks the version as processed". Keep the reason
  and the pointer (`spec 0001`, `ADR-0047`, `#624`); leave the full story to the ADR or the spec,
  and never repeat the same explanation in a doc comment, an inline comment and a document at once.
- **Comments stay short and carry no history (2026-10-23).** As a rough guide, an inline why is
  one or two lines, a member doc up to three sentences, a type doc up to about five. No
  `<summary>` or `<param>` that repeats a name, and no test `<summary>` that repeats the test
  name. No story of how the code got here ("used to", "before #123", "the first attempt"): git,
  the PR and the ADR keep it, and a ticket number stays only as a pointer to the reason. A comment
  that no longer matches the code is a defect, fixed in the same change. The 2026-10-23 sweep cut
  about a third of all comment lines because these rules had drifted.
- Code & identifiers in **English**.
- **Log lines use the `[LoggerMessage]` source generator (#1143).** Each one is a
  `private static partial void Log…(ILogger logger, …)` method on a `partial` type, and it sits
  with the private helper methods. Never write a `LoggerMessage.Define` delegate in a static field:
  CA1848 accepts both styles, so the build does not stop it (#1062 copied the old style from the
  file around it).
- **Member order: one order for every type, tests included** (golden template:
  `TranslationSystem.Domain/Aggregates/GameVersionAggregate/Entities/GameVersion.cs`). Top to
  bottom: (1) constants, right under the type declaration, (2) fields, (3) properties,
  (4) public/internal methods, (5) the `static` factory method(s) (`Create`), when the type has
  one, (6) constructors (in a domain class: the domain ctor, then the parameterless EF ctor),
  (7) private helper methods. A constant, field or property never sits below a constructor or a
  method. Handlers, services, validators, page models and test classes follow the same order, so
  adding a factory later never moves the constructors. Older code breaks this in many files and is
  not swept (owner decision, 2026-10-09), but no new case is added: a member you add goes in its
  right place even when its neighbours are wrong, and if it joins a group in the wrong place, move
  the whole group. Never copy a wrong placement from the code around it (#1055 did).

## Anatomy of a feature slice

### Patcher slice (stable — the reference shape; bugfixes & deliberate refactors)

`Application/Features/<Area>/`: `<Action>Command.cs` (sealed record `: ICommand<Result<T>>`) +
`<Action>CommandHandler.cs` (internal sealed, explicit ctor DI) + validator (commands only) +
response record. Wired in `ApplicationDependencyInjection`; the CLI injects the closed interface
and maps failures via `ErrorMapper.MapErrorToExitCode`. Canonical examples: `Features/Patching/`,
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

Wire the rest, **all three steps, every time**: (1) explicit DI registration of the closed handler
interface, (2) request/response DTOs in `TranslationSystem.Contracts`, (3) tests: domain/handler
unit tests + an endpoint integration test against real PostgreSQL. There are no pipeline
behaviours: validation and logging (`ILogger<Handler>`) live inside the handler. **Mirror the
nearest existing sibling slice** rather than inventing structure.

## Testing philosophy — repo-authoritative

Per-suite details (inventory, libraries, snapshots, E2E, conventions): `tests/CLAUDE.md`.

- **Black box over the public seam, never the implementation.** Assert observable behaviour:
  inputs in → `Result`/persisted state out. NSubstitute stubs **genuine boundaries**
  (`IDatFileHandler`, `IForumPageFetcher`), never internals you own.
- **`.Received()` policy:** only for side effects invisible in the return value (resource cleanup,
  "destructive op was NOT called on validation failure"). If the return value already proves it,
  `.Received()` is forbidden: a behaviour-preserving refactor must never break a test.
- **Unit tests are pure:** no filesystem, no network, no DB, no order dependence. Real-resource
  verification belongs to integration projects.
- **The whole suite green locally before every push, unconditionally.** `dotnet test`, no filter:
  everything runnable on this OS, and it exits 0 when all of that passes. Off Windows the two
  Windows-only patcher suites (`.Tests.Infrastructure`, `.Tests.E2E`) are compiled but not run, and
  each prints one "not run" line (#816). Never narrow it to the touched area, and never run integration only "when
  the slice ships an endpoint": deciding from the diff which suites a change can reach is the
  mistake, not the shortcut. `pr-verify` runs unit **and** integration on every PR, so a leg you
  skip locally fails on a runner minutes later (in the sibling repo a CSS-only fix broke four
  snapshot tests nobody thought it could reach). A suite that cannot run here is unproven: say
  which, and do not open the PR.
- **Edge cases are first-class.** The happy path is the floor. `[Theory]` + `[InlineData]` for the
  unhappy-path/boundary matrix (empty, max, malformed, already-in-state).
- **AAA always; assertions inline in the test method.** DRY the Arrange (builders), never the
  Assert. One reason to fail per test.
- **Tooling: xUnit + Shouldly + NSubstitute** for the framework, assertions and mocks. Naming:
  `MethodName_Scenario_ExpectedResult`. Other tools only for their own job, each listed with where
  it is used in `tests/CLAUDE.md` → "Framework & Libraries" (for example NetArchTest.Rules for
  architecture rules, Verify.Xunit for snapshots, bUnit for components, FakeTimeProvider for timers
  no substitute can fire, Testcontainers and Playwright for real databases and the E2E stacks).
- **Snapshots pin shape; they never replace an assert (#571).** Golden fixtures own the `||` file
  contract on both sides (a snapshot must not replace them), plain asserts own behaviour across
  many inputs, and a Verify snapshot owns "did anything about this large payload change" (TMS API
  response bodies, Blazor SSR markup). Deleting an assert because "the snapshot covers it" is
  wrong. `*.verified.*` files are committed and ARE the pinned contract; `*.received.*` is
  git-ignored scratch; **re-accepting a verified file is a deliberate, reviewed act**: read the
  diff, then land it in the same PR as the change that caused it. One shared scrubber set
  (`tests/Shared/VerifyModuleInitializer.cs`) keeps runs deterministic; a snapshot that churns is
  worse than none.
- **The structural house rules are a TEST, not review memory.**
  `tests/LotroKoniecDev.Architecture.Tests.Unit` enforces the patcher dependency rule, no-mediator
  (ADR-0001), patcher/TMS isolation, the Frontend's contracts-only reach, the persistence direction,
  the CQRS split and the sealed / `internal`-handler / commands-only-validator conventions over
  assembly IL, in the normal unit gate. **A new production project must join
  `ProductionAssemblies.All`** or it escapes every rule (a self-test fails until it does). Changing
  a rule is an architecture decision: fix the code, or write the ADR first; never weaken the test to
  green.
- Platform honesty: tests must pass on macOS, Linux AND Windows: `Path.Combine`, never a hardcoded
  `C:\`.
- **TMS test projects follow one naming scheme**, all under `tests/LotroKoniecDev.<name>`. Unit
  (pure): `TranslationSystem.Domain.Tests.Unit`, `TranslationSystem.API.Tests.Unit`,
  `AuthSystem.API.Tests.Unit`, `SharedKernel.Tests.Unit`, `Logging.Tests.Unit`,
  `Frontend.Tests.Unit`. Integration (real PostgreSQL or a booted host, never in a Unit project):
  `TranslationSystem.API.Tests.Integration`, `AuthSystem.API.Tests.Integration`,
  `Frontend.Tests.Integration` (the Frontend host in memory, no database; #915). Browser/E2E
  (Testcontainers + Playwright, ADR-0009; Docker-required, off the PR gate by name):
  `TranslationSystem.E2E.Tests`, `Frontend.E2E.Tests`. The patcher test projects (`Tests.Unit`,
  `Tests.Infrastructure`, `Tests.E2E`) stay as they are.

## Workflow (the loop that compounds)

1. **Ticket before code.** Work flows from GitHub issues (`M{milestone}-{nn}: Title`, one
   `priority-*` + one `type-*` + any `area-*`; full taxonomy in **`docs/labels.md`**). Run
   **`/ticket <n>`**.
2. **Spec before code.** Anything non-trivial gets `docs/specs/NNNN-*.md` (via `/ticket` or
   `/spec`). Open questions are **extracted for the user, never invented**. Implementation starts
   only at **Status: Agreed**.
3. **Decision before code.** A non-trivial modeling/architecture choice → **`/adr`** first.
4. **Slice, mirror, test, review.** Branch via `gh issue develop <n> --checkout`. Implement by
   mirroring the nearest sibling slice, add tests, then run the **`code-reviewer`** agent on the
   diff (**`/security-review`** for anything touching native interop, file protection, or auth).
   **Green build + zero warnings + clean review = "done", not before.**
5. **PR closes the ticket.** Title mirrors the ticket; body contains `Closes #<n>`. Push and open
   the PR without asking (the ticket is the authorization), as the last step, only when every gate
   is green (`/ticket` step 8); **merging is the one step that always needs a separate explicit
   ask.** **No merge with open CodeQL alerts:** the CodeQL check succeeds even when it uploads
   findings, so before any merge (interactive or loop) list
   `gh api "repos/{owner}/{repo}/code-scanning/alerts?ref=refs/pull/<n>/merge&state=open"` and fix
   every alert (dismiss only with a stated reason); `/merge-train` enforces this.
6. **Feed the flywheel.** A reusable correction is persisted: a global rule → **this file**; a real
   decision → a new ADR; an empirical DAT/update finding → `docs/knowledge-base/` (dated). The same
   mistake made twice means a rule is missing.
   - **An owner ruling that reaches past its own ticket goes into an ADR or this file in the same
     session (2026-10-11).** A ruling that lives only in an issue comment says nothing about how far
     it reaches, so the next session stretches it or misses it (PR #1068, #1023 vs #966, PR #952).
   - **"The owner decides" in a ticket, unless a later owner comment answers it, is a question,
     never a default:** ask it at the `/ticket` step-2 gate, or BLOCK a headless run, and never pick
     the strictest option because "the owner ruled no path out" (PR #1063).
   - **Before you follow an acceptance criterion that sets visible behaviour** (a status code, a
     sign-out, a log level, a limit), search the owner's rulings on the same flow:
     `gh issue list --state all --search "\"Owner decision\" <flow words> in:comments"`. A conflict
     is CLARIFY, never a silent winner.
   - A lesson too narrow for any of those goes to `.claude/agent-memory/<agent>/`, which is
     **gitignored** (this repo is public). Treat it as a scratchpad: a lesson worth keeping is worth
     promoting to a shared, checkable place from the list above.

### Manual QA — external testers execute what you write, literally

Manual QA runs on deployed **staging**, browser-only, by external testers with no repo access, no
Docker, no CLI, and no way to tell whether a ticket is still true. They pair with a general-purpose
LLM that has none of this repo's context, so a wrong scenario line is not questioned: it is
executed and reported as a bug. **Write QA tickets with `/qa-ticket`; re-baseline an existing one
with `/qa-ticket #<n>` before handing it over.**

- **An unverified scenario is a defect.** Every line must be backed by something you actually read in
  `src/` (the `.AllowAnonymous()`, the validator rule, the razor condition, the literal message
  string) or by a live query against staging. Of the first QA batch's 8 bug reports only 2 were
  valid: the rest traced to stale, unexecutable or code-contradicted ticket lines, or to lines that
  invited browser-side fault injection (#546, #547, #602–#604). Nothing re-checks a ticket after
  the product moves (#271 claimed the `polish.txt` download was auth-gated for a month after #309
  made it public).
- **Classify every scenario:** plain (browser-only), _(owner-assisted — SKIP unless the owner runs
  it)_, or **blocked: what is missing**. Never "optional": optional invites improvisation. DevTools
  "Offline" is not API downtime: it cuts browser→frontend, and nothing server-rendered can render.
- **Every QA ticket carries a pointer to the wiki, never a copy of its rules (wiki verdict,
  2026-08-26).** The ticket opens with one line telling the tester to paste `Workflow-testera.md`
  into their assistant first; the wording lives in `.claude/commands/qa-ticket.md` §3, and
  `Workflow-testera.md` §13 owns it. Rules live in exactly one place: the ticket says **what** to
  check, the wiki says **how** to test. Old tickets keep their frame until a `/qa-ticket #<n>`
  re-baseline strips it; a closed QA ticket is never re-run, so editing it buys nothing.
- **Every test step carries a stable id (owner rule, 2026-08-27, #742).** Each checkbox under
  `## Test scenarios` opens with `TC01`, `TC02`, … The id belongs to the **step, not its
  position**: a deleted step leaves a gap, a new one takes the next unused number, and a retired
  number is never reused, because ids travel into run reports, bug titles and other tickets. Ids
  are unique per ticket and never restart inside the next scenario; `### S01 — name` grouping is
  optional and never enters the id, so the full id is always `QA-FE-{nn}-TC{kk}`. The wiki's
  `Workflow-testera.md` §13 owns this rule.
- **Hand over at most 3 tickets at a time**, and give every filed bug a verdict the same day
  (`valid` / `by design + why` / `needs retest`). A large batch rots, and without the feedback loop
  a tester keeps applying a wrong mental model.
- **A QA ticket closes when the run is reported, not when the bugs are fixed (owner rule,
  2026-08-20).** Its done: every scenario has a status, every non-pass has a same-day verdict, and
  every `valid` finding has its **own** ticket. Then it closes, with one run-report comment per
  execution (wiki §5, #767). The retest obligation moves **onto the bug ticket** (an acceptance
  criterion naming the scenario, e.g. "re-run QA-FE-01 / `S01_PUBLIC` TC01"). Never reopen a closed
  QA ticket: the next pass is a fresh, re-baselined run. **The exception is `blocked:`**: a
  scenario that could not run produced no information, so the QA ticket (or a follow-up run) stays
  open. `FAILED` = information obtained; `blocked:` = work not done (worked example: #262 closed at
  6/7 with the failures carved out to #670 and #672).
- **A blocked run is labelled `qa-blocked`, by the tester (owner rule, 2026-08-21).** An open QA
  ticket does not say why it is open, so a run stalled on a missing precondition would look like one
  nobody started. The tester applies the label (they hold write access) and names the missing
  precondition in a comment;
  `gh issue list --label qa-blocked` is the owner's queue. The owner removes the label when the
  precondition lands, which is the tester's signal to finish the run. The label comes off once
  nothing on the ticket is blocked any more, not once the first blocked scenario clears.
- **Preconditions are the owner's job, not the tester's.** Non-default row states come from
  `scripts/qa/seed-staging.sql` (staging only; one approve in the UI afterwards rebuilds the
  artifact). Sample `exported.txt` files and the admin login are owner-provided. A scenario without
  its precondition ships as `blocked:`, never as a hopeful checkbox, and the ticket is handed over
  already carrying `qa-blocked`.

### Loop mode — one ticket = one PR, in its own fresh headless process and worktree

Two non-negotiables: **one ticket = one PR** (git hygiene) and **one ticket = one fresh context**
(cost and quality). The full manual, with every outcome and setting, is `docs/claude-loop.md`.

- **The loop is a script, not a session.** The owner starts the conductor
  (`scripts/claude/backlog-loop.sh`) with `scripts/claude/start-loop.sh` in a plain terminal,
  never from a Claude Code session: a background command there is killed after two hours with no
  cleanup, and a batch runs five hours or more (#969). `/backlog <numbers>` only prints that line,
  and `/backlog` after the run reports the roll-up. It takes the tickets the owner names, or picks
  ready ones (`next-ticket.sh`).
- Each ticket runs in a **fresh headless process** (`work-ticket.sh` → `claude -p "/work-ticket
  <n>"`) inside its **own worktree** `.claude/worktrees/ticket-<n>` cut from `origin/main`, up to
  three at once. The session does the whole slice (spec weight, branch, implement, tests,
  `code-reviewer` gate, commit → push → PR), ends with a `STATUS: DONE|BLOCKED` block and dies. No
  LLM context outlives a ticket; an orchestrator *session* that collects every ticket's returns is
  a retired anti-pattern.
- **Only maintainer-written tickets may drive the loop** (this repo is public): `issue-trust.sh`
  refuses any issue whose author *or any commenter* lacks write access, fails closed, and runs in
  front of the session, so naming a ticket cannot bypass it (ADR-0026).
- **The loop stops at the PR (ADR-0060).** Nothing in it merges or assigns. The owner approves a PR
  by **assigning themselves** (GitHub does not let an author approve their own PR), and
  `/merge-train` merges only PRs assigned after their last push, with green required checks and
  zero open CodeQL alerts. No session ever sets the assignee.
- **Never let two tickets share a working copy.** The main checkout is never touched. A ticket
  with an open PR or a worktree is skipped, except a worktree kept after a usage limit, whose
  session the next run resumes (#934). Leftovers of a failed or blocked run are committed (never
  deleted, never stashed) on a `loop-salvage/<n>-<timestamp>` branch. BLOCKED tickets get the
  `loop-blocked` label plus the open questions as an issue comment. Business questions are
  **extracted for the user, never invented**, by the worker and the conductor alike.
- Starting the loop **is** the standing authorization for commit → push → PR, never for a merge. A
  single large change (a whole new module) is **one ticket**; a large diff is fine, two tickets in
  one working copy or one context are not.

## Roadmap (digest — details live as GitHub issues; `gh issue list`)

- **M1 — Patcher: DONE**, empirically proven on live game updates.
- **M2 — TMS backend: DONE** (core loop + update lifecycle, spec 0001): version-bound import of
  `exported.txt` + diff/invalidation, list/get/upsert/approve translations (approve clears
  invalidation and rebuilds the artifact), translation-file distribution (pre-built artifact +
  ETag/304, `GET /translation-files/{lang}`), GameVersion endpoints, CLI auto-download (M2-20,
  `Features/TranslationFileSyncing` + `UpdateChecking`). **The forum watcher (M2-18 / #85) is cut
  to post-MVP** (owner decision 2026-06, ADR-0030): game-version registration stays manual, like the
  export→import pipeline. Neither the loop nor a contributor works #85 without an explicit owner
  go-ahead.
- **M3 — Frontend (Blazor Static SSR): DONE**; the manual QA pass **QA-FE / #275 is still open**.
  OIDC sign-in; pages: dashboard, translation list, editor with `<--DO_NOT_TOUCH!-->` placeholder
  validation + approve flow, import/export, game-versions admin, "Moje konto" and the legal pages.
- **M6 — Deployment: DONE.** CD over ssh + `docker compose` to a **Hetzner VPS** (Caddy ingress,
  Let's Encrypt TLS) + Neon Postgres, staging + prod two-stage promotion, secrets in a `chmod 600`
  `/opt/lotro/.env` per box, daily health ping (ADRs 0008–0029 for the pipeline; **ADR-0034** moved
  the hosting off Azure, epic #486). Ops details: `docs/deployment/runbook.md`.
- **LEGAL — GDPR/compliance pack: DONE** (epic #459): two-phase account deletion (ADR-0031), data
  export, terms of service + privacy policy, cookie banner, self-hosted fonts, e-mail change.
  Account-takeover hardening continues under epic SEC-00 (#691).
- **M7 — Game-content catalog: NEXT UP** (spec 0008, agreed 2026-07-06; epic #362, tickets
  #363–#375; not started). LOTRO Companion's lore XML imported as a catalog lens over the flat
  rows: catalog entries (quest/deed + a registry-driven long tail incl. items) with role-tagged text
  slots joined **by `(FileId, GossipId)` keys, never text**; admin zip import (replace-per-kind,
  COPY idiom), catalog browser + per-entry/per-category Approved-based progress, atomic quest
  translation UX (entry page + editor context + entry bulk approve), translation→entry
  memberships. The lens never changes translations and never triggers the artifact. Naming rule:
  **CatalogEntry, never "entity"** (the DDD-Entity misconception; user decision 2026-07-06).
  **DoD:** import fixture → browse `/catalog` → translate a whole quest via editor context → entry
  bulk approve → the artifact contains the rows (E2E).
- **M4 — desktop player app (Avalonia, ADR-0033): not started** (#41–#46). A GUI over the patcher
  handlers + the same TMS auto-download the CLI ships. The MVP is Windows-only, but the framework
  keeps the Steam Deck/Proton path open (WPF would close it).
- **Post-MVP backlog (cut from MVP):** glossary, `TranslationHistory`, bulk operations, keyboard
  shortcuts, AI review, Discord notifications, public API versioning, crowdsourced game-version
  reports, per-language roles, Companion data auto-fetch from GitHub. **Epic TP-00 (#377)** parks
  the post-M7 productivity/ecosystem pack with its evidence and promotion order (TP-01/TP-10 are
  `/spec`-first).

## Proactive command use

The `/ticket`, `/spec`, `/feature`, `/adr`, `/qa-ticket` workflows are model-invocable: reach for
them yourself when the request matches, without waiting for the user to type the slash.

- User references **a ticket number or pastes an issue** → **`/ticket`**.
- User asks **which tickets are worth working / whether they make sense** ("czy te tickety mają
  sens", "co brać najpierw", "przejrzyj backlog zanim zaczniemy") → **`/preflight`** over them:
  verdicts and lanes only, never start implementing from a list.
- User wants a **manual QA scenario written, refreshed or handed to a tester** → **`/qa-ticket`**.
  Same when triaging a tester's bug report: verify the claim against the code before answering, and
  re-baseline the QA ticket that produced it.
- User floats a **rough feature idea** with unclear business rules → **`/spec`** first.
- User describes a **concrete new use case** → mirror the nearest sibling slice (**`/feature`
  scaffolds the patcher-style Application slice**; for a TMS slice, mirror the nearest TMS sibling
  and "Anatomy of a feature slice").
- User is **settling an architecture/modeling choice** → **`/adr`** first, then implement.
- Any **DAT binary format work** → the **`dat-format-expert`** agent.
- User says **"kontynuuj pracę w pętli" / "continue the loop" / "work through the backlog" /
  "jazda dalej"** (any keep-grinding-tickets phrasing) → **`/backlog <numbers>`** with the tickets
  the owner names (ask once which ones when none are named: `/backlog` with no arguments only
  reports the last run). It prints the terminal command for the owner to run. NEVER start the loop
  from a session, in the background or not (#969); NEVER grind tickets inline in the current session
  or spawn per-ticket subagents from it; never route to `/loop`.

Don't narrate "I'll run the command": follow the workflow and report results. Never scaffold off a
vague one-liner: if a business rule is unclear, ask once, then proceed.
