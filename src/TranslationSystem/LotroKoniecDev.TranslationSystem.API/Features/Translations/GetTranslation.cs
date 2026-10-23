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
/// The handler also reports whether the row is soft-removed, for the links, so the Contracts DTO stays
/// free of that.
/// </summary>
internal sealed class GetTranslation : IEndpoint
{
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
            if (query.Id == TranslationId.Empty)
            {
                return Result.Failure<QueryResult>(DomainErrors.TranslationEntity.NotFound(query.Id));
            }

            // Not TranslationProjections.ToDetail, so the removal flag comes back in the same query.
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
