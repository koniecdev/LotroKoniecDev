using LotroKoniecDev.Frontend.Components.Pages.Editor;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Editor;

public sealed class PlaceholderAnalyzerTests
{
    private const string Token = "<--DO_NOT_TOUCH!-->";

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("No placeholders here", 0)]
    [InlineData("One " + Token + " marker", 1)]
    [InlineData("Two " + Token + " and " + Token + " markers", 2)]
    [InlineData(Token + Token + Token, 3)]
    [InlineData(Token, 1)]
    public void Count_ReturnsTheNumberOfMarkers(string? text, int expected)
    {
        PlaceholderAnalyzer.Count(text).ShouldBe(expected);
    }

    [Fact]
    public void Count_DoesNotMatchAcrossOverlappingPartialTokens()
    {
        // A truncated marker must not be double-counted; only the full token is a placeholder.
        PlaceholderAnalyzer.Count("<--DO_NOT_TOUCH!--").ShouldBe(0);
    }

    [Fact]
    public void Compare_WhenCountsAreEqual_ReportsAMatch()
    {
        PlaceholderComparison comparison = PlaceholderAnalyzer.Compare(
            $"Hello {Token} world",
            $"Witaj {Token} świecie");

        comparison.IsMatch.ShouldBeTrue();
        comparison.SourceCount.ShouldBe(1);
        comparison.TranslatedCount.ShouldBe(1);
    }

    [Fact]
    public void Compare_WhenTranslationDropsAMarker_ReportsAMismatchWithBothCounts()
    {
        PlaceholderComparison comparison = PlaceholderAnalyzer.Compare(
            $"You have {Token} of {Token} items",
            $"Masz {Token} przedmiotów");

        comparison.IsMatch.ShouldBeFalse();
        comparison.SourceCount.ShouldBe(2);
        comparison.TranslatedCount.ShouldBe(1);
    }

    [Fact]
    public void Compare_WhenTranslationAddsAMarker_ReportsAMismatch()
    {
        PlaceholderComparison comparison = PlaceholderAnalyzer.Compare(
            "No markers in source",
            $"Dodatkowy {Token} znacznik");

        comparison.IsMatch.ShouldBeFalse();
        comparison.SourceCount.ShouldBe(0);
        comparison.TranslatedCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Compare_WhenTranslationIsStillEmpty_NeverWarnsEvenIfSourceHasMarkers(string? translatedText)
    {
        // An untranslated row must not show a warning, because there is nothing to compare yet.
        PlaceholderComparison comparison = PlaceholderAnalyzer.Compare(
            $"Source with {Token} marker",
            translatedText);

        comparison.IsMatch.ShouldBeTrue();
        comparison.SourceCount.ShouldBe(1);
        comparison.TranslatedCount.ShouldBe(0);
    }

    [Fact]
    public void Compare_WhenSourceHasNoMarkersAndTranslationIsTyped_ReportsAMatch()
    {
        PlaceholderComparison comparison = PlaceholderAnalyzer.Compare(
            "Plain English",
            "Zwykły polski");

        comparison.IsMatch.ShouldBeTrue();
        comparison.SourceCount.ShouldBe(0);
        comparison.TranslatedCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Segment_WithNoText_YieldsNoSegments(string? text)
    {
        PlaceholderAnalyzer.Segment(text).ShouldBeEmpty();
    }

    [Fact]
    public void Segment_WithNoMarkers_YieldsASingleLiteralSegment()
    {
        IReadOnlyList<PlaceholderSegment> segments = PlaceholderAnalyzer.Segment("Just literal text");

        PlaceholderSegment only = segments.ShouldHaveSingleItem();
        only.IsPlaceholder.ShouldBeFalse();
        only.Text.ShouldBe("Just literal text");
    }

    [Fact]
    public void Segment_WithAMarkerInTheMiddle_SplitsLiteralMarkerLiteralInOrder()
    {
        IReadOnlyList<PlaceholderSegment> segments = PlaceholderAnalyzer.Segment($"before {Token} after");

        segments.Count.ShouldBe(3);
        segments[0].ShouldBe(new PlaceholderSegment("before ", false));
        segments[1].ShouldBe(new PlaceholderSegment(Token, true));
        segments[2].ShouldBe(new PlaceholderSegment(" after", false));
    }

    [Fact]
    public void Segment_WithLeadingAndTrailingMarkers_DoesNotEmitEmptyLiterals()
    {
        IReadOnlyList<PlaceholderSegment> segments = PlaceholderAnalyzer.Segment($"{Token}middle{Token}");

        segments.Count.ShouldBe(3);
        segments[0].ShouldBe(new PlaceholderSegment(Token, true));
        segments[1].ShouldBe(new PlaceholderSegment("middle", false));
        segments[2].ShouldBe(new PlaceholderSegment(Token, true));
    }

    [Fact]
    public void Segment_WithAdjacentMarkers_EmitsBackToBackPlaceholdersWithNoLiteralBetween()
    {
        IReadOnlyList<PlaceholderSegment> segments = PlaceholderAnalyzer.Segment($"{Token}{Token}");

        segments.Count.ShouldBe(2);
        segments.ShouldAllBe(segment => segment.IsPlaceholder);
    }

    [Fact]
    public void Segment_PreservesNewlinesInsideLiteralRuns()
    {
        IReadOnlyList<PlaceholderSegment> segments = PlaceholderAnalyzer.Segment($"line1\r\nline2 {Token}");

        segments[0].Text.ShouldBe("line1\r\nline2 ");
        segments[1].IsPlaceholder.ShouldBeTrue();
    }

    [Fact]
    public void Segment_RoundTripsToTheOriginalTextWhenConcatenated()
    {
        const string original = $"A {Token} B {Token}{Token} C";

        string reconstructed = string.Concat(
            PlaceholderAnalyzer.Segment(original).Select(segment => segment.Text));

        reconstructed.ShouldBe(original);
    }
}
