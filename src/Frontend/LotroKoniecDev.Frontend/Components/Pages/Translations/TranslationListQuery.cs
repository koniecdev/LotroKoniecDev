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

    /// <summary>The rows-per-page used when the user has not picked one; must be one of <see cref="PageSizeOptions"/>.</summary>
    internal const int DefaultPageSize = 50;

    /// <summary>
    /// The user-selectable rows-per-page sizes offered by the list's page-size control (#323). Any other
    /// requested size (e.g. a hand-typed URL) falls back to <see cref="DefaultPageSize"/>, so the rendered
    /// dropdown always reflects the size actually in effect. Every value stays within the API's 1–100 clamp.
    /// </summary>
    internal static readonly IReadOnlyList<int> PageSizeOptions = [25, 50, 100];

    private const string ApiPath = "/api/v1/translations";
    private const string PagePath = "/translations";

    private TranslationListQuery(string? search, TranslationStatus? status, int page, int pageSize)
    {
        Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        Status = status;
        Page = Math.Max(page, 1);
        PageSize = PageSizeOptions.Contains(pageSize) ? pageSize : DefaultPageSize;
    }

    public string? Search { get; }

    public TranslationStatus? Status { get; }

    public int Page { get; }

    public int PageSize { get; }

    /// <summary>
    /// Builds the normalized state from raw query-string inputs: a blank search collapses to
    /// <c>null</c>, an unknown or <see cref="TranslationStatus.Unset"/> status is ignored (treated as
    /// "all"), the page is floored at 1, and an absent or unsupported <paramref name="pageSize"/> falls
    /// back to <see cref="DefaultPageSize"/>.
    /// </summary>
    public static TranslationListQuery From(string? search, string? status, int page, int? pageSize = null)
    {
        return new TranslationListQuery(search, ParseStatus(status), page, pageSize ?? DefaultPageSize);
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
    /// <paramref name="page"/> — used to compose pager links. It omits the API-only <c>lang</c> and the
    /// default <c>pageSize</c> so the user-facing URL stays clean, but carries a non-default page size so
    /// a chosen size survives paging (#323), while sharing the same encoding path for the
    /// <c>search</c>/<c>status</c> filter so the two can never drift.
    /// </summary>
    public string ToPageRelativeUri(int page)
    {
        Dictionary<string, string?> parameters = new();
        AddPagingAndFilterParameters(parameters, Math.Max(page, 1));
        AddNonDefaultPageSize(parameters);

        return QueryHelpers.AddQueryString(PagePath, parameters);
    }

    /// <summary>
    /// The page-relative URI for the Post-Redirect-Get target after a bulk approve (#322): this page's own
    /// route carrying the current filters and page, plus the <c>approved</c>/<c>skipped</c> result counts,
    /// so the confirmation flash survives the redirect while the active filters stay preserved.
    /// </summary>
    public string ToPageRelativeUriWithApprovalResult(int approved, int skipped)
    {
        Dictionary<string, string?> parameters = new();
        AddPagingAndFilterParameters(parameters);
        AddNonDefaultPageSize(parameters);
        parameters["approved"] = approved.ToString(CultureInfo.InvariantCulture);
        parameters["skipped"] = skipped.ToString(CultureInfo.InvariantCulture);

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

    /// <summary>
    /// Adds the chosen <c>pageSize</c> to a page-facing URL only when it differs from
    /// <see cref="DefaultPageSize"/>, so the selected size survives paging and the bulk-approve redirect
    /// while the default keeps the URL clean — mirroring how <c>search</c>/<c>status</c> appear only when set.
    /// </summary>
    private void AddNonDefaultPageSize(Dictionary<string, string?> parameters)
    {
        if (PageSize != DefaultPageSize)
        {
            parameters["pageSize"] = PageSize.ToString(CultureInfo.InvariantCulture);
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
