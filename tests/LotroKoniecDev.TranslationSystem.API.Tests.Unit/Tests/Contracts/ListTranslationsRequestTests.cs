using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Contracts;

/// <summary>
/// The keys are pinned as text on purpose: they are the API's public option names, so renaming a
/// property must fail here and be a deliberate change (#1478).
/// </summary>
public sealed class ListTranslationsRequestTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    public void Page_IsClampedToAtLeastOne(int input, int expected)
        => new ListTranslationsRequest(Page: input).Page.ShouldBe(expected);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(101, 100)]
    [InlineData(100, 100)]
    [InlineData(50, 50)]
    public void PageSize_IsClampedBetweenOneAndHundred(int input, int expected)
        => new ListTranslationsRequest(PageSize: input).PageSize.ShouldBe(expected);

    [Fact]
    public void ToQueryParameters_AtTheDefaults_WritesOnlyTheFirstPageAndTheDefaultPageSize()
    {
        IReadOnlyList<KeyValuePair<string, string?>> parameters = new ListTranslationsRequest().ToQueryParameters();

        parameters.ShouldBe(
        [
            new KeyValuePair<string, string?>("page", "1"),
            new KeyValuePair<string, string?>("pageSize", "50")
        ]);
    }

    [Fact]
    public void ToQueryParameters_WithEveryOption_WritesTheFiltersFirstThenThePaging()
    {
        // The paging links rely on this order.
        ListTranslationsRequest request = new(
            Search: "Stout-axe",
            Status: TranslationStatus.NeedsReview,
            Page: 3,
            PageSize: 25,
            Sort: "gossipId:desc",
            ConflictsOnly: true,
            Match: TranslationSearchMatch.WholeText,
            Mine: true);

        IReadOnlyList<KeyValuePair<string, string?>> parameters = request.ToQueryParameters();

        parameters.ShouldBe(
        [
            new KeyValuePair<string, string?>("search", "Stout-axe"),
            new KeyValuePair<string, string?>("match", "WholeText"),
            new KeyValuePair<string, string?>("status", "NeedsReview"),
            new KeyValuePair<string, string?>("sort", "gossipId:desc"),
            new KeyValuePair<string, string?>("conflictsOnly", "true"),
            new KeyValuePair<string, string?>("mine", "true"),
            new KeyValuePair<string, string?>("page", "3"),
            new KeyValuePair<string, string?>("pageSize", "25")
        ]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ToQueryParameters_WithBlankSearchAndSort_LeavesThemOut(string blank)
    {
        IReadOnlyList<KeyValuePair<string, string?>> parameters =
            new ListTranslationsRequest(Search: blank, Sort: blank).ToQueryParameters();

        parameters.ShouldBe(
        [
            new KeyValuePair<string, string?>("page", "1"),
            new KeyValuePair<string, string?>("pageSize", "50")
        ]);
    }

    [Fact]
    public void ToQueryParameters_WithPaddedSearch_SendsItAsTyped()
    {
        // The API trims the term itself.
        IReadOnlyList<KeyValuePair<string, string?>> parameters =
            new ListTranslationsRequest(Search: " Frodo ").ToQueryParameters();

        parameters.ShouldContain(new KeyValuePair<string, string?>("search", " Frodo "));
    }

    [Fact]
    public void ToQueryParameters_ForAnotherPage_ChangesOnlyThePage()
    {
        ListTranslationsRequest request = new(Search: "Frodo", Page: 2, PageSize: 25, ConflictsOnly: true);

        IReadOnlyList<KeyValuePair<string, string?>> parameters = request.ToQueryParameters(7);

        parameters.ShouldBe(
        [
            new KeyValuePair<string, string?>("search", "Frodo"),
            new KeyValuePair<string, string?>("conflictsOnly", "true"),
            new KeyValuePair<string, string?>("page", "7"),
            new KeyValuePair<string, string?>("pageSize", "25")
        ]);
    }

    [Theory]
    [InlineData(TranslationSearchMatch.WholeWord, "WholeWord")]
    [InlineData(TranslationSearchMatch.WholeText, "WholeText")]
    public void ToQueryParameters_WithANewMatchMode_WritesItsName(TranslationSearchMatch match, string expected)
    {
        IReadOnlyList<KeyValuePair<string, string?>> parameters =
            new ListTranslationsRequest(Search: "ring", Match: match).ToQueryParameters();

        parameters.ShouldContain(new KeyValuePair<string, string?>("match", expected));
    }

    [Fact]
    public void ToQueryParameters_WithContains_LeavesTheMatchOut()
    {
        // Contains is the default, so an address with no match keeps its meaning (#1477).
        IReadOnlyList<KeyValuePair<string, string?>> parameters =
            new ListTranslationsRequest(Search: "ring", Match: TranslationSearchMatch.Contains).ToQueryParameters();

        parameters.ShouldBe(
        [
            new KeyValuePair<string, string?>("search", "ring"),
            new KeyValuePair<string, string?>("page", "1"),
            new KeyValuePair<string, string?>("pageSize", "50")
        ]);
    }

    [Fact]
    public void ToQueryParameters_WritesTheClampedPaging()
    {
        IReadOnlyList<KeyValuePair<string, string?>> parameters =
            new ListTranslationsRequest(Page: 0, PageSize: 500).ToQueryParameters();

        parameters.ShouldBe(
        [
            new KeyValuePair<string, string?>("page", "1"),
            new KeyValuePair<string, string?>("pageSize", "100")
        ]);
    }

    [Fact]
    public void ToQueryParameters_WithAfter_WritesItAfterTheFiltersAndBeforeThePaging()
    {
        ListTranslationsRequest request = new(Search: "ring", Mine: true, After: "azoxOjI", PageSize: 25);

        IReadOnlyList<KeyValuePair<string, string?>> parameters = request.ToQueryParameters();

        parameters.ShouldBe(
        [
            new KeyValuePair<string, string?>("search", "ring"),
            new KeyValuePair<string, string?>("mine", "true"),
            new KeyValuePair<string, string?>("after", "azoxOjI"),
            new KeyValuePair<string, string?>("page", "1"),
            new KeyValuePair<string, string?>("pageSize", "25")
        ]);
    }

    [Fact]
    public void ToQueryParameters_ForAnotherPage_LeavesAfterOut()
    {
        // A numbered page starts at its own first row, so the paging links never carry a place (ADR-0077).
        ListTranslationsRequest request = new(Search: "ring", After: "azoxOjI", PageSize: 25);

        IReadOnlyList<KeyValuePair<string, string?>> parameters = request.ToQueryParameters(2);

        parameters.ShouldBe(
        [
            new KeyValuePair<string, string?>("search", "ring"),
            new KeyValuePair<string, string?>("page", "2"),
            new KeyValuePair<string, string?>("pageSize", "25")
        ]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ToQueryParameters_WithABlankAfter_LeavesItOut(string blank)
    {
        IReadOnlyList<KeyValuePair<string, string?>> parameters =
            new ListTranslationsRequest(After: blank).ToQueryParameters();

        parameters.ShouldBe(
        [
            new KeyValuePair<string, string?>("page", "1"),
            new KeyValuePair<string, string?>("pageSize", "50")
        ]);
    }
}
