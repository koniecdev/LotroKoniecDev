---
description: Scaffold an Application feature slice (record + slim handler + validator + DI + CLI wiring + tests) mirroring the existing pattern
argument-hint: <area> <what the use case does>  e.g. "Patching add a dry-run mode"
---

You are implementing a new **feature slice** in LotroKoniecDev. The request:

> $ARGUMENTS

Work production-ready, end to end. Follow this loop — do not skip steps.

## 1. Orient (read before writing)

- `CLAUDE.md` → "Anatomy of an Application feature slice" + the project house rules.
- The agreed spec in `docs/specs/` or the GitHub ticket acceptance criteria, if either exists.
  State which constraints/criteria this slice must honor.
- The **nearest existing sibling slice**: `Features/Patching/` is the canonical command,
  `Features/PreflightChecking/` the canonical query. Match their structure, naming, DI style and
  error handling exactly.
- DAT binary involved? Hand the format-sensitive part to the **`dat-format-expert`** agent
  instead of re-deriving offsets/VarLen rules ad hoc.

## 2. Decide if an ADR is needed

A non-trivial modeling/architecture decision (new aggregate/model, new persistence shape, a new
cross-layer port, a CLI contract break)? **Stop and run `/adr` first**, then come back. Routine
slice → skip.

## 3. Build the slice — slim SRP handlers, no mediator (ADR-0001)

In `Application/Features/<Area>/`:

- `public sealed record <Action>Command(...) : ICommand<Result<TResponse>>` — or
  `<Action>Query(...) : IQuery<Result<TResponse>>` (`Abstractions/Messaging` namespace).
- `internal sealed class <Action>CommandHandler : ICommandHandler<<Action>Command, Result<TResponse>>`
  — **explicit constructor** (no primary ctor), `ValueTask<...> Handle(...)`,
  `ArgumentNullException.ThrowIfNull` for programmer errors, `Result.Failure` for business rules,
  **never throw for a user-facing rule**.
- **Commands:** a sealed FluentValidation `<Action>CommandValidator`; the handler injects
  `IValidator<TCommand>`, maps failures via `.ToValidationError(nameof(<Action>Command))`.
  **Queries:** no validator class — inline guards returning `Error.Validation(...)`.
- `public sealed record <X>Response(...)` with a `ToString()` the CLI can print.
- Handlers stay orchestrators — real logic goes to a service (`<X>Service`) or Domain.
- New external concern (file, process, network, native DLL)? Define the port in
  `Application/Abstractions/`, implement in Infrastructure.

Wire the rest — **all of it, every time**:

1. **DI**: handler + validator registrations in `ApplicationDependencyInjection.AddApplicationServices()`
   (Infrastructure ports in `InfrastructureDependencyInjection`).
2. **Consumer**: CLI command in `Cli/Commands/` injecting the closed handler interface; map
   failures with `ErrorMapper.MapErrorToExitCode`; register the verb in `Program.cs` if new.
3. **Never** reference `Mediator`/`MediatR`, `ISender`, or pipeline behaviors.

## 4. Test

- Handler behavior → `Tests.Unit/Tests/Features/` — construct the **real validator**, mock only
  genuine ports; happy path + each `Result.Failure` mode + `[Theory]` boundary matrix.
- Cross-platform: build paths with `Path.Combine` — tests run on macOS too.
- E2E (`Tests.E2E`, SkippableFact) only if the CLI pipeline contract changed.

## 5. Verify "done"

- `dotnet build LotroKoniecDev.slnx` green with **zero warnings** (TreatWarningsAsErrors).
- `dotnet test tests/LotroKoniecDev.Tests.Unit` green.
- End with: files created/changed (relative paths), constraints honored, and the exact CLI
  invocation to exercise it manually. If the CLI contract changed, call it out explicitly.

If the request is ambiguous about a business rule (boundary behavior, defaults, scope), ask one
focused question before scaffolding — don't guess.
