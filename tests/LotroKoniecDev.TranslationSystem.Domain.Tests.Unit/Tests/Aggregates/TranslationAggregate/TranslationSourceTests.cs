using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.TranslationAggregate;

public sealed class TranslationSourceTests
{
    private const string EnglishWithoutABreak = "Show all";

    [Fact]
    public void Create_WithTextAndArgs_ShouldSucceed()
    {
        // Act
        Result<TranslationSource> result = TranslationSource.Create("Welcome to <--DO_NOT_TOUCH!--> Middle-earth", "1", "1");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldBe("Welcome to <--DO_NOT_TOUCH!--> Middle-earth");
        result.Value.ArgsOrder.ShouldBe("1");
        result.Value.ArgsId.ShouldBe("1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NULL")]
    [InlineData("null")]
    public void Create_WithAbsentArgs_ShouldNormalizeToNull(string? argsValue)
    {
        // Act
        Result<TranslationSource> result = TranslationSource.Create("Some text", argsValue, argsValue);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ArgsOrder.ShouldBeNull();
        result.Value.ArgsId.ShouldBeNull();
    }

    [Fact]
    public void Equals_WithSameTextAndArgs_ShouldBeEqual()
    {
        // Arrange
        TranslationSource first = TranslationSource.Create("Text", "1-2", "1-2").Value;
        TranslationSource second = TranslationSource.Create("Text", "1-2", "1-2").Value;

        // Assert
        first.ShouldBe(second);
        first.GetHashCode().ShouldBe(second.GetHashCode());
    }

    [Fact]
    public void Equals_WithSameTextButDifferentArgs_ShouldNotBeEqual()
    {
        // Arrange: args are part of the source: a placeholder-structure change is a source change.
        TranslationSource first = TranslationSource.Create("Text", "1-2", "1-2").Value;
        TranslationSource second = TranslationSource.Create("Text", "2-1", "1-2").Value;

        // Assert
        first.ShouldNotBe(second);
    }

    [Fact]
    public void Equals_WithNullArgsBothSides_ShouldBeEqual()
    {
        // Arrange
        TranslationSource first = TranslationSource.Create("Text", null, null).Value;
        TranslationSource second = TranslationSource.Create("Text", "NULL", "NULL").Value;

        // Assert: NULL and absent collapse to the same value.
        first.ShouldBe(second);
    }

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
    public void RemoveTrailingLineBreaksFrom_WhenTheEnglishHasNoTrailingBreak_ShouldRemoveOnlyTheTrailingBreaks(
        string polish, string expected)
    {
        // Arrange
        TranslationSource source = Source(EnglishWithoutABreak);

        // Act
        string result = source.RemoveTrailingLineBreaksFrom(polish);

        // Assert
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("tekst")]
    [InlineData("tekst ")]
    [InlineData("tekst\\n")]
    [InlineData("pierwsza\r\ndruga")]
    [InlineData("\r\ntekst")]
    public void RemoveTrailingLineBreaksFrom_WhenThePolishHasNoTrailingBreak_ShouldKeepItAsTyped(string polish)
    {
        // Arrange
        TranslationSource source = Source(EnglishWithoutABreak);

        // Act
        string result = source.RemoveTrailingLineBreaksFrom(polish);

        // Assert
        result.ShouldBe(polish);
    }

    [Fact]
    public void RemoveTrailingLineBreaksFrom_WithBreaksAtBothEnds_ShouldRemoveOnlyTheOneAtTheEnd()
    {
        // Arrange: only the end is the Enter key's mark, so a trim from both sides would change the text.
        TranslationSource source = Source(EnglishWithoutABreak);

        // Act
        string result = source.RemoveTrailingLineBreaksFrom("\ntekst\r\n");

        // Assert
        result.ShouldBe("\ntekst");
    }

    [Theory]
    [InlineData("Line one\n")]
    [InlineData("Line one\r")]
    [InlineData("Line one\r\n")]
    public void RemoveTrailingLineBreaksFrom_WhenTheEnglishEndsWithABreak_ShouldKeepThePolishBreak(string english)
    {
        // Arrange
        TranslationSource source = Source(english);

        // Act
        string result = source.RemoveTrailingLineBreaksFrom("Linia pierwsza\r\n");

        // Assert
        result.ShouldBe("Linia pierwsza\r\n");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Line one\\n")]
    [InlineData("Line one ")]
    public void RemoveTrailingLineBreaksFrom_WhenTheEnglishEndsWithoutARealBreak_ShouldRemoveThePolishBreak(string english)
    {
        // Arrange: the game writes its line break as the two characters \n, which is text, not a break.
        TranslationSource source = Source(english);

        // Act
        string result = source.RemoveTrailingLineBreaksFrom("Linia pierwsza\r\n");

        // Assert
        result.ShouldBe("Linia pierwsza");
    }

    [Fact]
    public void RemoveTrailingLineBreaksFrom_WithNullText_ShouldThrow()
    {
        // Arrange
        TranslationSource source = Source(EnglishWithoutABreak);

        // Act
        Action remove = () => source.RemoveTrailingLineBreaksFrom(null!);

        // Assert
        remove.ShouldThrow<ArgumentNullException>();
    }

    private static TranslationSource Source(string text) => TranslationSource.Create(text, null, null).Value;
}
