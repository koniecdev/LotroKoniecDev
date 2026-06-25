using System.Security.Claims;
using LotroKoniecDev.Hateoas.ContentNegotiation;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.API.Hateoas.TranslationAggregateFactories;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// Returns one translation in full by id (spec 0001), read from the POCO read model — never the
/// write aggregate (CQRS, ADR-0002 amendment). An unknown (or all-zeros) id is a <c>NotFound</c>.
/// The handler also surfaces the row's soft-removal state so the endpoint can shape the HATEOAS link
/// set (a removed row exposes <c>self</c> only); the Contracts DTO stays clean.
/// </summary>
internal sealed class GetTranslation : IEndpoint
{
    /// <summary>The translation view plus the lifecycle state the HATEOAS link factory needs.</summary>
    internal sealed record QueryResult(TranslationDetailResponse Response, bool IsRemoved);

    internal sealed record Query(TranslationId Id) : IQuery<Result<QueryResult>>;

    internal sealed class Handler : IQueryHandler<Query, Result<QueryResult>>
    {
        private readonly IApplicationReadDbContext _readDbContext;

        public Handler(IApplicationReadDbContext readDbContext)
        {
            _readDbContext = readDbContext;
        }

        public async ValueTask<Result<QueryResult>> Handle(Query query, CancellationToken cancellationToken)
        {
            // An all-zeros id never identifies a row — short-circuit before touching the database.
            if (query.Id == TranslationId.Empty)
            {
                return Result.Failure<QueryResult>(DomainErrors.TranslationEntity.NotFound(query.Id));
            }

            // Inlined (not the shared TranslationProjections.ToDetail) so the soft-removal flag rides
            // alongside the detail view in a single projection — mirrors KittySaver's GetCat.
            QueryResult? result = await _readDbContext.Translations
                .Where(translation => translation.Id == query.Id)
                .Select(translation => new QueryResult(
                    new TranslationDetailResponse(
                        translation.Id,
                        translation.FileId,
                        translation.GossipId,
                        translation.SourceText,
                        translation.ArgsOrder,
                        translation.ArgsId,
                        translation.TranslatedText,
                        translation.PreviousSourceText,
                        translation.SubmittedBy == null
                            ? null
                            : new TranslatorSummaryResponse(translation.SubmittedBy.Id, translation.SubmittedBy.DisplayName),
                        translation.ApprovedBy == null
                            ? null
                            : new TranslatorSummaryResponse(translation.ApprovedBy.Id, translation.ApprovedBy.DisplayName),
                        translation.Status,
                        translation.CreatedAt,
                        translation.UpdatedAt),
                    translation.RemovedInVersion != null))
                .FirstOrDefaultAsync(cancellationToken);

            return result is null
                ? Result.Failure<QueryResult>(DomainErrors.TranslationEntity.NotFound(query.Id))
                : Result.Success(result);
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("/api/v1/translations/{id:guid}", async (
                Guid id,
                IQueryHandler<Query, Result<QueryResult>> handler,
                ITranslationAggregateLinkFactory translationLinkFactory,
                ClaimsPrincipal user,
                CancellationToken cancellationToken) =>
            {
                Result<QueryResult> result = await handler.Handle(new Query(TranslationId.FromValue(id)), cancellationToken);

                if (result.IsFailure)
                {
                    return Results.Problem(result.Error.ToProblemDetails());
                }

                QueryResult queryResult = result.Value;
                bool callerIsAdmin = user.IsInRole(AuthConstants.Roles.Admin);

                return HateoasResults.Ok(queryResult.Response, r =>
                    r.Links = translationLinkFactory.CreateTranslationLinks(
                        r.Id, r.Status, queryResult.IsRemoved, callerIsAdmin));
            })
            .WithName(nameof(GetTranslation))
            .WithTags("Translations")
            .RequireAuthorization(AuthorizationPolicies.RequireTranslatorRole)
            .Produces<TranslationDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
