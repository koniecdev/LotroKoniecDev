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
TMS backend** (first step: write ADR-0002 recording this pivot). Live backlog: `gh issue list`,
**but** issues are being re-cut after the 2026-06 architecture pivot — where an old issue body
conflicts with this file (MediatR, one shared Application for all UIs, auth postponed to M5),
**this file wins**; align the ticket before coding.

## Architecture — two bounded contexts, one file contract

```
src/
  LotroKoniecDev.{Primitives,Domain,Application,Infrastructure,Cli}                       ← PATCHER (exists, frozen)
  SharedKernel/LotroKoniecDev.SharedKernel                                                ← M2 (lift; TMS-side only)
  TranslationSystem/LotroKoniecDev.TranslationSystem.{Domain,Persistence,Contracts,API}   ← M2 (new)
  AuthSystem/LotroKoniecDev.AuthSystem.{API,Domain,Infrastructure,Persistence,Contracts}  ← M2 (lift)
  Frontend/LotroKoniecDev.Frontend                                                        ← M3 (Blazor SSR, OIDC RP)
  Utilities/…                                                                             ← M2 (lift only what's used)
```

**The contexts share a data contract, not code: the `||` translation file.** CLI `export` →
`exported.txt` → TMS import; TMS export → `polish.txt` → CLI `patch`. Each context owns its own
parser/serializer; **golden fixture files + round-trip tests on both sides** guard against format
drift, and the format itself changes only via ADR. The TMS never references `datexport.dll`/DAT
code (it runs in Linux Docker); the patcher never touches the DB (it runs on a Windows gaming
box). WPF (M4) calls patcher handlers locally and downloads `polish.txt` from the TMS API.

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
green without touching its assertions.

### TMS — the KittySaver lift map

| Building… | Mirror from `~/RiderProjects/TheKittySaver` | Lift notes |
|---|---|---|
| `SharedKernel` | `src/SharedKernel/TheKittySaver.SharedKernel` | Drop the `Mediator.Abstractions` package; add `Messaging/` with in-house `ICommand(Handler)`/`IQuery(Handler)` (same shapes as patcher `Application/Abstractions/Messaging/`). Keep monads, BuildingBlocks, `Ensure`, `StronglyTypedId` |
| `TranslationSystem.Domain` | `src/AdoptionSystem/…AdoptionSystem.Domain` | `Aggregates/<X>Aggregate/{Entities,ValueObjects,Repositories}` + `Core/Errors`; our aggregates are far simpler than `Cat` — don't inflate them |
| `TranslationSystem.Persistence` | `…AdoptionSystem.Persistence` | DbContext + configurations + UoW + migrations + design-time factory; EF house rules below |
| `TranslationSystem.Contracts` | `…AdoptionSystem.Contracts` | Request/response DTOs per feature; referenced by Frontend |
| `TranslationSystem.API` | `…AdoptionSystem.API` | `IEndpoint` + assembly-scan `AddEndpoints`/`MapEndpoints`; slices in `Features/<Area>/<Action>.cs`; `ExceptionHandlers/`, `Auth/` (JwtBearer + policies + `CurrentUserAccessor` + ownership guards), health checks, Serilog + OTel bootstrap |
| `AuthSystem` (whole module) | `src/AuthSystem/*` | Self-hosted OpenIddict + Identity server — lift wholesale. **Do NOT lift the synchronous `RegisterUser`→`CreatePersonAsync` saga**: provision the translator profile lazily & idempotently on first authenticated TMS request (pattern: KittySaver ADR-0007 §4) |
| `Frontend` (infra) | `src/Frontend/TheKittySaver.Frontend` | Lift `Infrastructure/` (OIDC RP, `CookieTokenRefresher`, `DiscoveryCache`, `ApiResult`, typed HttpClients, error pages); pages are written fresh for translations; reference `TranslationSystem.Contracts` directly |
| Docker / compose | `compose.yaml`, `Dockerfile.migrator`, `Dockerfile.tests` | postgres + migrator + auth-api + tms-api (+ mailpit/aspire-dashboard in dev); Frontend joins in M3 |

**Deliberate non-lifts (YAGNI — revisit only on a real, present need):** `ReadModels(+EF)`
read/write split, `Calculators`, per-system `Primitives`, domain events (KittySaver dispatches
them via Mediator notifications; the TMS core loop doesn't need them — if a need appears, design
an in-house dispatcher via ADR first).

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
| Make a non-trivial architectural/modeling decision | skim `docs/adr/`, then **write a new ADR** (`/adr`); anchors: 0001 (no mediator), 0002 (TMS pivot — to be written at M2 start) |
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
```

TMS compose/migration commands land with M2 — **add them to this section the moment they exist.**
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
- **Patcher is frozen** (see Architecture) — never refactor it to serve the TMS.
- **TMS ships with auth from day 1.** Endpoints are authorized by default (public ones are
  explicit); the first migration already carries user attribution (`SubmittedById`,
  `ApprovedById`). No auth-less interim state to retrofit later.
- **Validation:** FluentValidation **for commands only** — the command handler injects
  `IValidator<TCommand>` and maps failures to `Result` (never throws). Queries validate inline
  in their handler. Every validator must be registered in DI.
- **Handlers are orchestrators.** Business logic lives in domain/application services; handlers
  validate, delegate, return.
- **EF Core (`TranslationSystem.Persistence`):** Fluent API only (never attributes), `nameof()`
  for column names, `MaxLength`/`Precision`+`Scale` over `HasColumnType`, no needless
  `IsRequired()` (value types & non-null strings are already required), FK property names
  parametrized with `nameof()`.
- **Right-size the design — YAGNI by default.** Before proposing an abstraction, cache, config
  knob, queue, or new infra, check it solves a **real, present** need from the current
  spec/ticket — not a hypothetical future. Pick the simple path and note the trade-off in one line.

## Code style (C#) — repo-authoritative

- **Sealed** all types unless there is explicit inheritance.
- **Explicit constructors** in classes — no primary constructors for a `class` (records are fine).
- `var` **only for anonymous types**; explicit types everywhere else.
- **LINQ methods**, never query syntax. **Pattern matching** — except inside a query expression.
- **File-scoped namespaces**, **Allman braces**, no `#region`, no useless/obvious comments.
- Code & identifiers in **English**.

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
        // explicit ctor DI: repositories, IUnitOfWork, IValidator<Command> (commands only)…
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

## Roadmap (digest — details land as re-cut GitHub issues)

- **M2 — TMS backend (core loop).** ADR-0002 (record this pivot) → SharedKernel lift →
  AuthSystem lift → TranslationSystem (Domain/Persistence/Contracts/API) with exactly these
  slices: import `exported.txt` (upload), list translations (search/filter/paginate), get one,
  upsert translation, approve translation, export `polish.txt` (download) → compose (postgres +
  migrator + auth-api + tms-api) → integration tests.
  **DoD:** the full loop works: CLI `export` → TMS import → edit/approve → TMS export → CLI
  `patch` → texts visible in game.
- **M3 — Frontend (Blazor SSR).** Lifted OIDC infra; pages: translation list, side-by-side
  editor with `<--DO_NOT_TOUCH!-->` placeholder validation, approve flow, import/export,
  mini-dashboard (progress counters). **DoD:** a translator completes the whole loop in the
  browser, authenticated.
- **M4 — WPF player app** (later): patcher handlers + HTTP download of `polish.txt` from the TMS.
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

Don't narrate "I'll run the command" — just follow the workflow and report results. Never scaffold
off a vague one-liner: if a business rule is unclear, ask once, then proceed.
