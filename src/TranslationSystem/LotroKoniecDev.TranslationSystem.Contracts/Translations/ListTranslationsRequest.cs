using System.Globalization;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// The list's query options. The API binds this record and every client builds the address from it, so
/// each option name is written once, as a property name here (#1478). <see cref="Status"/> null means
/// every status; <see cref="ConflictsOnly"/> keeps the rows whose identical English has different Polish
/// (#1347).
/// </summary>
public sealed record ListTranslationsRequest(
    string? Search = null,
    TranslationStatus? Status = null,
    int Page = ListTranslationsRequest.FirstPage,
    int PageSize = ListTranslationsRequest.DefaultPageSize,
    string? Sort = null,
    bool ConflictsOnly = false)
{
    public const int FirstPage = 1;
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public int Page { get; } = Math.Max(Page, FirstPage);

    public int PageSize { get; } = Math.Clamp(PageSize, 1, MaxPageSize);

    public IReadOnlyList<KeyValuePair<string, string?>> ToQueryParameters() => ToQueryParameters(Page);

    /// <summary>
    /// The same options for another page; the paging links use it. A blank or default filter is left
    /// out. The page and the page size are always written, last, so every paging link keeps one shape.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string?>> ToQueryParameters(int page)
    {
        List<KeyValuePair<string, string?>> parameters = [];

        if (!string.IsNullOrWhiteSpace(Search))
        {
            parameters.Add(QueryParameter.Of(nameof(Search), Search));
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

        parameters.Add(QueryParameter.Of(nameof(Page), page.ToString(CultureInfo.InvariantCulture)));
        parameters.Add(QueryParameter.Of(nameof(PageSize), PageSize.ToString(CultureInfo.InvariantCulture)));

        return parameters;
    }
}
