using System.Security.Claims;
using LotroKoniecDev.Hateoas.ContentNegotiation;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.API.Hateoas.GameVersionAggregateFactories;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.GameVersions;

/// <summary>
/// Lists every known game version newest-first (spec 0001): the admin watches which versions are
/// pending (Unprocessed), already Processed, or Superseded. Reads the POCO read model — never the
/// write aggregate (CQRS, ADR-0002 amendment). Few rows ever exist (one per game update), so this is
/// an unpaged ordered list by design.
/// </summary>
internal sealed class ListGameVersions : IEndpoint
{
    internal sealed record Query : IQuery<Result<IReadOnlyList<GameVersionResponse>>>;

    internal sealed class Handler : IQueryHandler<Query, Result<IReadOnlyList<GameVersionResponse>>>
    {
        private readonly IApplicationReadDbContext _readDbContext;

        public Handler(IApplicationReadDbContext readDbContext)
        {
            _readDbContext = readDbContext;
        }

        public async ValueTask<Result<IReadOnlyList<GameVersionResponse>>> Handle(Query query, CancellationToken cancellationToken)
        {
            List<GameVersionResponse> items = await _readDbContext.GameVersions
                .OrderByDescending(gameVersion => gameVersion.DetectedAt)
                .Select(gameVersion => new GameVersionResponse(
                    gameVersion.Id,
                    gameVersion.LotroNotationVersion,
                    gameVersion.DetectedAt,
                    gameVersion.Status))
                .ToListAsync(cancellationToken);

            return Result.Success<IReadOnlyList<GameVersionResponse>>(items);
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("/api/v1/game-versions", async (
                IQueryHandler<Query, Result<IReadOnlyList<GameVersionResponse>>> handler,
                IGameVersionAggregateLinkFactory gameVersionLinkFactory,
                ClaimsPrincipal user,
                CancellationToken cancellationToken) =>
            {
                Result<IReadOnlyList<GameVersionResponse>> result = await handler.Handle(new Query(), cancellationToken);

                if (result.IsFailure)
                {
                    return Results.Problem(result.Error.ToProblemDetails());
                }

                CollectionResponse<GameVersionResponse> response = new() { Items = result.Value };
                bool callerIsAdmin = user.IsInRole(AuthConstants.Roles.Admin);

                return HateoasResults.Ok(response, collection =>
                {
                    foreach (GameVersionResponse item in collection.Items)
                    {
                        item.Links = gameVersionLinkFactory.CreateGameVersionLinks(item.Id);
                    }

                    collection.Links = gameVersionLinkFactory.CreateCollectionLinks(callerIsAdmin);
                });
            })
            .WithName(nameof(ListGameVersions))
            .WithTags("GameVersions")
            .RequireAuthorization(AuthorizationPolicies.RequireTranslatorRole)
            .Produces<CollectionResponse<GameVersionResponse>>(StatusCodes.Status200OK);
    }
}
