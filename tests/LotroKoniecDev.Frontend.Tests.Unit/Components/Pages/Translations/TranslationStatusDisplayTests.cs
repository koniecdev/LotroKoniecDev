using LotroKoniecDev.Frontend.Components.Pages.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Translations;

public sealed class TranslationStatusDisplayTests
{
    [Theory]
    [InlineData(TranslationStatus.Untranslated, "Nieprzetłumaczone")]
    [InlineData(TranslationStatus.Draft, "Wersja robocza")]
    [InlineData(TranslationStatus.Approved, "Zatwierdzone")]
    [InlineData(TranslationStatus.NeedsReview, "Do ponownego sprawdzenia")]
    [InlineData(TranslationStatus.Unset, "Nieznany")]
    public void Label_ReturnsThePolishLabelForEveryStatus(TranslationStatus status, string expected)
    {
        TranslationStatusDisplay.Label(status).ShouldBe(expected);
    }

    [Theory]
    [InlineData(TranslationStatus.Approved, "badge-success")]
    [InlineData(TranslationStatus.Draft, "badge-warning")]
    [InlineData(TranslationStatus.NeedsReview, "badge-danger")]
    [InlineData(TranslationStatus.Untranslated, "badge-neutral")]
    [InlineData(TranslationStatus.Unset, "badge-neutral")]
    public void BadgeClass_MapsEveryStatusToAKnownBadgeClass(TranslationStatus status, string expected)
    {
        TranslationStatusDisplay.BadgeClass(status).ShouldBe(expected);
    }

    [Fact]
    public void FilterableStatuses_ExcludesTheUnsetSentinel()
    {
        TranslationStatusDisplay.FilterableStatuses.ShouldNotContain(TranslationStatus.Unset);
    }

    [Fact]
    public void FilterableStatuses_ContainsEveryRealStatusInDisplayOrder()
    {
        TranslationStatusDisplay.FilterableStatuses.ShouldBe(
        [
            TranslationStatus.Untranslated,
            TranslationStatus.Draft,
            TranslationStatus.Approved,
            TranslationStatus.NeedsReview
        ]);
    }
}
