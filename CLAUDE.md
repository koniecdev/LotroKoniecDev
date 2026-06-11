# CLAUDE.md — LotroKoniecDev

> Project memory — **self-contained**: a fresh clone has everything the AI needs, with no
> machine-local config required. When a doc and the code disagree, **the code wins**: read the
> file, use what's there, and fix or flag the stale doc.

## What this is

A **LOTRO Polish translation patcher** on **.NET 10 / C# 13**: exports English texts from the
game's binary DAT file, injects `||`-format Polish translations back, and launches the game.
CLI today (`export` / `patch` / `launch`); the roadmap adds PostgreSQL (M2), Web API + Blazor SSR
(M3), WPF (M4), auth (M5) — see `docs/PROJECT_PLAN.md`.

**Architectural identity:** this repo is a boilerplate copy of **TheKittySaver**
(`~/RiderProjects/TheKittySaver` — the reference for every pattern: Result monad, feature slices,
testing philosophy, spec/ADR discipline) with **exactly one deviation — no mediator**.
Commands/queries are dispatched by **direct handler injection** (ADR-0001). Never add
Mediator/MediatR back.

## Project status — pre-release, no users

Active development, zero production users. **Breaking changes are free** — no back-compat shims,
no deprecation windows. Optimize for the right end-state. (Revisit at first public release.)

## Architecture

Strict Clean Architecture, 5 layers. Dependency rule (NEVER violate the downward flow):

**Cli (presentation) / Infrastructure → Application → Domain → Primitives**

| Project | Role |
|---|---|
| `LotroKoniecDev.Cli` | Spectre.Console commands; resolves paths, reports, maps `Error` → exit code. Presentation only — WPF will sit beside it (M4) |
| `LotroKoniecDev.Application` | feature slices (`Features/<Area>/`): command/query records + slim handlers + services; `Abstractions/` ports |
| `LotroKoniecDev.Domain` | `Result`/`Maybe` monads, `Error` + `DomainErrors` factories, DAT models (`SubFile`, `Fragment`, `Translation`), `VarLenEncoder` — Eric Evans DDD |
| `LotroKoniecDev.Infrastructure` | native lotro DLL interop (`datexport.dll`), DAT file handler, forum fetcher, process/launcher adapters |
| `LotroKoniecDev.Primitives` | constants + enums, zero dependencies |
| `tests/` | `Tests.Unit` (pure, mocked), `Tests.Infrastructure` (reserved: real DAT/DB), `Tests.E2E` (full pipeline, Windows-only, `SkippableFact`) |

## Read-first routing (do this BEFORE touching the area)

| You're about to… | Read first |
|---|---|
| Work a GitHub ticket end-to-end | run **`/ticket <number>`** — it drives BRD/spec → branch → slice → review |
| Touch DAT binary parsing / writing / native interop | delegate to the **`dat-format-expert`** agent — it holds the full format spec + KB index |
| Re-investigate update behavior, vnum, translation survival, launch flow | **don't** — empirically settled in `docs/knowledge-base/` (start at its README; 6 live tests incl. 48.0 major) |
| Make a non-trivial architectural/modeling decision | skim `docs/adr/`, then **write a new ADR** (`/adr`) |
| Implement a feature whose business rules are fuzzy | **`/spec`** first (seed → questions → agreed spec in `docs/specs/`) |
| Review a finished change | the **`code-reviewer`** agent (acceptance criteria + architecture + test purity) |
| Understand the backlog / milestones | `gh issue list` (live truth) + `docs/TICKETS.md` (static plan) |
| Compare with the Russian sister project | `docs/RUSSIAN_PROJECT_RESEARCH.md` + `docs/knowledge-base/russian-project.md` |

## Commands

```bash
# Build — zero-warnings gate: TreatWarningsAsErrors is on repo-wide; any warning IS a failing build
dotnet build LotroKoniecDev.slnx

# Tests
dotnet test                                            # everything runnable on this OS
dotnet test tests/LotroKoniecDev.Tests.Unit            # fast, pure unit (this must always be green)
dotnet test tests/LotroKoniecDev.Tests.E2E             # full pipeline — auto-skips off-Windows (SkippableFact)
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

Exit codes: `0` success, `1` invalid arguments (incl. `ErrorType.Validation`), `2` file not found,
`3` operation failed, `4` cancelled.

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

## Translation file format

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
  `DomainErrors.*` factories / `Error.Validation(...)`. `ArgumentNullException.ThrowIfNull` &
  guards are for **programmer** errors only — never throw for a user-facing rule.
- **No mediator — slim SRP handlers (ADR-0001).** One use case = one record + one handler
  implementing the in-house `ICommandHandler<,>`/`IQueryHandler<,>`
  (`Application/Abstractions/Messaging/`). Consumers inject the closed handler interface
  directly. `Mediator`/`MediatR` packages are forbidden.
- **Validation:** FluentValidation **for commands only** — the command handler injects
  `IValidator<TCommand>` and maps failures to `Result` (never throws). Queries validate inline
  in their handler. Every validator must be **registered in DI** (`AddApplicationServices`).
- **Handlers are orchestrators.** Business logic lives in domain/application services
  (`PatchingService`, `SimplifiedGameLaunchingStrategy`); handlers validate, delegate, return.
- **EF Core (from M2 on):** Fluent API only (never attributes), `nameof()` for column names,
  `MaxLength`/`Precision`+`Scale` over `HasColumnType`, no needless `IsRequired()` (value types &
  non-null strings are already required), FK property names parametrized with `nameof()`.
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

## Anatomy of an Application feature slice

One use case = one folder: `Application/Features/<Area>/`. Canonical example to copy:
`Features/Patching/` (command) and `Features/PreflightChecking/` (query). Shape:

```
Features/<Area>/
├── <Action>Command.cs        public sealed record : ICommand<Result<TResponse>>
│     (or <Action>Query.cs    public sealed record : IQuery<Result<TResponse>>)
├── <Action>CommandHandler.cs internal sealed class : ICommandHandler<TCommand, Result<TResponse>>
│                             — explicit ctor DI; ValueTask Handle(); commands inject IValidator<TCommand>,
│                               map failures via .ToValidationError(nameof(TCommand)); queries guard inline
├── <Action>CommandValidator.cs  commands only — FluentValidation, sealed
├── <X>Response.cs            public sealed record result DTO (with ToString() for CLI display)
└── <X>Service.cs             optional — real business logic when the handler would exceed orchestration
```

Then wire the rest — **all three steps, every time**:
1. **DI**: register handler + validator explicitly in `ApplicationDependencyInjection.AddApplicationServices()`.
2. **Consumer**: CLI command injects the closed interface, e.g.
   `ICommandHandler<ApplyPatchCommand, Result<PatchSummaryResponse>>`, calls
   `await handler.Handle(request, cancellationToken)`, maps failure via
   `ErrorMapper.MapErrorToExitCode(result.Error)`.
3. **Tests**: handler tests in `Tests.Unit/Tests/Features/` (construct the real validator, mock
   the ports); E2E only if the CLI pipeline contract changed.

**Mirror the nearest existing sibling slice** rather than inventing structure.

## Testing philosophy — repo-authoritative

- **Black box over the public seam — never the implementation.** Assert observable behavior:
  inputs in → `Result`/persisted state out. NSubstitute stubs **genuine boundaries**
  (`IDatFileHandler`, `IForumPageFetcher`), never internals you own.
- **`.Received()` policy:** only for side effects invisible in the return value (resource cleanup
  `Close()`/`Dispose()`, "destructive op was NOT called on validation failure"). If the return
  value already proves it, `.Received()` is forbidden — a behavior-preserving refactor must never
  break a test.
- **Unit tests are pure:** no filesystem asserts, no network, no DB, no order dependence. File
  content verification = `Tests.Infrastructure`, not `Tests.Unit`.
- **Edge cases are first-class.** Happy path is the floor. `[Theory]` + `[InlineData]` for the
  unhappy-path/boundary matrix (empty, max, malformed, already-in-state).
- **AAA always; assertions inline in the test method** — never hidden in helpers. DRY the
  Arrange (builders like `TestDataFactory`), never the Assert. One reason to fail per test.
- **Tooling: xUnit + Shouldly + NSubstitute only.** Naming: `MethodName_Scenario_ExpectedResult`.
- Platform honesty: tests must pass on macOS AND Windows — build paths with `Path.Combine`,
  never hardcode `C:\` (the CLI itself is Windows-only; tests are not).

## Workflow (the loop that compounds)

1. **Ticket before code.** Work flows from GitHub issues (`M{milestone}-{nn}: Title`,
   labels `critical/high/medium/low` + `bug/refactor/infra/feature/test`, body with
   Context / Depends on / Tasks / Acceptance criteria). Run **`/ticket <n>`** — it pulls the
   issue, grounds it in code + knowledge base, and produces the BRD/spec.
2. **Spec before code.** Anything non-trivial gets `docs/specs/NNNN-*.md` (via `/ticket` or
   `/spec`). Open questions are **extracted for the user, never invented**. Implementation starts
   only at **Status: Agreed**.
3. **Decision before code.** Non-trivial modeling/architecture choice → **`/adr`** first
   (house format, anchor: `docs/adr/0001-*`), implement second.
4. **Slice, mirror, test, review.** Branch via `gh issue develop <n> --checkout`
   (`{n}-{kebab-title}`). Implement by mirroring the nearest sibling slice (`/feature`), add
   unit tests, then run the **`code-reviewer`** agent on the diff (**`/security-review`** for
   anything touching native interop, file protection, or auth later).
   **Green build + zero warnings + clean review = "done" — not before.**
5. **PR closes the ticket.** Title mirrors the ticket; body contains `Closes #<n>`.
   Ask before pushing.
6. **Feed the flywheel.** When a correction is reusable, persist it so it can't recur:
   agent-specific lesson → `.claude/agent-memory/<agent>/`; a global rule → **this file**;
   a real decision → a new ADR; an empirical DAT/update finding → `docs/knowledge-base/` (dated).
   The same mistake made twice means a rule is missing.

## Proactive command use

The `/ticket`, `/spec`, `/feature`, `/adr` workflows are model-invocable — reach for them yourself
when the request matches, without waiting for the user to type the slash:

- User references **a ticket number or pastes an issue** → run **`/ticket`**.
- User floats a **rough feature idea** with unclear business rules → **`/spec`** first, then `/feature`.
- User describes a **concrete new use case** for an existing area → **`/feature`** directly
  (ask one focused question only if a business rule is genuinely ambiguous — otherwise go).
- User is **settling an architecture/modeling choice** → **`/adr`** first, then implement.
- Any **DAT binary format work** (parsing, writing, args, VarLen, native interop) → hand off to
  the **`dat-format-expert`** agent; don't re-derive the format ad hoc.

Don't narrate "I'll run the command" — just follow the workflow and report results. Never scaffold
off a vague one-liner: if a business rule is unclear, ask once, then proceed.
