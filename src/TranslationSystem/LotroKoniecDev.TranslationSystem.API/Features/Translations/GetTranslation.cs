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
/// Returns one full translation by id (spec 0001), read from the read model and never from the write
/// aggregate (CQRS, ADR-0002 amendment). An unknown id, or an all-zeros one, gives a <c>NotFound</c>.
/// The handler also reports whether the row is soft-removed, so the endpoint can decide which links to
/// send: a removed row gets only <c>self</c>. The DTO in Contracts stays free of that.
/// </summary>
internal sealed class GetTranslation : IEndpoint
{
    /// <summary>The translation as the client sees it, plus the state the link factory needs.</summary>
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
            // An all-zeros id can never match a row, so answer before touching the database.
            if (query.Id == TranslationId.Empty)
            {
                return Result.Failure<QueryResult>(DomainErrors.TranslationEntity.NotFound(query.Id));
            }

            // Written out here instead of using the shared TranslationProjections.ToDetail, so the
            // soft-removal flag comes back with the detail view in one query, as KittySaver's GetCat
            // does.
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
                bool callerIsTranslator = callerIsAdmin || user.IsInRole(AuthConstants.Roles.Translator);

                return HateoasResults.Ok(queryResult.Response, async r =>
                    r.Links = await translationLinkFactory.CreateTranslationLinksAsync(
                        r.Id, r.Status, queryResult.IsRemoved, callerIsTranslator, callerIsAdmin));
            })
            .WithName(nameof(GetTranslation))
            .WithTags("Translations")
            .RequireAuthorization(AuthorizationPolicies.RequireTranslatorRole)
            .Produces<TranslationDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
