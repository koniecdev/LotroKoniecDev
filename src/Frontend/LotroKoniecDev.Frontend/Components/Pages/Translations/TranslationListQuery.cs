using System.Globalization;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace LotroKoniecDev.Frontend.Components.Pages.Translations;

/// <summary>
/// The filter and paging state of the translation list page. The collection's address comes from the TMS
/// service document (#610), so for the API this type only adds the query string. It also writes the
/// list's and the editor's own addresses and reads the state back from them (#1452). Kept apart from the
/// component so it can be unit-tested without rendering.
/// </summary>
internal sealed record TranslationListQuery
{
    /// <summary>Must be one of <see cref="PageSizeOptions"/>.</summary>
    internal const int DefaultPageSize = 50;

    /// <summary>The query names of the note after an inline save (#1493). The list page reads them.</summary>
    internal const string SavedRowName = "savedRow";

    internal const string SavedAfterName = "savedAfter";
    internal const string SavedSlipsName = "savedSlips";

    /// <summary>
    /// The row's place in the list, in the editor's address (#1490, ADR-0077). Only "Edytuj" writes it, and
    /// only an editor with a place offers "Zapisz i następny".
    /// </summary>
    internal const string PlaceName = "place";

    /// <summary>The list's note after "Zapisz i następny" passed the last row (#1490). The list page reads it.</summary>
    internal const string LastRowName = "lastRow";

    internal const string LastRowSavedValue = "saved";
    internal const string LastRowUnchangedValue = "unchanged";

    private const string PagePath = "/translations";
    private const string RowElementIdPrefix = "row-";
    private const string EditorPathPrefix = "/editor/";
    private const string PageName = "page";
    private const string SearchName = "search";
    private const string StatusName = "status";
    private const string ConflictsOnlyName = "conflictsOnly";
    private const string MatchName = "match";
    private const string MineName = "mine";
    private const string PageSizeName = "pageSize";

    /// <summary>
    /// Any other size, for example from a hand-typed URL, falls back to <see cref="DefaultPageSize"/>, so
    /// the dropdown always shows the size in use (#323). Every value stays inside the API's range of 1-100.
    /// </summary>
    internal static readonly IReadOnlyList<int> PageSizeOptions = [25, 50, 100];

    public string? Search { get; }

    public TranslationStatus? Status { get; }

    public int Page { get; }

    public int PageSize { get; }

    /// <summary>
    /// Only translated rows whose identical English has two or more different Polish texts (#1347).
    /// </summary>
    public bool ConflictsOnly { get; }

    /// <summary>How <see cref="Search"/> must match: anywhere, as a whole word or as the whole text (#1477).</summary>
    public TranslationSearchMatch Match { get; }

    /// <summary>
    /// Only the rows whose current Polish text the signed-in user saved (#1491). The page turns it off for a
    /// visitor, so a visitor's list never sends it.
    /// </summary>
    public bool Mine { get; }

    /// <summary>
    /// Only <c>true</c>, in any case, turns the filter on. Any other value means off, as an unknown status
    /// means "all".
    /// </summary>
    public static bool ParseConflictsOnly(string? conflictsOnly) =>
        bool.TryParse(conflictsOnly, out bool isOn) && isOn;

    /// <summary>The same rule as <see cref="ParseConflictsOnly"/>.</summary>
    public static bool ParseMine(string? mine) => ParseConflictsOnly(mine);

    /// <summary>
    /// A mode's name, in any case. Anything else, a number included, means
    /// <see cref="TranslationSearchMatch.Contains"/>, as an unknown status means "all".
    /// </summary>
    public static TranslationSearchMatch ParseMatch(string? match)
    {
        if (string.IsNullOrWhiteSpace(match))
        {
            return TranslationSearchMatch.Contains;
        }

        string trimmed = match.Trim();
        return Enum.GetValues<TranslationSearchMatch>()
            .FirstOrDefault(
                mode => string.Equals(mode.ToString(), trimmed, StringComparison.OrdinalIgnoreCase),
                TranslationSearchMatch.Contains);
    }

    /// <summary>
    /// With <paramref name="after"/>, the API returns the rows after that place instead of this page, and
    /// says which page they start on (ADR-0077).
    /// </summary>
    public ListTranslationsRequest ToApiRequest(string? after = null)
        => new(
            Search: Search,
            Status: Status,
            Page: Page,
            PageSize: PageSize,
            ConflictsOnly: ConflictsOnly,
            Match: Match,
            Mine: Mine,
            After: after);

    /// <summary>
    /// The API's option names come from <see cref="ListTranslationsRequest"/>, the record the API binds,
    /// so the two ends cannot spell them differently (#1478).
    /// </summary>
    public string ToApiUri(string collectionHref, string? after = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionHref);

        return QueryHelpers.AddQueryString(collectionHref, ToApiRequest(after).ToQueryParameters());
    }

    /// <summary>The same list on another page, for the row "Zapisz i następny" opens (#1490).</summary>
    public TranslationListQuery AtPage(int page) =>
        new(Search, Status, page, PageSize, ConflictsOnly, Match, Mine);

    /// <summary>
    /// For the pager links. The page's own address is the website's contract with the browser, so it keeps
    /// its own names. It leaves out the default <c>pageSize</c>, so the URL the user sees stays short (#323).
    /// </summary>
    public string ToPageRelativeUri(int page) =>
        QueryHelpers.AddQueryString(PagePath, PageQueryParameters(Math.Max(page, 1)));

    /// <summary>
    /// The redirect after a bulk approve (#322). The counts ride in the query string, so the confirmation
    /// survives the redirect.
    /// </summary>
    public string ToPageRelativeUriWithApprovalResult(int approved, int skipped)
    {
        Dictionary<string, string?> parameters = PageQueryParameters(Page);
        parameters["approved"] = approved.ToString(CultureInfo.InvariantCulture);
        parameters["skipped"] = skipped.ToString(CultureInfo.InvariantCulture);

        return QueryHelpers.AddQueryString(PagePath, parameters);
    }

    /// <summary>
    /// The editor's address carries the list state, so the editor can build the way back (#1452). Default
    /// values stay out, so the first page of an unfiltered list links to a bare <c>/editor/{id}</c>. The
    /// <paramref name="place"/> is the row's place in this list (#1490); a link from outside the list has none.
    /// </summary>
    public string ToEditorRelativeUri(TranslationId id, string? place = null)
    {
        Dictionary<string, string?> parameters = PageQueryParameters(PageUnlessFirst());
        if (!string.IsNullOrWhiteSpace(place))
        {
            parameters[PlaceName] = place;
        }

        return QueryHelpers.AddQueryString(
            string.Create(CultureInfo.InvariantCulture, $"{EditorPathPrefix}{id.Value}"),
            parameters);
    }

    /// <summary>
    /// The editor's way back to the list (#1452). The path is always the list page, and every value is
    /// encoded into the query string, so no value can make the link leave the site.
    /// </summary>
    public string ToListRelativeUri() =>
        QueryHelpers.AddQueryString(PagePath, PageQueryParameters(PageUnlessFirst()));

    /// <summary>
    /// Where "Zapisz i następny" ends after the last row (#1490). The note says whether that press saved.
    /// </summary>
    public string ToListRelativeUriWithLastRowNote(bool saved)
    {
        Dictionary<string, string?> parameters = PageQueryParameters(PageUnlessFirst());
        parameters[LastRowName] = saved ? LastRowSavedValue : LastRowUnchangedValue;

        return QueryHelpers.AddQueryString(PagePath, parameters);
    }

    /// <summary>
    /// Where a row's own save form posts (#1493). The fragment opens a refused save at its row. A bare
    /// fragment would post to the site root, because the layout sets <c>&lt;base href="/"&gt;</c>.
    /// </summary>
    public string ToInlineSaveActionUri(TranslationId id) =>
        string.Create(CultureInfo.InvariantCulture, $"{ToPageRelativeUri(Page)}#{RowElementId(id)}");

    /// <summary>
    /// The redirect after a good inline save (#1493). The note rides in the query string, as the bulk
    /// approve counts do (#322). <paramref name="savedAfter"/> is the row above the saved one, so the
    /// note can stand in its place when the save took the row off this page.
    /// </summary>
    public string ToPageRelativeUriWithSavedRow(TranslationId savedRow, TranslationId? savedAfter, bool hasSlips)
    {
        Dictionary<string, string?> parameters = PageQueryParameters(Page);
        parameters[SavedRowName] = savedRow.Value.ToString("D", CultureInfo.InvariantCulture);
        if (savedAfter is { } rowAbove)
        {
            parameters[SavedAfterName] = rowAbove.Value.ToString("D", CultureInfo.InvariantCulture);
        }

        if (hasSlips)
        {
            parameters[SavedSlipsName] = "true";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{QueryHelpers.AddQueryString(PagePath, parameters)}#{RowElementId(savedRow)}");
    }

    /// <summary>The HTML id of a row on the list page, which the fragments above point at.</summary>
    public static string RowElementId(TranslationId id) =>
        string.Create(CultureInfo.InvariantCulture, $"{RowElementIdPrefix}{id.Value:D}");

    /// <summary>
    /// An unknown status or <see cref="TranslationStatus.Unset"/> means "all".
    /// </summary>
    public static TranslationListQuery From(
        string? search,
        string? status,
        int page,
        int? pageSize = null,
        bool conflictsOnly = false,
        TranslationSearchMatch match = TranslationSearchMatch.Contains,
        bool mine = false)
    {
        return new TranslationListQuery(
            search,
            ParseStatus(status),
            page,
            pageSize ?? DefaultPageSize,
            conflictsOnly,
            match,
            mine);
    }

    /// <summary>
    /// Reads the list state that <see cref="ToEditorRelativeUri"/> wrote into a page address (#1452). Every
    /// field is read as text, so a value that cannot be read falls back to its default instead of failing
    /// the page, and nothing but the list's own fields is read.
    /// </summary>
    public static TranslationListQuery FromPageQuery(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        Dictionary<string, StringValues> query = QueryHelpers.ParseQuery(QueryPart(uri));

        return new TranslationListQuery(
            FirstValue(query, SearchName),
            ParseStatus(FirstValue(query, StatusName)),
            ParsePage(FirstValue(query, PageName)),
            ParsePageSize(FirstValue(query, PageSizeName)),
            ParseConflictsOnly(FirstValue(query, ConflictsOnlyName)),
            ParseMatch(FirstValue(query, MatchName)),
            ParseMine(FirstValue(query, MineName)));
    }

    private TranslationListQuery(
        string? search,
        TranslationStatus? status,
        int page,
        int pageSize,
        bool conflictsOnly,
        TranslationSearchMatch match,
        bool mine)
    {
        Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        Status = status;
        Page = Math.Max(page, 1);
        PageSize = PageSizeOptions.Contains(pageSize) ? pageSize : DefaultPageSize;
        ConflictsOnly = conflictsOnly;
        Match = Enum.IsDefined(match) ? match : TranslationSearchMatch.Contains;
        Mine = mine;
    }

    /// <summary>
    /// The one writer of every page address, so a new list field reaches all of them. A null
    /// <paramref name="page"/> leaves the page out.
    /// </summary>
    private Dictionary<string, string?> PageQueryParameters(int? page)
    {
        Dictionary<string, string?> parameters = new();

        if (page is { } pageNumber)
        {
            parameters[PageName] = pageNumber.ToString(CultureInfo.InvariantCulture);
        }

        if (Search is not null)
        {
            parameters[SearchName] = Search;
        }

        if (Match is not TranslationSearchMatch.Contains)
        {
            parameters[MatchName] = Match.ToString();
        }

        if (Status is { } status)
        {
            parameters[StatusName] = status.ToString();
        }

        if (ConflictsOnly)
        {
            parameters[ConflictsOnlyName] = "true";
        }

        if (Mine)
        {
            parameters[MineName] = "true";
        }

        if (PageSize != DefaultPageSize)
        {
            parameters[PageSizeName] = PageSize.ToString(CultureInfo.InvariantCulture);
        }

        return parameters;
    }

    private int? PageUnlessFirst() => Page > 1 ? Page : null;

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

    /// <summary>
    /// The number rules of the list's own query binding. Where that binding fails the page on a value it
    /// cannot read (#1356), this falls back to the first page.
    /// </summary>
    private static int ParsePage(string? page) =>
        int.TryParse(page, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 1;

    /// <summary>
    /// A size that cannot be read is the default size, as an unsupported number is.
    /// </summary>
    private static int ParsePageSize(string? pageSize) =>
        int.TryParse(pageSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : DefaultPageSize;

    /// <summary>
    /// The query part of an absolute or a relative address. <see cref="Uri.Query"/> cannot read a relative
    /// one, and <see cref="QueryHelpers.ParseQuery"/> would keep a fragment.
    /// </summary>
    private static string QueryPart(string uri)
    {
        int fragmentStart = uri.IndexOf('#', StringComparison.Ordinal);
        string withoutFragment = fragmentStart < 0 ? uri : uri[..fragmentStart];
        int queryStart = withoutFragment.IndexOf('?', StringComparison.Ordinal);

        return queryStart < 0 ? string.Empty : withoutFragment[queryStart..];
    }

    /// <summary>
    /// The first value of a repeated name, as Blazor's query binding reads it. Joining the values would
    /// differ from the list: <c>page=2&amp;page=3</c> would read as "2,3", which is no number.
    /// </summary>
    private static string? FirstValue(Dictionary<string, StringValues> query, string name) =>
        query.TryGetValue(name, out StringValues values) && values.Count > 0 ? values[0] : null;
}
