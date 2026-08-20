using LotroKoniecDev.Frontend.Components.Pages.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Translations;

public sealed class TranslationListQueryTests
{
    /// <summary>
    /// Stands in for the <c>translations</c> href the TMS service document sends (#610). It looks nothing
    /// like the real route on purpose: this type may only add a query string to whatever the server gave
    /// it and must never know an API path of its own.
    /// </summary>
    private const string CollectionHref = "/resolved-by-discovery/translations";

    [Fact]
    public void From_WithNoFilters_BuildsBaseUriWithLangAndDefaultPaging()
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: null, page: 1);

        string uri = query.ToApiUri(CollectionHref);

        uri.ShouldStartWith($"{CollectionHref}?");
        uri.ShouldContain("lang=pl");
        uri.ShouldContain("page=1");
        uri.ShouldContain($"pageSize={TranslationListQuery.DefaultPageSize}");
        uri.ShouldNotContain("search=");
        uri.ShouldNotContain("status=");
    }

    [Fact]
    public void From_WithSearchTerm_IncludesEncodedSearchParameter()
    {
        TranslationListQuery query = TranslationListQuery.From(search: "Witaj w Śródziemiu", status: null, page: 1);

        query.ToApiUri(CollectionHref).ShouldContain("search=Witaj%20w%20%C5%9Ar%C3%B3dziemiu");
    }

    [Fact]
    public void From_WithSearchContainingLikeMetacharacters_EncodesThemForTransport()
    {
        TranslationListQuery query = TranslationListQuery.From(search: "100% & <tag>", status: null, page: 1);

        // The page only URL-encodes; the API escapes LIKE metacharacters server-side.
        string uri = query.ToApiUri(CollectionHref);
        uri.ShouldContain("search=100%25%20%26%20%3Ctag%3E");
    }

    [Fact]
    public void From_WithStatus_IncludesStatusByEnumName()
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: "NeedsReview", page: 1);

        query.ToApiUri(CollectionHref).ShouldContain("status=NeedsReview");
        query.Status.ShouldBe(TranslationStatus.NeedsReview);
    }

    [Theory]
    [InlineData("draft", TranslationStatus.Draft)]
    [InlineData("APPROVED", TranslationStatus.Approved)]
    [InlineData("NeEdSrEvIeW", TranslationStatus.NeedsReview)]
    public void From_WithStatusInAnyCase_ParsesCaseInsensitively(string status, TranslationStatus expected)
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: status, page: 1);

        query.Status.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NotAStatus")]
    [InlineData("Unset")]
    public void From_WithBlankOrUnknownOrSentinelStatus_TreatsAsAllAndOmitsStatus(string? status)
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: status, page: 1);

        query.Status.ShouldBeNull();
        query.ToApiUri(CollectionHref).ShouldNotContain("status=");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void From_WithBlankSearch_OmitsSearchAndNormalizesToNull(string? search)
    {
        TranslationListQuery query = TranslationListQuery.From(search: search, status: null, page: 1);

        query.Search.ShouldBeNull();
        query.ToApiUri(CollectionHref).ShouldNotContain("search=");
    }

    [Fact]
    public void From_WithPaddedSearch_TrimsBeforeUse()
    {
        TranslationListQuery query = TranslationListQuery.From(search: "  Frodo  ", status: null, page: 1);

        query.Search.ShouldBe("Frodo");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    public void From_FloorsPageAtOne(int requested, int expected)
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: null, page: requested);

        query.Page.ShouldBe(expected);
        query.ToApiUri(CollectionHref).ShouldContain($"page={expected}");
    }

    [Fact]
    public void ToApiUri_WithAllFilters_ComposesEveryParameter()
    {
        TranslationListQuery query = TranslationListQuery.From(search: "Gandalf", status: "Approved", page: 2);

        string uri = query.ToApiUri(CollectionHref);

        uri.ShouldStartWith($"{CollectionHref}?");
        uri.ShouldContain("lang=pl");
        uri.ShouldContain("page=2");
        uri.ShouldContain($"pageSize={TranslationListQuery.DefaultPageSize}");
        uri.ShouldContain("search=Gandalf");
        uri.ShouldContain("status=Approved");
    }

    [Fact]
    public void ToPageRelativeUri_TargetsThePageRouteAndOmitsTheApiOnlyParameters()
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: null, page: 1);

        string uri = query.ToPageRelativeUri(3);

        uri.ShouldStartWith("/translations?");
        uri.ShouldContain("page=3");
        uri.ShouldNotContain("lang=");
        uri.ShouldNotContain("pageSize=");
    }

    [Fact]
    public void ToPageRelativeUri_PreservesTheActiveSearchAndStatusFilter()
    {
        TranslationListQuery query = TranslationListQuery.From(search: "Bilbo", status: "Draft", page: 1);

        string uri = query.ToPageRelativeUri(3);

        uri.ShouldContain("page=3");
        uri.ShouldContain("search=Bilbo");
        uri.ShouldContain("status=Draft");
    }

    [Fact]
    public void ToPageRelativeUri_EncodesTheSearchFilterTheSameWayAsTheApiUri()
    {
        TranslationListQuery query = TranslationListQuery.From(search: "100% & <tag>", status: null, page: 1);

        query.ToPageRelativeUri(2).ShouldContain("search=100%25%20%26%20%3Ctag%3E");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(5, 5)]
    public void ToPageRelativeUri_FloorsTheTargetPageAtOne(int requested, int expected)
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: null, page: 1);

        query.ToPageRelativeUri(requested).ShouldContain($"page={expected}");
    }

    [Fact]
    public void ToPageRelativeUriWithApprovalResult_PreservesTheActiveFiltersAndPageAndAppendsTheCounts()
    {
        // Acceptance criterion 7 (#322): after a bulk approve the redirect must land back on the current
        // filtered page with the search and status still set, plus the approved and skipped counts. A
        // reload is then a plain GET that shows the same filtered list with the confirmation message.
        TranslationListQuery query = TranslationListQuery.From(search: "Bilbo", status: "Draft", page: 3);

        string uri = query.ToPageRelativeUriWithApprovalResult(approved: 2, skipped: 1);

        uri.ShouldStartWith("/translations?");
        uri.ShouldContain("page=3");
        uri.ShouldContain("search=Bilbo");
        uri.ShouldContain("status=Draft");
        uri.ShouldContain("approved=2");
        uri.ShouldContain("skipped=1");
    }

    [Fact]
    public void From_WithoutPageSize_DefaultsToDefaultPageSize()
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: null, page: 1);

        query.PageSize.ShouldBe(TranslationListQuery.DefaultPageSize);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(100)]
    public void From_WithSupportedPageSize_UsesItAndSendsItToTheApi(int pageSize)
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: null, page: 1, pageSize: pageSize);

        query.PageSize.ShouldBe(pageSize);
        query.ToApiUri(CollectionHref).ShouldContain($"pageSize={pageSize}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(7)]
    [InlineData(99)]
    [InlineData(101)]
    [InlineData(1000)]
    public void From_WithUnsupportedPageSize_SnapsBackToTheDefault(int pageSize)
    {
        // The list falls back to the default for any size that is not on its own list, unlike the API,
        // which simply clamps to 1 to 100. So the dropdown can always mark one of its real options as
        // selected (#323).
        TranslationListQuery query = TranslationListQuery.From(search: null, status: null, page: 1, pageSize: pageSize);

        query.PageSize.ShouldBe(TranslationListQuery.DefaultPageSize);
    }

    [Fact]
    public void PageSizeOptions_IncludeTheDefault_SoTheDropdownAlwaysHasASelectedOption()
    {
        TranslationListQuery.PageSizeOptions.ShouldContain(TranslationListQuery.DefaultPageSize);
    }

    [Fact]
    public void ToPageRelativeUri_WithNonDefaultPageSize_PreservesItAcrossPaging()
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: null, page: 1, pageSize: 100);

        string uri = query.ToPageRelativeUri(3);

        uri.ShouldContain("page=3");
        uri.ShouldContain("pageSize=100");
    }

    [Fact]
    public void ToPageRelativeUri_WithDefaultPageSize_OmitsPageSizeToKeepTheUrlClean()
    {
        TranslationListQuery query =
            TranslationListQuery.From(search: null, status: null, page: 1, pageSize: TranslationListQuery.DefaultPageSize);

        query.ToPageRelativeUri(3).ShouldNotContain("pageSize=");
    }

    [Fact]
    public void ToPageRelativeUriWithApprovalResult_WithNonDefaultPageSize_PreservesIt()
    {
        // Continuity with the bulk-approve Post-Redirect-Get (#322): approving on a custom page size must
        // land back on the same-sized page, not silently reset to the default.
        TranslationListQuery query = TranslationListQuery.From(search: "Bilbo", status: "Draft", page: 2, pageSize: 25);

        string uri = query.ToPageRelativeUriWithApprovalResult(approved: 1, skipped: 0);

        uri.ShouldContain("page=2");
        uri.ShouldContain("pageSize=25");
        uri.ShouldContain("approved=1");
    }

    [Fact]
    public void ToPageRelativeUriWithApprovalResult_WithDefaultPageSize_OmitsPageSize()
    {
        TranslationListQuery query = TranslationListQuery.From(
            search: "Bilbo", status: "Draft", page: 2, pageSize: TranslationListQuery.DefaultPageSize);

        query.ToPageRelativeUriWithApprovalResult(approved: 1, skipped: 0).ShouldNotContain("pageSize=");
    }
}
