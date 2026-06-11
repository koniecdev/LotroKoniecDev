# ADR-0001: Slim SRP handlers instead of the Mediator pipeline

**Status:** Accepted
**Date:** 2026-06-11
**Decision-makers:** Solo maintainer
**Related:** Application layer (all Features), CLI commands, ticket #59 (Application split), PR #78 (M1-09 pipeline behaviors)

## Context

M1 wired the Application layer through `martinothamar/Mediator` 3.0.1 (source-generated):
commands/queries implement `Mediator.ICommand<T>`/`IQuery<T>`, CLI commands inject `ISender`
and dispatch through the generated mediator, and two `IPipelineBehavior<,>` implementations
(`RequestLoggingPipelineBehavior`, `ValidationPipelineBehavior`) wrap every request.

Code facts that constrain the choice:

- This repo is the architectural boilerplate going forward: TheKittySaver patterns
  (Result monad, vertical feature slices, FluentValidation-for-commands, black-box tests)
  with exactly one deliberate deviation — no mediator.
- `ValidationPipelineBehavior` is a **runtime no-op**: no `AddValidatorsFromAssembly()` (or any
  validator registration) exists anywhere, so its injected `IEnumerable<IValidator<TRequest>>`
  is always empty. All four validators are dead code today.
- `ValidationPipelineBehavior` throws `ValidationException` for what is a user-facing rule —
  violating the house rule that business failures are `Result` values, never exceptions.
- `Mediator.SourceGenerator` pulls in `Scriban` 6.2.0 with 12 known vulnerabilities
  (1× critical NU1904, 8× high NU1903, 3× moderate NU1902) — visible on every build.
- The only consumer is the CLI (3 Spectre.Console commands). Each CLI command already knows
  exactly which request it sends; `ISender` indirection buys discoverability we don't use, and
  handler wiring is verified at runtime instead of compile time (the same complaint that
  motivated ticket #59).

## Decision

### 1. In-house messaging abstractions, same vocabulary

`LotroKoniecDev.Application.Abstractions.Messaging` defines `ICommand<TResponse>`,
`IQuery<TResponse>`, `ICommandHandler<TCommand, TResponse>`, `IQueryHandler<TQuery, TResponse>`
with `ValueTask<TResponse> Handle(...)`. Records and handlers keep their names and shapes —
only the `using Mediator;` goes away. CQRS semantics stay; the dispatch layer does not.

### 2. Handlers are injected directly — no dispatcher

Consumers (CLI commands today, Minimal-API endpoints and WPF view models later) constructor-inject
the closed handler interface they need, e.g.
`ICommandHandler<ApplyPatchCommand, Result<PatchSummaryResponse>>`. No `ISender`, no `Send()`.
A missing registration is a compile-visible explicit line in DI, not a runtime dispatch failure.

### 3. Validation is a value, placed per the house rule

- **Commands:** FluentValidation validators stay; the command handler constructor-injects
  `IValidator<TCommand>`, calls `Validate()`, and maps failures to
  `Result.Failure(Error.Validation(...))` via `ValidationResultExtensions.ToValidationError()`.
- **Queries:** no FluentValidation. Query handlers guard their inputs inline and return
  `Result.Failure(Error.Validation(...))`. The two query validators are deleted.
- CLI maps `ErrorType.Validation` → `ExitCodes.InvalidArguments`, completing the exit-code contract.

### 4. No pipeline behaviors

Both behaviors are deleted. Uniform request logging was the only real loss; for a local CLI,
Serilog logging in services plus `IOperationStatusReporter` at the presentation edge already
covers diagnostics. Cross-cutting telemetry returns only when a real need arrives (e.g. the M3
API layer), and then as an explicit decision — not speculative plumbing now.

### 5. Explicit DI registration

`AddApplicationServices()` registers each handler interface→implementation pair and each command
validator explicitly. Four lines today; the registration list doubles as a use-case inventory.

### 6. Mediator packages removed

`Mediator.Abstractions` and `Mediator.SourceGenerator` leave `Directory.Packages.props` and
`Application.csproj`, taking the vulnerable transitive `Scriban` with them.

## Consequences

### Positive

- One less framework concept; a new feature is: record + handler + (command) validator + DI line.
- Compile-time-visible wiring; CLI commands declare their real dependencies.
- Validation actually runs (it never did), and as a `Result` value, not an exception.
- Build drops 24 NuGet vulnerability warnings (Scriban transitive removed).
- Boilerplate stays copy-paste consistent for M2 (DB handlers) and M3 (API endpoints inject
  handlers exactly the same way).

### Negative / Accepted Trade-offs

- No single choke point for cross-cutting behavior; uniform request logging/telemetry must be
  reintroduced deliberately (decorator or middleware) if a real need appears.
- Each consumer constructor names full closed generic types — more verbose than `ISender`.
- Manual registration line per handler (mitigated: it doubles as documentation).

## Alternatives Considered

### A. Keep `martinothamar/Mediator`

Source-gen, fast, already wired. Rejected. The indirection serves no consumer here, hides wiring
until runtime, and ships a vulnerable transitive dependency; the project's stated identity is
"TheKittySaver minus mediator".

### B. Switch to MediatR

Rejected. Same indirection, plus commercial licensing since v13 — strictly worse than A.

### C. Bare concrete handler classes, no interfaces

Maximally slim. Rejected. Loses the uniform seam that makes handlers mirror-able and mockable as
a category (NSubstitute on closed interfaces), and breaks the symmetry the M3 API slices will copy.

### D. Interfaces + decorator chain replacing pipeline behaviors

Rejected for now. Re-introduces pipeline-by-another-name with per-handler composition ceremony;
nothing today needs uniform cross-cutting. Revisit at M3 if the API layer proves the need (YAGNI).

## Implementation Notes

- New: `Application/Abstractions/Messaging/{ICommand,IQuery,ICommandHandler,IQueryHandler}.cs`,
  `Application/Extensions/ValidationResultExtensions.cs`
- Changed: all 4 feature records + handlers, `ApplicationDependencyInjection`,
  `ExportCommand`, `PatchCommand`, `LaunchCommand`, `ErrorMapper`,
  `Application.csproj`, `Directory.Packages.props`
- Deleted: `Application/Behaviors/` (both files), `ExportTextsQueryValidator`,
  `PreflightCheckQueryValidator`
- Tests: command-handler tests construct real validators; new validation-failure tests added

## References

- TheKittySaver (`~/RiderProjects/TheKittySaver`) — the reference architecture this repo mirrors
- Ticket #59 — compile-time wiring motivation (Application split)
- PR #78 (M1-09) — the pipeline behaviors this ADR supersedes
- `docs/knowledge-base/` — empirical DAT/update findings (unaffected by this decision)
