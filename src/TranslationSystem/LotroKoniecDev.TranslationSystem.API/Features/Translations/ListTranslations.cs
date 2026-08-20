using System.Globalization;
using System.Linq.Expressions;
using System.Security.Claims;
using LotroKoniecDev.Hateoas.ContentNegotiation;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
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
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// Lists translations for the editor (spec 0001): paged, with an optional text search over the English
/// source or the Polish translation, an optional status filter, and always the same order by
/// <c>(FileId, GossipId)</c>. Soft-removed rows are left out, and <c>status=NeedsReview</c> is the
/// "needs retranslation" view, the rows a game update invalidated.
/// It reads the read model and never the write aggregate (CQRS, ADR-0002 amendment).
/// Anyone may read the list (#309), because the data is public by nature: game texts and their
/// translations. Every action still needs a login, so a caller who is not a translator gets items with
/// no action links.
/// </summary>
internal sealed class ListTranslations : IEndpoint
{
    /// <summary>The only language the catalog holds today. More languages are post-MVP.</summary>
    private const string SupportedLanguage = SupportedLanguages.Polish;

    internal sealed record Query(
        string? Lang,
        string? Search,
        TranslationStatus? Status,
        int Page = 1,
        int PageSize = 50,
        string? Sort = null)
        : IQuery<Result<PaginationResponse<TranslationListItemResponse>>>, IPaginationable, ISortable
    {
        public int Page { get; } = Math.Max(Page, 1);
        public int PageSize { get; } = Math.Clamp(PageSize, 1, 100);
    }

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
            // Queries validate here in the handler. FluentValidation is for commands only (house rule).
            if (!string.IsNullOrWhiteSpace(query.Lang)
                && !string.Equals(query.Lang, SupportedLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure<PaginationResponse<TranslationListItemResponse>>(new Error(
                    "Translations.UnsupportedLanguage",
                    $"Language '{query.Lang}' is not supported; only '{SupportedLanguage}' exists today.",
                    TypeOfError.Validation));
            }

            IQueryable<TranslationReadModel> filtered = _readDbContext.Translations
                .Where(translation => translation.RemovedInVersion == null);

            if (query.Status is { } status)
            {
                filtered = filtered.Where(translation => translation.Status == status);
            }

            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                // ILIKE ignores case. Escape the LIKE special characters, so a % or _ in the term
                // matches itself instead of acting as a wildcard. LOTRO source text contains both.
                string pattern = $"%{EscapeLike(query.Search.Trim())}%";
                filtered = filtered.Where(translation =>
                    EF.Functions.ILike(translation.SourceText, pattern, LikeEscapeCharacter)
                    || translation.TranslatedText != null
                       && EF.Functions.ILike(translation.TranslatedText, pattern, LikeEscapeCharacter));
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
        /// Maps a <c>?sort=</c> key to the read-model column it orders by. An unknown key falls back to
        /// <c>FileId</c> ascending, the first part of the default order, so a typo still returns a list
        /// instead of an error.
        /// <c>submittedAt</c> means <c>UpdatedAt</c>, the time of the last submission the row shows.
        /// <c>status</c> sorts by the enum's name, because the column stores the name, not the number.
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

        private const string LikeEscapeCharacter = "\\";

        /// <summary>Escapes the LIKE and ILIKE special characters, so the search term matches itself.</summary>
        private static string EscapeLike(string term)
            => term
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_");
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("/api/v1/translations", async (
                IQueryHandler<Query, Result<PaginationResponse<TranslationListItemResponse>>> handler,
                ITranslationAggregateLinkFactory translationLinkFactory,
                IPaginationLinkFactory paginationLinkFactory,
                ClaimsPrincipal user,
                CancellationToken cancellationToken,
                [FromQuery] string? lang = null,
                [FromQuery] string? search = null,
                [FromQuery] TranslationStatus? status = null,
                [FromQuery] int page = 1,
                [FromQuery] int pageSize = 50,
                [FromQuery] string? sort = null) =>
            {
                Query query = new(lang, search, status, page, pageSize, sort);

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
                            nameof(ListTranslations), paged, new { lang, search, status, sort }),
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
