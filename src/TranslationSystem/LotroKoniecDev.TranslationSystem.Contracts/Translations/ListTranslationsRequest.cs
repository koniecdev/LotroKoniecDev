using System.Globalization;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// The list's query options. The API binds this record and every client builds the address from it, so
/// each option name is written once, as a property name here (#1478). <see cref="Status"/> null means
/// every status; <see cref="ConflictsOnly"/> keeps the rows whose identical English has different Polish
/// (#1347); <see cref="Match"/> says how <see cref="Search"/> must match (#1477); <see cref="Mine"/> keeps
/// the rows whose current Polish text the caller saved, newest change first when no sort is given (#1491).
/// <see cref="After"/> is a row's <see cref="TranslationListItemResponse.Place"/>: the API then returns the
/// rows after that row and says which page they start on (ADR-0077, #1490).
/// </summary>
public sealed record ListTranslationsRequest(
    string? Search = null,
    TranslationStatus? Status = null,
    int Page = ListTranslationsRequest.FirstPage,
    int PageSize = ListTranslationsRequest.DefaultPageSize,
    string? Sort = null,
    bool ConflictsOnly = false,
    TranslationSearchMatch Match = TranslationSearchMatch.Contains,
    bool Mine = false,
    string? After = null)
{
    public const int FirstPage = 1;
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public int Page { get; } = Math.Max(Page, FirstPage);

    public int PageSize { get; } = Math.Clamp(PageSize, 1, MaxPageSize);

    public IReadOnlyList<KeyValuePair<string, string?>> ToQueryParameters() => QueryParameters(Page, After);

    /// <summary>
    /// The same options for another page; the paging links use it. A blank or default filter is left
    /// out. The page and the page size are always written, last, so every paging link keeps one shape.
    /// It never writes <see cref="After"/>: a numbered page starts at its own first row.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string?>> ToQueryParameters(int page) => QueryParameters(page, after: null);

    private List<KeyValuePair<string, string?>> QueryParameters(int page, string? after)
    {
        List<KeyValuePair<string, string?>> parameters = [];

        if (!string.IsNullOrWhiteSpace(Search))
        {
            parameters.Add(QueryParameter.Of(nameof(Search), Search));
        }

        if (Match is not TranslationSearchMatch.Contains)
        {
            parameters.Add(QueryParameter.Of(nameof(Match), Match.ToString()));
        }

        if (Status is { } status)
        {
            parameters.Add(QueryParameter.Of(nameof(Status), status.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(Sort))
        {
            parameters.Add(QueryParameter.Of(nameof(Sort), Sort));
        }

        if (ConflictsOnly)
        {
            parameters.Add(QueryParameter.Of(nameof(ConflictsOnly), "true"));
        }

        if (Mine)
        {
            parameters.Add(QueryParameter.Of(nameof(Mine), "true"));
        }

        if (!string.IsNullOrWhiteSpace(after))
        {
            parameters.Add(QueryParameter.Of(nameof(After), after));
        }

        parameters.Add(QueryParameter.Of(nameof(Page), page.ToString(CultureInfo.InvariantCulture)));
        parameters.Add(QueryParameter.Of(nameof(PageSize), PageSize.ToString(CultureInfo.InvariantCulture)));

        return parameters;
    }
}
