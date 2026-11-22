using System.Reflection;
using LotroKoniecDev.Frontend.Components.Pages.Translations;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.AspNetCore.WebUtilities;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Translations;

public sealed class TranslationListQueryTests
{
    /// <summary>
    /// Looks nothing like the real route: this type must never know an API path of its own (#610).
    /// </summary>
    private const string CollectionHref = "/resolved-by-discovery/translations";

    private const string SiteOrigin = "https://lotro-translator.example";

    private static readonly TranslationId RowId = TranslationId.Create(Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301"));

    /// <summary>
    /// Every field differs from the default, so a round trip that loses any one of them fails.
    /// </summary>
    private static readonly TranslationListQuery EveryFieldSet = TranslationListQuery.From(
        search: "Frodo & Sam + #1 100% Śródziemie",
        status: "NeedsReview",
        page: 3,
        pageSize: 25,
        conflictsOnly: true,
        match: TranslationSearchMatch.WholeWord);

    [Fact]
    public void From_WithNoFilters_BuildsBaseUriWithDefaultPagingAndNoLanguage()
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: null, page: 1);

        string uri = query.ToApiUri(CollectionHref);

        uri.ShouldStartWith($"{CollectionHref}?");
        uri.ShouldNotContain("lang=");
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
        uri.ShouldNotContain("lang=");
        uri.ShouldContain("page=2");
        uri.ShouldContain($"pageSize={TranslationListQuery.DefaultPageSize}");
        uri.ShouldContain("search=Gandalf");
        uri.ShouldContain("status=Approved");
    }

    [Fact]
    public void ToApiRequest_CarriesTheListStateTheApiBinds()
    {
        TranslationListQuery query = TranslationListQuery.From(
            search: "  Gandalf ", status: "approved", page: 2, pageSize: 25, conflictsOnly: true);

        query.ToApiRequest().ShouldBe(new ListTranslationsRequest(
            Search: "Gandalf",
            Status: TranslationStatus.Approved,
            Page: 2,
            PageSize: 25,
            ConflictsOnly: true));
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
        // #322: the redirect lands back on the same filtered page, plus the counts.
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
        // Unlike the API, which clamps to 1-100, the list falls back to the default, so the dropdown can
        // always mark a real option (#323).
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
        // #322: approving on a custom page size lands back on the same-sized page.
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

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("on", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("abc", false)]
    [InlineData(null, false)]
    public void ParseConflictsOnly_ReadsOnlyTrueAsOn(string? value, bool expected)
        => TranslationListQuery.ParseConflictsOnly(value).ShouldBe(expected);

    [Fact]
    public void From_WithConflictsOnly_AsksTheApiForConflicts()
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: null, page: 1, conflictsOnly: true);

        query.ConflictsOnly.ShouldBeTrue();
        query.ToApiUri(CollectionHref).ShouldContain("conflictsOnly=true");
    }

    [Fact]
    public void From_WithoutConflictsOnly_LeavesTheParameterOutOfEveryUri()
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: null, page: 1);

        query.ToApiUri(CollectionHref).ShouldNotContain("conflictsOnly");
        query.ToPageRelativeUri(2).ShouldNotContain("conflictsOnly");
        query.ToPageRelativeUriWithApprovalResult(approved: 1, skipped: 0).ShouldNotContain("conflictsOnly");
    }

    [Fact]
    public void ToPageRelativeUri_WithConflictsOnly_KeepsItAcrossPaging()
    {
        TranslationListQuery query = TranslationListQuery.From(search: "axe", status: null, page: 1, conflictsOnly: true);

        query.ToPageRelativeUri(2).ShouldBe("/translations?page=2&search=axe&conflictsOnly=true");
    }

    [Fact]
    public void ToPageRelativeUriWithApprovalResult_WithConflictsOnly_KeepsItSoTheAdminStaysOnTheConflictList()
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: null, page: 1, conflictsOnly: true);

        query.ToPageRelativeUriWithApprovalResult(approved: 2, skipped: 0).ShouldContain("conflictsOnly=true");
    }

    [Fact]
    public void From_EveryFieldSetFixture_DiffersFromTheDefaultInEveryField()
    {
        // A field added later fails here until the round-trip fixture sets it, so the round trips below
        // cover it too.
        TranslationListQuery defaults = TranslationListQuery.From(search: null, status: null, page: 1);

        PropertyInfo[] fields = typeof(TranslationListQuery).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        fields.ShouldNotBeEmpty();
        fields.ShouldAllBe(field => !Equals(field.GetValue(EveryFieldSet), field.GetValue(defaults)));
    }

    [Fact]
    public void FromPageQuery_ReadsBackEveryFieldTheEditorAddressCarries()
    {
        string editorUri = EveryFieldSet.ToEditorRelativeUri(RowId);

        TranslationListQuery.FromPageQuery(editorUri).ShouldBe(EveryFieldSet);
    }

    [Fact]
    public void FromPageQuery_ReadsBackEveryFieldTheBackLinkCarries_FromAnAbsoluteAddress()
    {
        // The editor reads its own address as NavigationManager.Uri, which is absolute.
        string backLink = SiteOrigin + EveryFieldSet.ToListRelativeUri();

        TranslationListQuery.FromPageQuery(backLink).ShouldBe(EveryFieldSet);
    }

    [Fact]
    public void ToEditorRelativeUri_WithTheDefaultState_IsTheBareEditorAddress()
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: null, page: 1);

        query.ToEditorRelativeUri(RowId).ShouldBe($"/editor/{RowId.Value}");
    }

    [Fact]
    public void ToListRelativeUri_WithTheDefaultState_IsExactlyTheListPage()
    {
        TranslationListQuery query = TranslationListQuery.From(search: null, status: null, page: 1);

        query.ToListRelativeUri().ShouldBe("/translations");
    }

    [Fact]
    public void ToEditorRelativeUri_WritesThePagersNamesInThePagersOrder()
    {
        TranslationListQuery query = TranslationListQuery.From(
            search: "axe", status: "Draft", page: 2, pageSize: 25, conflictsOnly: true, match: TranslationSearchMatch.WholeText);

        query.ToEditorRelativeUri(RowId)
            .ShouldBe($"/editor/{RowId.Value}?page=2&search=axe&match=WholeText&status=Draft&conflictsOnly=true&pageSize=25");
    }

    [Fact]
    public void ToListRelativeUri_OnTheFirstPage_LeavesThePageOut()
    {
        TranslationListQuery query = TranslationListQuery.From(search: "axe", status: null, page: 1);

        query.ToListRelativeUri().ShouldBe("/translations?search=axe");
    }

    [Theory]
    [InlineData("Witaj w Śródziemiu")]
    [InlineData("zażółć gęślą jaźń")]
    [InlineData("100% & <tag>")]
    [InlineData("a+b")]
    [InlineData("#1 hit")]
    [InlineData("a&search=b")]
    [InlineData("x=y?z")]
    [InlineData("%2F")]
    public void ToEditorRelativeUri_KeepsASearchWithSpecialCharactersThroughTheWholeRoundTrip(string search)
    {
        TranslationListQuery query = TranslationListQuery.From(search: search, status: null, page: 2);

        string editorUri = query.ToEditorRelativeUri(RowId);
        string backLink = TranslationListQuery.FromPageQuery(SiteOrigin + editorUri).ToListRelativeUri();

        // QueryHelpers decodes as Blazor's query binding does: "+" is a space, "%2B" is a plus.
        QueryHelpers.ParseQuery(editorUri[editorUri.IndexOf('?', StringComparison.Ordinal)..])["search"]
            .ToString().ShouldBe(search);
        backLink.ShouldBe(query.ToListRelativeUri());
        TranslationListQuery.FromPageQuery(backLink).Search.ShouldBe(search);
    }

    [Fact]
    public void ToEditorRelativeUri_EncodesAPlusAndAHash_SoNeitherIsReadAsASpaceOrAFragment()
    {
        TranslationListQuery query = TranslationListQuery.From(search: "a+b #1", status: null, page: 1);

        query.ToEditorRelativeUri(RowId).ShouldBe($"/editor/{RowId.Value}?search=a%2Bb%20%231");
    }

    [Theory]
    [InlineData("search=//evil.example")]
    [InlineData("search=https://evil.example/")]
    [InlineData("search=%5C%5Cevil.example")]
    [InlineData("search=/%5Cevil.example")]
    [InlineData("search=javascript:alert(1)")]
    [InlineData("search=%0D%0ALocation:%20https://evil.example")]
    [InlineData("status=https://evil.example")]
    [InlineData("page=//evil.example")]
    [InlineData("pageSize=https://evil.example")]
    [InlineData("conflictsOnly=https://evil.example")]
    [InlineData("match=https://evil.example")]
    [InlineData("returnUrl=https://evil.example")]
    [InlineData("search=//evil.example&returnUrl=https://evil.example&page=2")]
    public void ToListRelativeUri_WhateverTheAddressCarries_StaysOnTheListPageOfThisSite(string editorQuery)
    {
        TranslationListQuery query = TranslationListQuery.FromPageQuery($"/editor/{RowId.Value}?{editorQuery}");

        Uri followed = new(new Uri(SiteOrigin), query.ToListRelativeUri());

        followed.GetLeftPart(UriPartial.Path).ShouldBe($"{SiteOrigin}/translations");
        followed.Query.ShouldNotContain("returnUrl");
    }

    [Fact]
    public void FromPageQuery_WithAnAddressInTheSearch_KeepsItOnlyAsTheSearchTerm()
    {
        TranslationListQuery query = TranslationListQuery.FromPageQuery($"/editor/{RowId.Value}?search=//evil.example");

        query.Search.ShouldBe("//evil.example");
        query.ToListRelativeUri().ShouldBe("/translations?search=%2F%2Fevil.example");
    }

    [Theory]
    [InlineData("")]
    [InlineData("page=abc")]
    [InlineData("page=0")]
    [InlineData("page=-3")]
    [InlineData("page=")]
    [InlineData("page=1.5")]
    [InlineData("page=99999999999")]
    [InlineData("pageSize=abc")]
    [InlineData("pageSize=7")]
    [InlineData("pageSize=")]
    [InlineData("status=Bogus")]
    [InlineData("status=Unset")]
    [InlineData("status=")]
    [InlineData("conflictsOnly=on")]
    [InlineData("conflictsOnly=1")]
    [InlineData("conflictsOnly")]
    [InlineData("match=Banana")]
    [InlineData("match=7")]
    [InlineData("match=")]
    [InlineData("search=")]
    [InlineData("search=%20%20")]
    public void FromPageQuery_WithAValueItCannotRead_FallsBackToTheDefaultAsTheListDoes(string editorQuery)
    {
        TranslationListQuery query = TranslationListQuery.FromPageQuery($"/editor/{RowId.Value}?{editorQuery}");

        query.ShouldBe(TranslationListQuery.From(search: null, status: null, page: 1));
    }

    [Fact]
    public void FromPageQuery_ReadsTheNamesInAnyCase_AsTheListsQueryBindingDoes()
    {
        TranslationListQuery query = TranslationListQuery.FromPageQuery(
            $"/editor/{RowId.Value}?SEARCH=axe&Page=2&STATUS=draft&MATCH=wholetext");

        query.ShouldBe(TranslationListQuery.From(
            search: "axe", status: "Draft", page: 2, match: TranslationSearchMatch.WholeText));
    }

    [Fact]
    public void FromPageQuery_IgnoresTheEditorsAndTheListsOwnFlags()
    {
        TranslationListQuery query = TranslationListQuery.FromPageQuery(
            $"/editor/{RowId.Value}?search=axe&saved=true&approved=true&skipped=1");

        query.ToListRelativeUri().ShouldBe("/translations?search=axe");
        query.ToEditorRelativeUri(RowId).ShouldBe($"/editor/{RowId.Value}?search=axe");
    }

    [Fact]
    public void FromPageQuery_WithARepeatedName_TakesTheFirstValueAsBlazorDoes()
    {
        TranslationListQuery query = TranslationListQuery.FromPageQuery(
            $"/editor/{RowId.Value}?page=2&page=3&search=a&search=b");

        query.Page.ShouldBe(2);
        query.Search.ShouldBe("a");
    }

    [Theory]
    [InlineData("/editor/x?search=axe#fragment", "axe")]
    [InlineData("/editor/x#fragment?search=axe", null)]
    [InlineData("/editor/x", null)]
    [InlineData("https://lotro-translator.example/editor/x?search=axe", "axe")]
    public void FromPageQuery_ReadsOnlyTheQueryPartOfTheAddress(string uri, string? expectedSearch)
    {
        TranslationListQuery.FromPageQuery(uri).Search.ShouldBe(expectedSearch);
    }

    [Theory]
    [InlineData("WholeWord", TranslationSearchMatch.WholeWord)]
    [InlineData("wholeword", TranslationSearchMatch.WholeWord)]
    [InlineData("WHOLETEXT", TranslationSearchMatch.WholeText)]
    [InlineData(" WholeText ", TranslationSearchMatch.WholeText)]
    [InlineData("Contains", TranslationSearchMatch.Contains)]
    [InlineData("", TranslationSearchMatch.Contains)]
    [InlineData("   ", TranslationSearchMatch.Contains)]
    [InlineData(null, TranslationSearchMatch.Contains)]
    [InlineData("Banana", TranslationSearchMatch.Contains)]
    [InlineData("1", TranslationSearchMatch.Contains)]
    [InlineData("2", TranslationSearchMatch.Contains)]
    [InlineData("WholeWord,WholeText", TranslationSearchMatch.Contains)]
    public void ParseMatch_ReadsOnlyAModeNameAndFallsBackToContains(string? value, TranslationSearchMatch expected)
        => TranslationListQuery.ParseMatch(value).ShouldBe(expected);

    [Fact]
    public void From_WithAnUndefinedMatch_FallsBackToContains()
    {
        TranslationListQuery query = TranslationListQuery.From(
            search: "ring", status: null, page: 1, match: (TranslationSearchMatch)7);

        query.Match.ShouldBe(TranslationSearchMatch.Contains);
        query.ToApiUri(CollectionHref).ShouldNotContain("match=");
    }

    [Theory]
    [InlineData(TranslationSearchMatch.WholeWord, "match=WholeWord")]
    [InlineData(TranslationSearchMatch.WholeText, "match=WholeText")]
    public void From_WithANewMatchMode_AsksTheApiForIt(TranslationSearchMatch match, string expected)
    {
        TranslationListQuery query = TranslationListQuery.From(search: "ring", status: null, page: 1, match: match);

        query.ToApiRequest().Match.ShouldBe(match);
        query.ToApiUri(CollectionHref).ShouldContain(expected);
    }

    [Fact]
    public void From_WithContains_LeavesTheMatchOutOfEveryUri()
    {
        TranslationListQuery query = TranslationListQuery.From(
            search: "ring", status: null, page: 1, match: TranslationSearchMatch.Contains);

        query.ToApiUri(CollectionHref).ShouldNotContain("match=");
        query.ToPageRelativeUri(2).ShouldNotContain("match=");
        query.ToPageRelativeUriWithApprovalResult(approved: 1, skipped: 0).ShouldNotContain("match=");
    }

    [Fact]
    public void ToPageRelativeUri_WithAMatchMode_KeepsItAcrossPaging()
    {
        TranslationListQuery query = TranslationListQuery.From(
            search: "ring", status: "Draft", page: 1, conflictsOnly: true, match: TranslationSearchMatch.WholeText);

        query.ToPageRelativeUri(2).ShouldBe("/translations?page=2&search=ring&match=WholeText&status=Draft&conflictsOnly=true");
    }

    [Fact]
    public void ToPageRelativeUriWithApprovalResult_WithAMatchMode_KeepsIt()
    {
        TranslationListQuery query = TranslationListQuery.From(
            search: "ring", status: null, page: 3, match: TranslationSearchMatch.WholeWord);

        query.ToPageRelativeUriWithApprovalResult(approved: 2, skipped: 1)
            .ShouldBe("/translations?page=3&search=ring&match=WholeWord&approved=2&skipped=1");
    }
}
