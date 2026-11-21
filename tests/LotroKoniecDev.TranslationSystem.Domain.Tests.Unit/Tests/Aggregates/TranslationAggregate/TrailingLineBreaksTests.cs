using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.TranslationAggregate;

public sealed class TrailingLineBreaksTests
{
    private const string EnglishWithoutABreak = "Show all";

    [Theory]
    [InlineData("Pokaż wszystko\r\n", "Pokaż wszystko")]
    [InlineData("tekst\n", "tekst")]
    [InlineData("tekst\r", "tekst")]
    [InlineData("tekst\r\n\r\n", "tekst")]
    [InlineData("tekst\n\r", "tekst")]
    [InlineData("tekst \r\n", "tekst ")]
    [InlineData("tekst\t\n", "tekst\t")]
    [InlineData("tekst\\n\r\n", "tekst\\n")]
    public void RemoveUnlessSourceEndsWithOne_WhenTheEnglishHasNoTrailingBreak_ShouldRemoveOnlyTheTrailingBreaks(
        string polish, string expected)
        => TrailingLineBreaks.RemoveUnlessSourceEndsWithOne(polish, Source(EnglishWithoutABreak)).ShouldBe(expected);

    [Theory]
    [InlineData("tekst")]
    [InlineData("tekst ")]
    [InlineData("tekst\\n")]
    [InlineData("pierwsza\r\ndruga")]
    [InlineData("\r\ntekst")]
    public void RemoveUnlessSourceEndsWithOne_WhenThePolishHasNoTrailingBreak_ShouldKeepItAsTyped(string polish)
        => TrailingLineBreaks.RemoveUnlessSourceEndsWithOne(polish, Source(EnglishWithoutABreak)).ShouldBe(polish);

    [Fact]
    public void RemoveUnlessSourceEndsWithOne_WithBreaksAtBothEnds_ShouldRemoveOnlyTheOneAtTheEnd()
        // Only the end is the Enter key's mark, so a trim from both sides would change the text.
        => TrailingLineBreaks.RemoveUnlessSourceEndsWithOne("\ntekst\r\n", Source(EnglishWithoutABreak)).ShouldBe("\ntekst");

    [Theory]
    [InlineData("Line one\n")]
    [InlineData("Line one\r")]
    [InlineData("Line one\r\n")]
    public void RemoveUnlessSourceEndsWithOne_WhenTheEnglishEndsWithABreak_ShouldKeepThePolishBreak(string english)
        => TrailingLineBreaks.RemoveUnlessSourceEndsWithOne("Linia pierwsza\r\n", Source(english)).ShouldBe("Linia pierwsza\r\n");

    [Theory]
    [InlineData("")]
    [InlineData("Line one\\n")]
    [InlineData("Line one ")]
    public void RemoveUnlessSourceEndsWithOne_WhenTheEnglishEndsWithoutARealBreak_ShouldRemoveThePolishBreak(string english)
        // The game writes its line break as the two characters \n, which is text, not a break.
        => TrailingLineBreaks.RemoveUnlessSourceEndsWithOne("Linia pierwsza\r\n", Source(english)).ShouldBe("Linia pierwsza");

    [Fact]
    public void RemoveUnlessSourceEndsWithOne_WithNullText_ShouldThrow()
        => Should.Throw<ArgumentNullException>(
            () => TrailingLineBreaks.RemoveUnlessSourceEndsWithOne(null!, Source(EnglishWithoutABreak)));

    [Fact]
    public void RemoveUnlessSourceEndsWithOne_WithNullSource_ShouldThrow()
        => Should.Throw<ArgumentNullException>(() => TrailingLineBreaks.RemoveUnlessSourceEndsWithOne("tekst", null!));

    private static TranslationSource Source(string text) => TranslationSource.Create(text, null, null).Value;
}
