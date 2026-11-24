using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using LotroKoniecDev.Hateoas.ContentNegotiation;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Auth.CurrentUserAccessing;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.API.Hateoas.PaginationLinkFactories;
using LotroKoniecDev.TranslationSystem.API.Hateoas.TranslationAggregateFactories;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// The editor's list (spec 0001), without soft-removed rows; <c>status=NeedsReview</c> is the "needs
/// retranslation" view. Anyone may read it (#309), because game texts and their translations are public
/// by nature; every action still needs a login, so other callers get no action links and no author
/// (#1543). <c>conflictsOnly</c> keeps the translated rows whose identical English has two or more
/// different Polish texts (#1347). <c>match</c> picks how the search term must match (#1477).
/// <c>mine</c> keeps the rows whose current Polish text the caller saved; the caller comes from the
/// token, and a caller with no token or no translator profile gets an empty page (#1491).
/// </summary>
internal sealed class ListTranslations : IEndpoint
{
    /// <summary>
    /// <see cref="ListTranslationsRequest"/> already holds the page limits, and the endpoint is the only
    /// place that builds this query. <see cref="Caller"/> is the token's subject, never a value from the
    /// address; without one, the items carry no author.
    /// </summary>
    internal sealed record Query(
        string? Search,
        TranslationStatus? Status,
        int Page,
        int PageSize,
        string? Sort,
        bool ConflictsOnly,
        TranslationSearchMatch Match,
        bool Mine,
        ValueMaybe<IdentityId> Caller)
        : IQuery<Result<PaginationResponse<TranslationListItemResponse>>>, IPaginationable, ISortable;

    internal sealed class Handler : IQueryHandler<Query, Result<PaginationResponse<TranslationListItemResponse>>>
    {
        private const string LikeEscapeCharacter = "\\";

        /// <summary>
        /// PostgreSQL regex syntax, not .NET's: Npgsql sends the pattern to PostgreSQL as <c>~*</c>. A word
        /// edge is a lookaround, because <c>\m</c> and <c>\M</c> never match a term that starts or ends
        /// with a non-letter, such as <c>Ring[E]</c>.
        /// </summary>
        private const string WordStart = "(?<![[:alnum:]_])";

        private const string WordEnd = "(?![[:alnum:]_])";

        private const string TextStart = "^\\s*";

        /// <summary>
        /// White space at the end, and one grammar mark such as <c>[E]</c> or <c>[mn]</c>: 1 to 4 ASCII
        /// letters. Longer bracket text is not a mark, so it must be typed.
        /// </summary>
        private const string TextEnd = "\\s*(\\[[A-Za-z]{1,4}\\])?\\s*$";

        private readonly IApplicationReadDbContext _readDbContext;

        public Handler(IApplicationReadDbContext readDbContext)
        {
            _readDbContext = readDbContext;
        }

        public async ValueTask<Result<PaginationResponse<TranslationListItemResponse>>> Handle(
            Query query,
            CancellationToken cancellationToken)
        {
            // Binding takes a number or a comma list too, so match=7 arrives here as an undefined value.
            if (!Enum.IsDefined(query.Match))
            {
                return Result.Failure<PaginationResponse<TranslationListItemResponse>>(new Error(
                    "Translations.UnknownSearchMatch",
                    $"Search match '{query.Match}' is not supported; use Contains, WholeWord or WholeText.",
                    TypeOfError.Validation));
            }

            IQueryable<TranslationReadModel> filtered = _readDbContext.Translations
                .Where(translation => translation.RemovedInVersion == null);

            if (query.Mine)
            {
                // A profile, not a role: an admin's own saves are theirs too.
                TranslatorId? callerTranslatorId = await FindCallerTranslatorIdAsync(query.Caller, cancellationToken);
                if (callerTranslatorId is not { } translatorId)
                {
                    return Result.Success(EmptyPage(query));
                }

                // Today the row keeps only the author of its current text. When every save becomes a
                // proposal (#1384), this filter and its order move to the newest proposal (spec 0014).
                filtered = filtered.Where(translation => translation.SubmittedById == translatorId);
            }

            if (query.Status is { } status)
            {
                filtered = filtered.Where(translation => translation.Status == status);
            }

            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                filtered = WhereTheSearchMatches(filtered, query.Search.Trim(), query.Match);
            }

            if (query.ConflictsOnly)
            {
                filtered = WhereTheIdenticalEnglishHasDifferentPolish(filtered);
            }

            int totalCount = await filtered.CountAsync(cancellationToken);

            List<TranslationListItemResponse> items = await Order(filtered, query)
                .ApplyPagination(query.Page, query.PageSize)
                .Select(query.Caller.HasNoValue
                    ? TranslationProjections.ToVisitorListItem
                    : TranslationProjections.ToListItem)
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
        /// An explicit sort always wins. Without one, "mine" shows the newest change first (#1491). That is
        /// <c>UpdatedAt</c>, which an approve, an import or a restore moves too: the row keeps no time of
        /// the save itself.
        /// </summary>
        private static IQueryable<TranslationReadModel> Order(IQueryable<TranslationReadModel> filtered, Query query)
        {
            if (!string.IsNullOrWhiteSpace(query.Sort))
            {
                return filtered.ApplyMultipleSorting(
                    query.Sort,
                    GetSortProperty,
                    translation => translation.FileId,
                    translation => translation.GossipId);
            }

            return query.Mine
                ? filtered
                    .OrderByDescending(translation => translation.UpdatedAt)
                    .ThenBy(translation => translation.FileId)
                    .ThenBy(translation => translation.GossipId)
                : filtered
                    .OrderBy(translation => translation.FileId)
                    .ThenBy(translation => translation.GossipId);
        }

        private static PaginationResponse<TranslationListItemResponse> EmptyPage(Query query) => new()
        {
            Items = [],
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = 0
        };

        private async Task<TranslatorId?> FindCallerTranslatorIdAsync(
            ValueMaybe<IdentityId> caller,
            CancellationToken cancellationToken)
        {
            if (caller.HasNoValue)
            {
                return null;
            }

            IdentityId identityId = caller.Value;
            return await _readDbContext.Translators
                .Where(profile => profile.IdentityId == identityId)
                .Select(profile => (TranslatorId?)profile.Id)
                .FirstOrDefaultAsync(cancellationToken);
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

        private static IQueryable<TranslationReadModel> WhereTheSearchMatches(
            IQueryable<TranslationReadModel> filtered,
            string term,
            TranslationSearchMatch match)
        {
            // Numbers appear in game texts too, so an id match only adds rows to the text match, in every mode
            // (#1406). EF drops the comparisons whose value is null, so a text term keeps the plain text query.
            TranslationSearchIds ids = TranslationSearchTermParser.Parse(term);
            int? fileId = ids.FileId;
            long? gossipId = ids.GossipId;
            int? pairFileId = ids.PairFileId;
            long? pairGossipId = ids.PairGossipId;

            if (match is TranslationSearchMatch.Contains)
            {
                // LOTRO source text contains both % and _, so they must match themselves.
                string likePattern = $"%{EscapeLike(term)}%";
                return filtered.Where(translation =>
                    EF.Functions.ILike(translation.SourceText, likePattern, LikeEscapeCharacter)
                    || translation.TranslatedText != null
                       && EF.Functions.ILike(translation.TranslatedText, likePattern, LikeEscapeCharacter)
                    || fileId != null && translation.FileId == fileId
                    || gossipId != null && translation.GossipId == gossipId
                    || pairFileId != null
                       && pairGossipId != null
                       && translation.FileId == pairFileId
                       && translation.GossipId == pairGossipId);
            }

            string escapedTerm = EscapeRegex(term);
            string regexPattern = match switch
            {
                TranslationSearchMatch.WholeWord => $"{WordStart}{escapedTerm}{WordEnd}",
                TranslationSearchMatch.WholeText => $"{TextStart}{escapedTerm}{TextEnd}",
                _ => throw new UnreachableException($"Search match '{match}' has no regex pattern.")
            };

            // The options stay a literal: Npgsql translates IsMatch only when they are a constant.
            return filtered.Where(translation =>
                Regex.IsMatch(translation.SourceText, regexPattern, RegexOptions.IgnoreCase)
                || translation.TranslatedText != null
                   && Regex.IsMatch(translation.TranslatedText, regexPattern, RegexOptions.IgnoreCase)
                || fileId != null && translation.FileId == fileId
                || gossipId != null && translation.GossipId == gossipId
                || pairFileId != null
                   && pairGossipId != null
                   && translation.FileId == pairFileId
                   && translation.GossipId == pairGossipId);
        }

        private static string EscapeLike(string term)
            => term
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_");

        /// <summary>
        /// In PostgreSQL's regex syntax a backslash before an ASCII character that is not a letter or a
        /// digit means that character itself, and every special character is ASCII. A backslash before a
        /// letter is an escape (<c>\d</c>), so letters and digits stay bare. Non-ASCII characters stay bare
        /// too: a backslash between the two halves of an emoji breaks the text, and Npgsql then fails the
        /// request.
        /// </summary>
        private static string EscapeRegex(string term)
        {
            StringBuilder escaped = new(term.Length * 2);
            foreach (char character in term)
            {
                if (char.IsAscii(character) && !char.IsAsciiLetterOrDigit(character))
                {
                    escaped.Append('\\');
                }

                escaped.Append(character);
            }

            return escaped.ToString();
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("/api/v1/translations", async (
                [AsParameters] ListTranslationsRequest request,
                IQueryHandler<Query, Result<PaginationResponse<TranslationListItemResponse>>> handler,
                ITranslationAggregateLinkFactory translationLinkFactory,
                IPaginationLinkFactory paginationLinkFactory,
                ICurrentUserAccessor currentUserAccessor,
                ClaimsPrincipal user,
                CancellationToken cancellationToken) =>
            {
                Query query = new(
                    request.Search,
                    request.Status,
                    request.Page,
                    request.PageSize,
                    request.Sort,
                    request.ConflictsOnly,
                    request.Match,
                    request.Mine,
                    currentUserAccessor.MaybeIdentityId);

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
