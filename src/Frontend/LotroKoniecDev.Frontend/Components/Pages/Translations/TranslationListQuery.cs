using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.Frontend.Components.Pages.Translations;

/// <summary>
/// The filter and paging state of the translation list page, and the one place that turns it into the
/// URI the typed client calls. The collection's own address is not known here: it is the
/// <c>translations</c> entry point read from the TMS service document (#610) and passed in, so this type
/// only adds the query string to it.
/// It sits in its own class so the search, the status filter and the paging can be unit-tested without
/// rendering the component. The page reads the state from the query string and hands it to the loader.
/// </summary>
internal sealed record TranslationListQuery
{
    /// <summary>The only language the catalog holds today, the same one the API supports.</summary>
    internal const string Language = "pl";

    /// <summary>The page size used when the user has not chosen one. It must be one of <see cref="PageSizeOptions"/>.</summary>
    internal const int DefaultPageSize = 50;

    /// <summary>
    /// The page sizes the list's size control offers (#323). Any other size, for example from a
    /// hand-typed URL, falls back to <see cref="DefaultPageSize"/>, so the dropdown always shows the size
    /// really in use. Every value stays inside the API's range of 1 to 100.
    /// </summary>
    internal static readonly IReadOnlyList<int> PageSizeOptions = [25, 50, 100];

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
    /// Builds the state from the raw query-string values. A blank search becomes <c>null</c>, an unknown
    /// status or <see cref="TranslationStatus.Unset"/> is ignored and means "all", the page is at least
    /// 1, and a missing or unsupported <paramref name="pageSize"/> falls back to
    /// <see cref="DefaultPageSize"/>.
    /// </summary>
    public static TranslationListQuery From(string? search, string? status, int page, int? pageSize = null)
    {
        return new TranslationListQuery(search, ParseStatus(status), page, pageSize ?? DefaultPageSize);
    }

    /// <summary>
    /// The URI to call: the server's <paramref name="collectionHref"/> plus this state's query string. It
    /// always carries <c>lang</c>, <c>page</c> and <c>pageSize</c>, and adds <c>search</c> and
    /// <c>status</c> only when they are set. All values are URL-encoded.
    /// </summary>
    public string ToApiUri(string collectionHref)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionHref);

        Dictionary<string, string?> parameters = new()
        {
            ["lang"] = Language,
            ["pageSize"] = PageSize.ToString(CultureInfo.InvariantCulture)
        };
        AddPagingAndFilterParameters(parameters);

        return QueryHelpers.AddQueryString(collectionHref, parameters);
    }

    /// <summary>
    /// The relative URI of this page's own route (<c>/translations</c>) for <paramref name="page"/>, used
    /// to build the pager links. It leaves out <c>lang</c>, which only the API needs, and the default
    /// <c>pageSize</c>, so the URL the user sees stays short. A page size the user chose is kept, so it
    /// survives paging (#323). The <c>search</c> and <c>status</c> filters go through the same encoding
    /// code as above, so the two can never differ.
    /// </summary>
    public string ToPageRelativeUri(int page)
    {
        Dictionary<string, string?> parameters = new();
        AddPagingAndFilterParameters(parameters, Math.Max(page, 1));
        AddNonDefaultPageSize(parameters);

        return QueryHelpers.AddQueryString(PagePath, parameters);
    }

    /// <summary>
    /// Where to redirect to after a bulk approve (#322): this page's own route with the current filters
    /// and page, plus the <c>approved</c> and <c>skipped</c> counts, so the confirmation message survives
    /// the redirect and the filters stay as they were.
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
    /// Adds the chosen <c>pageSize</c> to a user-facing URL only when it differs from
    /// <see cref="DefaultPageSize"/>, so a chosen size survives paging and the bulk-approve redirect while
    /// the default keeps the URL short. <c>search</c> and <c>status</c> work the same way.
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
