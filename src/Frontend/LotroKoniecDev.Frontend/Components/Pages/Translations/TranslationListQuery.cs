using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.Frontend.Components.Pages.Translations;

/// <summary>
/// The filter and paging state of the translation list page. The collection's address comes from the TMS
/// service document (#610), so this type only adds the query string to it. Kept apart from the component
/// so it can be unit-tested without rendering.
/// </summary>
internal sealed record TranslationListQuery
{
    /// <summary>The only language the API supports today.</summary>
    internal const string Language = "pl";

    /// <summary>Must be one of <see cref="PageSizeOptions"/>.</summary>
    internal const int DefaultPageSize = 50;

    /// <summary>
    /// Any other size, for example from a hand-typed URL, falls back to <see cref="DefaultPageSize"/>, so
    /// the dropdown always shows the size in use (#323). Every value stays inside the API's range of 1-100.
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
    /// An unknown status or <see cref="TranslationStatus.Unset"/> means "all".
    /// </summary>
    public static TranslationListQuery From(string? search, string? status, int page, int? pageSize = null)
    {
        return new TranslationListQuery(search, ParseStatus(status), page, pageSize ?? DefaultPageSize);
    }

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
    /// For the pager links. It leaves out <c>lang</c>, which only the API needs, and the default
    /// <c>pageSize</c>, so the URL the user sees stays short (#323).
    /// </summary>
    public string ToPageRelativeUri(int page)
    {
        Dictionary<string, string?> parameters = new();
        AddPagingAndFilterParameters(parameters, Math.Max(page, 1));
        AddNonDefaultPageSize(parameters);

        return QueryHelpers.AddQueryString(PagePath, parameters);
    }

    /// <summary>
    /// The redirect after a bulk approve (#322). The counts ride in the query string, so the confirmation
    /// survives the redirect.
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
