# TheKittySaver lift map and the de-mediatorization recipe

> Reference material moved out of `CLAUDE.md` (#796) so it loads on demand: the routing table
> sends a new TMS slice here when no sibling slice fits. The lift itself is **done** — every
> row exists in the repo — and the map stays as the pattern reference. Deliberate non-lifts
> (YAGNI decisions) stay in `CLAUDE.md` → "Architecture".

## TMS — the KittySaver lift map

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
