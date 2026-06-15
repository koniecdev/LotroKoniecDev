using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.Frontend.Components.Pages.Translations;

/// <summary>
/// The normalized filter/paging state of the translation list page, and the single place that turns it
/// into the relative URI the typed client calls (<c>GET /api/v1/translations</c>). Pure and isolated so
/// the search / status-filter / pagination wiring is unit-testable without rendering the component (the
/// Frontend has no bUnit) — the page reads it from the query string and hands it to the loader.
/// </summary>
internal sealed record TranslationListQuery
{
    /// <summary>The only language the catalog holds today; mirrors the API's single supported language.</summary>
    internal const string Language = "pl";

    /// <summary>Rows per page on the list view. Fixed for now — no user-facing page-size control (YAGNI).</summary>
    internal const int DefaultPageSize = 50;

    private const string ApiPath = "/api/v1/translations";
    private const string PagePath = "/translations";

    private TranslationListQuery(string? search, TranslationStatus? status, int page, int pageSize)
    {
        Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        Status = status;
        Page = Math.Max(page, 1);
        PageSize = Math.Clamp(pageSize, 1, 100);
    }

    public string? Search { get; }

    public TranslationStatus? Status { get; }

    public int Page { get; }

    public int PageSize { get; }

    /// <summary>
    /// Builds the normalized state from raw query-string inputs: a blank search collapses to
    /// <c>null</c>, an unknown or <see cref="TranslationStatus.Unset"/> status is ignored (treated as
    /// "all"), and the page is floored at 1.
    /// </summary>
    public static TranslationListQuery From(string? search, string? status, int page)
    {
        return new TranslationListQuery(search, ParseStatus(status), page, DefaultPageSize);
    }

    /// <summary>
    /// The relative URI for the API call: always carries <c>lang</c>, <c>page</c> and <c>pageSize</c>;
    /// adds <c>search</c> and <c>status</c> only when set, with values URL-encoded.
    /// </summary>
    public string ToApiRelativeUri()
    {
        Dictionary<string, string?> parameters = new()
        {
            ["lang"] = Language,
            ["pageSize"] = PageSize.ToString(CultureInfo.InvariantCulture)
        };
        AddPagingAndFilterParameters(parameters);

        return QueryHelpers.AddQueryString(ApiPath, parameters);
    }

    /// <summary>
    /// The relative URI for this page's own route (<c>/translations</c>) targeting
    /// <paramref name="page"/> — used to compose pager links. Unlike <see cref="ToApiRelativeUri"/> it
    /// omits the API-only <c>lang</c>/<c>pageSize</c> so the user-facing URL stays clean, while sharing
    /// the same encoding path for the <c>search</c>/<c>status</c> filter so the two can never drift.
    /// </summary>
    public string ToPageRelativeUri(int page)
    {
        Dictionary<string, string?> parameters = new();
        AddPagingAndFilterParameters(parameters, Math.Max(page, 1));

        return QueryHelpers.AddQueryString(PagePath, parameters);
    }

    private void AddPagingAndFilterParameters(Dictionary<string, string?> parameters, int? page = null)
    {
        parameters["page"] = (page ?? Page).ToString(CultureInfo.InvariantCulture);

        if (Search is not null)
        {
            parameters["search"] = Search;
        }

        if (Status is { } status)
        {
            parameters["status"] = status.ToString();
        }
    }

    private static TranslationStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        bool parsed = Enum.TryParse(status, ignoreCase: true, out TranslationStatus result);
        return parsed && result != TranslationStatus.Unset
            ? result
            : null;
    }
}
