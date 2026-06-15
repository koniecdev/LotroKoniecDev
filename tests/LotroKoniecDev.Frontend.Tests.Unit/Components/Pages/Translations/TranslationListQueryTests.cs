using LotroKoniecDev.Frontend.Components.Pages.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Translations;

public sealed class TranslationListQueryTests
{
    [Fact]
    public void From_WithNoFilters_BuildsBaseUriWithLangAndDefaultPaging()
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: null, page: 1);

        string uri = query.ToApiRelativeUri();

        uri.ShouldStartWith("/api/v1/translations?");
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

        query.ToApiRelativeUri().ShouldContain("search=Witaj%20w%20%C5%9Ar%C3%B3dziemiu");
    }

    [Fact]
    public void From_WithSearchContainingLikeMetacharacters_EncodesThemForTransport()
    {
        TranslationListQuery query = TranslationListQuery.From(search: "100% & <tag>", status: null, page: 1);

        // The page only URL-encodes; the API escapes LIKE metacharacters server-side.
        string uri = query.ToApiRelativeUri();
        uri.ShouldContain("search=100%25%20%26%20%3Ctag%3E");
    }

    [Fact]
    public void From_WithStatus_IncludesStatusByEnumName()
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: "NeedsReview", page: 1);

        query.ToApiRelativeUri().ShouldContain("status=NeedsReview");
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
        query.ToApiRelativeUri().ShouldNotContain("status=");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void From_WithBlankSearch_OmitsSearchAndNormalizesToNull(string? search)
    {
        TranslationListQuery query = TranslationListQuery.From(search: search, status: null, page: 1);

        query.Search.ShouldBeNull();
        query.ToApiRelativeUri().ShouldNotContain("search=");
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
        query.ToApiRelativeUri().ShouldContain($"page={expected}");
    }

    [Fact]
    public void ToApiRelativeUri_WithAllFilters_ComposesEveryParameter()
    {
        TranslationListQuery query = TranslationListQuery.From(search: "Gandalf", status: "Approved", page: 2);

        string uri = query.ToApiRelativeUri();

        uri.ShouldStartWith("/api/v1/translations?");
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
}
