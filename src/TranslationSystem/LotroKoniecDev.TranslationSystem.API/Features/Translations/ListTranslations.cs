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
using LotroKoniecDev.TranslationSystem.API.Hateoas.PaginationLinkFactories;
using LotroKoniecDev.TranslationSystem.API.Hateoas.TranslationAggregateFactories;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// The editor's list (spec 0001), without soft-removed rows; <c>status=NeedsReview</c> is the "needs
/// retranslation" view. Anyone may read it (#309), because game texts and their translations are public
/// by nature; every action still needs a login, so other callers get no action links.
/// <c>conflictsOnly</c> keeps the translated rows whose identical English has two or more different
/// Polish texts (#1347).
/// </summary>
internal sealed class ListTranslations : IEndpoint
{
    /// <summary>
    /// <see cref="ListTranslationsRequest"/> already holds the page limits, and the endpoint is the only
    /// place that builds this query.
    /// </summary>
    internal sealed record Query(
        string? Search,
        TranslationStatus? Status,
        int Page,
        int PageSize,
        string? Sort,
        bool ConflictsOnly)
        : IQuery<Result<PaginationResponse<TranslationListItemResponse>>>, IPaginationable, ISortable;

    internal sealed class Handler : IQueryHandler<Query, Result<PaginationResponse<TranslationListItemResponse>>>
    {
        private readonly IApplicationReadDbContext _readDbContext;

        public Handler(IApplicationReadDbContext readDbContext)
        {
            _readDbContext = readDbContext;
        }

        public async ValueTask<Result<PaginationResponse<TranslationListItemResponse>>> Handle(
            Query query,
            CancellationToken cancellationToken)
        {
            IQueryable<TranslationReadModel> filtered = _readDbContext.Translations
                .Where(translation => translation.RemovedInVersion == null);

            if (query.Status is { } status)
            {
                filtered = filtered.Where(translation => translation.Status == status);
            }

            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                string term = query.Search.Trim();

                // LOTRO source text contains both % and _, so they must match themselves.
                string pattern = $"%{EscapeLike(term)}%";

                // Numbers appear in game texts too, so an id match only adds rows to the text match (#1406).
                // EF drops the comparisons whose value is null, so a text term keeps the plain text query.
                TranslationSearchIds ids = TranslationSearchTermParser.Parse(term);
                int? fileId = ids.FileId;
                long? gossipId = ids.GossipId;
                int? pairFileId = ids.PairFileId;
                long? pairGossipId = ids.PairGossipId;

                filtered = filtered.Where(translation =>
                    EF.Functions.ILike(translation.SourceText, pattern, LikeEscapeCharacter)
                    || translation.TranslatedText != null
                       && EF.Functions.ILike(translation.TranslatedText, pattern, LikeEscapeCharacter)
                    || fileId != null && translation.FileId == fileId
                    || gossipId != null && translation.GossipId == gossipId
                    || pairFileId != null
                       && pairGossipId != null
                       && translation.FileId == pairFileId
                       && translation.GossipId == pairGossipId);
            }

            if (query.ConflictsOnly)
            {
                filtered = WhereTheIdenticalEnglishHasDifferentPolish(filtered);
            }

            int totalCount = await filtered.CountAsync(cancellationToken);

            IQueryable<TranslationReadModel> ordered = string.IsNullOrWhiteSpace(query.Sort)
                ? filtered
                    .OrderBy(translation => translation.FileId)
                    .ThenBy(translation => translation.GossipId)
                : filtered.ApplyMultipleSorting(
                    query.Sort,
                    GetSortProperty,
                    translation => translation.FileId,
                    translation => translation.GossipId);

            List<TranslationListItemResponse> items = await ordered
                .ApplyPagination(query.Page, query.PageSize)
                .Select(TranslationProjections.ToListItem)
                .ToListAsync(cancellationToken);

            return Result.Success(new PaginationResponse<TranslationListItemResponse>
            {
                Items = items,
                Page = query.Page,
                PageSize = query.PageSize,
                TotalCount = totalCount
            });
        }

        /// <summary>
        /// An unknown key falls back to <c>FileId</c>, so a typo still returns a list. <c>submittedAt</c>
        /// means <c>UpdatedAt</c>. <c>status</c> sorts by the enum's name, because the column stores it.
        /// </summary>
        private static Expression<Func<TranslationReadModel, object>> GetSortProperty(string propertyName)
            => propertyName.ToLower(CultureInfo.InvariantCulture) switch
            {
                "fileid" => translation => translation.FileId,
                "gossipid" => translation => translation.GossipId,
                "status" => translation => translation.Status,
                "submittedat" => translation => translation.UpdatedAt,
                _ => translation => translation.FileId
            };

        /// <summary>
        /// One grouped subquery over the whole live table, so status and search never hide half of a group;
        /// a per-row "another row differs" check took 9 s on the production table (#1347). Untranslated
        /// rows are left out on both sides: they have nothing to fix and are never a different Polish.
        /// On top of one pass over the table, the cost grows with the number of translated rows.
        /// </summary>
        private IQueryable<TranslationReadModel> WhereTheIdenticalEnglishHasDifferentPolish(
            IQueryable<TranslationReadModel> filtered)
        {
            IQueryable<string> conflictingSourceTexts = _readDbContext.Translations
                .Where(translation => translation.RemovedInVersion == null && translation.TranslatedText != null)
                .GroupBy(translation => translation.SourceText)
                .Where(group => group.Select(translation => translation.TranslatedText).Distinct().Count() > 1)
                .Select(group => group.Key);

            return filtered.Where(translation =>
                translation.TranslatedText != null
                && conflictingSourceTexts.Contains(translation.SourceText));
        }

        private const string LikeEscapeCharacter = "\\";

        private static string EscapeLike(string term)
            => term
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_");
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("/api/v1/translations", async (
                [AsParameters] ListTranslationsRequest request,
                IQueryHandler<Query, Result<PaginationResponse<TranslationListItemResponse>>> handler,
                ITranslationAggregateLinkFactory translationLinkFactory,
                IPaginationLinkFactory paginationLinkFactory,
                ClaimsPrincipal user,
                CancellationToken cancellationToken) =>
            {
                Query query = new(
                    request.Search,
                    request.Status,
                    request.Page,
                    request.PageSize,
                    request.Sort,
                    request.ConflictsOnly);

                Result<PaginationResponse<TranslationListItemResponse>> result = await handler.Handle(query, cancellationToken);

                if (result.IsFailure)
                {
                    return Results.Problem(result.Error.ToProblemDetails());
                }

                PaginationResponse<TranslationListItemResponse> response = result.Value;
                bool callerIsAdmin = user.IsInRole(AuthConstants.Roles.Admin);
                bool callerIsTranslator = callerIsAdmin || user.IsInRole(AuthConstants.Roles.Translator);

                return HateoasResults.Ok(response, async paged =>
                {
                    foreach (TranslationListItemResponse item in paged.Items)
                    {
                        item.Links = await translationLinkFactory.CreateTranslationLinksAsync(
                            item.Id, item.Status, isRemoved: false, callerIsTranslator, callerIsAdmin);
                    }

                    paged.Links =
                    [
                        .. await paginationLinkFactory.CreatePaginationLinksAsync(
                            nameof(ListTranslations),
                            paged,
                            request.ToQueryParameters),
                        .. await translationLinkFactory.CreateCollectionLinksAsync(callerIsAdmin)
                    ];
                });
            })
            .WithName(nameof(ListTranslations))
            .WithTags("Translations")
            .AllowAnonymous()
            .Produces<PaginationResponse<TranslationListItemResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }
}
