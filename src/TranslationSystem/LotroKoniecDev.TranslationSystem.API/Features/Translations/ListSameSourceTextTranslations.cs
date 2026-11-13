using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// The editor's "other rows with the same text" panel (#1347): live rows whose English is exactly the
/// asked row's, so a translator sees how the same words were already translated. Text only, not the args
/// columns: a shown match needs no argument identity, unlike a copy (TP-01, #378).
/// </summary>
internal sealed class ListSameSourceTextTranslations : IEndpoint
{
    /// <summary>The list's default page size. An empty or one-word English text can have thousands of twins.</summary>
    internal const int MaxItems = 50;

    internal sealed record Query(TranslationId Id) : IQuery<Result<SameSourceTextTranslationsResponse>>;

    internal sealed class Handler : IQueryHandler<Query, Result<SameSourceTextTranslationsResponse>>
    {
        private readonly IApplicationReadDbContext _readDbContext;

        public Handler(IApplicationReadDbContext readDbContext)
        {
            _readDbContext = readDbContext;
        }

        public async ValueTask<Result<SameSourceTextTranslationsResponse>> Handle(
            Query query,
            CancellationToken cancellationToken)
        {
            if (query.Id == TranslationId.Empty)
            {
                return Result.Failure<SameSourceTextTranslationsResponse>(DomainErrors.TranslationEntity.NotFound(query.Id));
            }

            // A removed row is out of translation work, and it never carries the rel to this endpoint.
            string? sourceText = await _readDbContext.Translations
                .Where(translation => translation.Id == query.Id && translation.RemovedInVersion == null)
                .Select(translation => translation.SourceText)
                .FirstOrDefaultAsync(cancellationToken);

            if (sourceText is null)
            {
                return Result.Failure<SameSourceTextTranslationsResponse>(DomainErrors.TranslationEntity.NotFound(query.Id));
            }

            IQueryable<TranslationReadModel> liveGroup = _readDbContext.Translations
                .Where(translation => translation.RemovedInVersion == null && translation.SourceText == sourceText);

            List<SameSourceTextTranslationResponse> items = await liveGroup
                .Where(translation => translation.Id != query.Id)
                .OrderBy(translation => translation.TranslatedText == null)
                .ThenBy(translation => translation.FileId)
                .ThenBy(translation => translation.GossipId)
                .Take(MaxItems)
                .Select(TranslationProjections.ToSameSourceTextItem)
                .ToListAsync(cancellationToken);

            // One statement for both numbers. The explicit null check keeps an untranslated twin from
            // counting as a different Polish text: LINQ counts null as a value, SQL does not.
            GroupSummary? summary = await liveGroup
                .GroupBy(translation => translation.SourceText)
                .Select(group => new GroupSummary(
                    group.Count(translation => translation.Id != query.Id),
                    group
                        .Where(translation => translation.TranslatedText != null)
                        .Select(translation => translation.TranslatedText)
                        .Distinct()
                        .Count()))
                .FirstOrDefaultAsync(cancellationToken);

            return Result.Success(new SameSourceTextTranslationsResponse(
                items,
                summary?.OtherRows ?? 0,
                summary?.DistinctTranslations > 1));
        }

        private sealed record GroupSummary(int OtherRows, int DistinctTranslations);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("/api/v1/translations/{id:guid}/same-source-text", async (
                Guid id,
                IQueryHandler<Query, Result<SameSourceTextTranslationsResponse>> handler,
                CancellationToken cancellationToken) =>
            {
                Result<SameSourceTextTranslationsResponse> result =
                    await handler.Handle(new Query(TranslationId.FromValue(id)), cancellationToken);

                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.Problem(result.Error.ToProblemDetails());
            })
            .WithName(nameof(ListSameSourceTextTranslations))
            .WithTags("Translations")
            .RequireAuthorization(AuthorizationPolicies.RequireTranslatorRole)
            .Produces<SameSourceTextTranslationsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
