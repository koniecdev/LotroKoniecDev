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
    [InlineData("\r\n", "")]
    public void RemoveUnlessSourceEndsWithOne_WhenTheEnglishHasNoTrailingBreak_ShouldRemoveOnlyTheTrailingBreaks(
        string polish, string expected)
    {
        // Arrange
        TranslationSource source = Source(EnglishWithoutABreak);

        // Act
        string result = TrailingLineBreaks.RemoveUnlessSourceEndsWithOne(polish, source);

        // Assert
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("tekst")]
    [InlineData("tekst ")]
    [InlineData("tekst\\n")]
    [InlineData("pierwsza\r\ndruga")]
    [InlineData("\r\ntekst")]
    public void RemoveUnlessSourceEndsWithOne_WhenThePolishHasNoTrailingBreak_ShouldKeepItAsTyped(string polish)
    {
        // Arrange
        TranslationSource source = Source(EnglishWithoutABreak);

        // Act
        string result = TrailingLineBreaks.RemoveUnlessSourceEndsWithOne(polish, source);

        // Assert
        result.ShouldBe(polish);
    }

    [Fact]
    public void RemoveUnlessSourceEndsWithOne_WithBreaksAtBothEnds_ShouldRemoveOnlyTheOneAtTheEnd()
    {
        // Arrange: only the end is the Enter key's mark, so a trim from both sides would change the text.
        TranslationSource source = Source(EnglishWithoutABreak);

        // Act
        string result = TrailingLineBreaks.RemoveUnlessSourceEndsWithOne("\ntekst\r\n", source);

        // Assert
        result.ShouldBe("\ntekst");
    }

    [Theory]
    [InlineData("Line one\n")]
    [InlineData("Line one\r")]
    [InlineData("Line one\r\n")]
    public void RemoveUnlessSourceEndsWithOne_WhenTheEnglishEndsWithABreak_ShouldKeepThePolishBreak(string english)
    {
        // Arrange
        TranslationSource source = Source(english);

        // Act
        string result = TrailingLineBreaks.RemoveUnlessSourceEndsWithOne("Linia pierwsza\r\n", source);

        // Assert
        result.ShouldBe("Linia pierwsza\r\n");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Line one\\n")]
    [InlineData("Line one ")]
    public void RemoveUnlessSourceEndsWithOne_WhenTheEnglishEndsWithoutARealBreak_ShouldRemoveThePolishBreak(string english)
    {
        // Arrange: the game writes its line break as the two characters \n, which is text, not a break.
        TranslationSource source = Source(english);

        // Act
        string result = TrailingLineBreaks.RemoveUnlessSourceEndsWithOne("Linia pierwsza\r\n", source);

        // Assert
        result.ShouldBe("Linia pierwsza");
    }

    [Fact]
    public void RemoveUnlessSourceEndsWithOne_WithNullText_ShouldThrow()
    {
        // Arrange
        TranslationSource source = Source(EnglishWithoutABreak);

        // Act
        Action remove = () => TrailingLineBreaks.RemoveUnlessSourceEndsWithOne(null!, source);

        // Assert
        remove.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void RemoveUnlessSourceEndsWithOne_WithNullSource_ShouldThrow()
    {
        // Act
        Action remove = () => TrailingLineBreaks.RemoveUnlessSourceEndsWithOne("tekst", null!);

        // Assert
        remove.ShouldThrow<ArgumentNullException>();
    }

    private static TranslationSource Source(string text) => TranslationSource.Create(text, null, null).Value;
}
