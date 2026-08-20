using System.Globalization;
using System.Linq.Expressions;
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
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.GameVersionAggregate;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.GameVersions;

/// <summary>
/// Lists every known game version, newest first (spec 0001), so the admin can see which ones are still
/// Unprocessed, already Processed, or Superseded. It reads the read model and never the write
/// aggregate (CQRS, ADR-0002 amendment). There is one row per game update, so the list is deliberately
/// ordered but not paged.
/// </summary>
internal sealed class ListGameVersions : IEndpoint
{
    internal sealed record Query(string? Sort = null) : IQuery<Result<IReadOnlyList<GameVersionResponse>>>, ISortable;

    internal sealed class Handler : IQueryHandler<Query, Result<IReadOnlyList<GameVersionResponse>>>
    {
        private readonly IApplicationReadDbContext _readDbContext;

        public Handler(IApplicationReadDbContext readDbContext)
        {
            _readDbContext = readDbContext;
        }

        public async ValueTask<Result<IReadOnlyList<GameVersionResponse>>> Handle(Query query, CancellationToken cancellationToken)
        {
            IQueryable<GameVersionReadModel> ordered = string.IsNullOrWhiteSpace(query.Sort)
                ? _readDbContext.GameVersions.OrderByDescending(gameVersion => gameVersion.DetectedAt)
                : _readDbContext.GameVersions.ApplyMultipleSorting(
                    query.Sort,
                    GetSortProperty,
                    gameVersion => gameVersion.Id);

            List<GameVersionResponse> items = await ordered
                .Select(gameVersion => new GameVersionResponse(
                    gameVersion.Id,
                    gameVersion.LotroNotationVersion,
                    gameVersion.DetectedAt,
                    gameVersion.Status))
                .ToListAsync(cancellationToken);

            return Result.Success<IReadOnlyList<GameVersionResponse>>(items);
        }

        /// <summary>
        /// Maps a <c>?sort=</c> key to the read-model column it orders by. An unknown key falls back to
        /// <c>DetectedAt</c> <b>ascending</b>, which is the parser's default. Note that this is
        /// oldest-first, not the newest-first order used when <c>sort</c> is left out.
        /// <c>version</c> sorts the version string alphabetically and not by version number.
        /// <c>status</c> sorts by the enum's name, because the column stores the name, not the number.
        /// </summary>
        private static Expression<Func<GameVersionReadModel, object>> GetSortProperty(string propertyName)
            => propertyName.ToLower(CultureInfo.InvariantCulture) switch
            {
                "version" => gameVersion => gameVersion.LotroNotationVersion,
                "detectedat" => gameVersion => gameVersion.DetectedAt,
                "status" => gameVersion => gameVersion.Status,
                _ => gameVersion => gameVersion.DetectedAt
            };
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("/api/v1/game-versions", async (
                IQueryHandler<Query, Result<IReadOnlyList<GameVersionResponse>>> handler,
                IGameVersionAggregateLinkFactory gameVersionLinkFactory,
                ClaimsPrincipal user,
                CancellationToken cancellationToken,
                [FromQuery] string? sort = null) =>
            {
                Result<IReadOnlyList<GameVersionResponse>> result = await handler.Handle(new Query(sort), cancellationToken);

                if (result.IsFailure)
                {
                    return Results.Problem(result.Error.ToProblemDetails());
                }

                CollectionResponse<GameVersionResponse> response = new() { Items = result.Value };
                bool callerIsAdmin = user.IsInRole(AuthConstants.Roles.Admin);

                return HateoasResults.Ok(response, async collection =>
                {
                    foreach (GameVersionResponse item in collection.Items)
                    {
                        item.Links = await gameVersionLinkFactory.CreateGameVersionLinksAsync(
                            item.Id, item.Status, callerIsAdmin);
                    }

                    collection.Links = await gameVersionLinkFactory.CreateCollectionLinksAsync(callerIsAdmin);
                });
            })
            .WithName(nameof(ListGameVersions))
            .WithTags("GameVersions")
            .RequireAuthorization(AuthorizationPolicies.RequireTranslatorRole)
            .Produces<CollectionResponse<GameVersionResponse>>(StatusCodes.Status200OK);
    }
}
