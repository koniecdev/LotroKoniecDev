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
/// Lists translations for the editor (spec 0001): paginated, optionally text-searched (English
/// source or Polish translation) and status-filtered, sorted deterministically by
/// <c>(FileId, GossipId)</c>. Soft-removed rows are excluded; <c>status=NeedsReview</c> is the
/// "needs re-translation" view (rows a game update invalidated). Reads the POCO read model — never
/// the write aggregate (CQRS, ADR-0002 amendment).
/// </summary>
internal sealed class ListTranslations : IEndpoint
{
    /// <summary>The only language the catalog holds today; multi-language is post-MVP.</summary>
    private const string SupportedLanguage = SupportedLanguages.Polish;

    internal sealed record Query(string? Lang, string? Search, TranslationStatus? Status, int Page = 1, int PageSize = 50)
        : IQuery<Result<PaginationResponse<TranslationListItemResponse>>>
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
            // Queries validate inline (house rule — FluentValidation is for commands only).
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
                // ILIKE is case-insensitive; escape LIKE metacharacters so a literal % or _ in the
                // term (LOTRO source carries both) matches literally instead of as a wildcard.
                string pattern = $"%{EscapeLike(query.Search.Trim())}%";
                filtered = filtered.Where(translation =>
                    EF.Functions.ILike(translation.SourceText, pattern, LikeEscapeCharacter)
                    || translation.TranslatedText != null
                       && EF.Functions.ILike(translation.TranslatedText, pattern, LikeEscapeCharacter));
            }

            int totalCount = await filtered.CountAsync(cancellationToken);

            List<TranslationListItemResponse> items = await filtered
                .OrderBy(translation => translation.FileId)
                .ThenBy(translation => translation.GossipId)
                .Skip((query.Page - 1) * query.PageSize)
                .Take(query.PageSize)
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

        private const string LikeEscapeCharacter = "\\";

        /// <summary>Escapes the LIKE/ILIKE metacharacters so the search term matches literally.</summary>
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
                [FromQuery] int pageSize = 50) =>
            {
                Query query = new(lang, search, status, page, pageSize);

                Result<PaginationResponse<TranslationListItemResponse>> result = await handler.Handle(query, cancellationToken);

                if (result.IsFailure)
                {
                    return Results.Problem(result.Error.ToProblemDetails());
                }

                PaginationResponse<TranslationListItemResponse> response = result.Value;
                bool callerIsAdmin = user.IsInRole(AuthConstants.Roles.Admin);

                return HateoasResults.Ok(response, paged =>
                {
                    foreach (TranslationListItemResponse item in paged.Items)
                    {
                        item.Links = translationLinkFactory.CreateTranslationLinks(
                            item.Id, item.Status, isRemoved: false, callerIsAdmin);
                    }

                    paged.Links = paginationLinkFactory.CreatePaginationLinks(
                        nameof(ListTranslations), paged, new { lang, search, status });
                });
            })
            .WithName(nameof(ListTranslations))
            .WithTags("Translations")
            .RequireAuthorization(AuthorizationPolicies.RequireTranslatorRole)
            .Produces<PaginationResponse<TranslationListItemResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }
}
