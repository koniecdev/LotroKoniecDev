using System.Security.Claims;
using LotroKoniecDev.Hateoas.ContentNegotiation;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.API.Hateoas.GameVersionAggregateFactories;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.GameVersions;

/// <summary>
/// Returns one game version in full by id, read from the POCO read model — never the write aggregate
/// (CQRS, ADR-0002 amendment). The item endpoint exists so a game version has a real <c>self</c>
/// hypermedia target (M2-25); an unknown (or all-zeros) id is a <c>NotFound</c>.
/// </summary>
internal sealed class GetGameVersion : IEndpoint
{
    internal sealed record Query(GameVersionId Id) : IQuery<Result<GameVersionResponse>>;

    internal sealed class Handler : IQueryHandler<Query, Result<GameVersionResponse>>
    {
        private readonly IApplicationReadDbContext _readDbContext;

        public Handler(IApplicationReadDbContext readDbContext)
        {
            _readDbContext = readDbContext;
        }

        public async ValueTask<Result<GameVersionResponse>> Handle(Query query, CancellationToken cancellationToken)
        {
            // An all-zeros id never identifies a row — short-circuit before touching the database.
            if (query.Id == GameVersionId.Empty)
            {
                return Result.Failure<GameVersionResponse>(DomainErrors.GameVersionEntity.NotFound(query.Id));
            }

            GameVersionResponse? response = await _readDbContext.GameVersions
                .Where(gameVersion => gameVersion.Id == query.Id)
                .Select(gameVersion => new GameVersionResponse(
                    gameVersion.Id,
                    gameVersion.LotroNotationVersion,
                    gameVersion.DetectedAt,
                    gameVersion.Status))
                .FirstOrDefaultAsync(cancellationToken);

            return response is null
                ? Result.Failure<GameVersionResponse>(DomainErrors.GameVersionEntity.NotFound(query.Id))
                : Result.Success(response);
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("/api/v1/game-versions/{id:guid}", async (
                Guid id,
                IQueryHandler<Query, Result<GameVersionResponse>> handler,
                IGameVersionAggregateLinkFactory gameVersionLinkFactory,
                ClaimsPrincipal user,
                CancellationToken cancellationToken) =>
            {
                Result<GameVersionResponse> result = await handler.Handle(new Query(GameVersionId.FromValue(id)), cancellationToken);

                if (result.IsFailure)
                {
                    return Results.Problem(result.Error.ToProblemDetails());
                }

                bool callerIsAdmin = user.IsInRole(AuthConstants.Roles.Admin);

                return HateoasResults.Ok(result.Value, r =>
                    r.Links = gameVersionLinkFactory.CreateGameVersionLinks(r.Id, r.Status, callerIsAdmin));
            })
            .WithName(nameof(GetGameVersion))
            .WithTags("GameVersions")
            .RequireAuthorization(AuthorizationPolicies.RequireTranslatorRole)
            .Produces<GameVersionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
