using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Constants;

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

    [Theory]
    [InlineData("Hail!", "Witaj!")]
    [InlineData("", "Cokolwiek")]
    [InlineData("Hail <--DO_NOT_TOUCH!-->!", "<--DO_NOT_TOUCH!--> wita!")]
    [InlineData("<--DO_NOT_TOUCH!--><--DO_NOT_TOUCH!-->", "a<--DO_NOT_TOUCH!-->b<--DO_NOT_TOUCH!-->c")]
    [InlineData("Hail!", "Witaj <--DO_NOT_TOUCH!--!")]
    [InlineData("Hail!", "Witaj <--do_not_touch!-->!")]
    public void CheckPlaceholderCountOf_WhenTheCountsMatch_ShouldSucceed(string english, string polish)
    {
        // Arrange: a marker cut short or in other letter case is plain text, as it is for the patcher.
        TranslationSource source = Source(english);

        // Act
        Result result = source.CheckPlaceholderCountOf(polish);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Hail <--DO_NOT_TOUCH!-->!", "Witaj!", 1, 0)]
    [InlineData("Hail <--DO_NOT_TOUCH!-->!", "Witaj <--DO_NOT_TOUCH!--><--DO_NOT_TOUCH!-->!", 1, 2)]
    [InlineData("Hail!", "Witaj <--DO_NOT_TOUCH!-->!", 0, 1)]
    [InlineData("", "<--DO_NOT_TOUCH!-->", 0, 1)]
    [InlineData("A <--DO_NOT_TOUCH!--> B <--DO_NOT_TOUCH!-->", "A <--DO_NOT_TOUCH!--> B <--DO_NOT_TOUCH!--", 2, 1)]
    public void CheckPlaceholderCountOf_WhenTheCountsDiffer_ShouldFailWithAValidationError(
        string english, string polish, int englishCount, int polishCount)
    {
        // Arrange
        TranslationSource source = Source(english);

        // Act
        Result result = source.CheckPlaceholderCountOf(polish);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationEntity.TranslatedText.PlaceholderCountMismatch");
        result.Error.Type.ShouldBe(TypeOfError.Validation);
        result.Error.Message.ShouldBe(
            $"The Polish text has {polishCount} placeholder(s) and the English has {englishCount}; they must match.");
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("No marker", 0)]
    [InlineData("<--DO_NOT_TOUCH!-->", 1)]
    [InlineData("<--DO_NOT_TOUCH!--><--DO_NOT_TOUCH!-->", 2)]
    [InlineData("a<--DO_NOT_TOUCH!-->b<--DO_NOT_TOUCH!-->c<--DO_NOT_TOUCH!-->", 3)]
    [InlineData("<--DO_NOT_TOUCH!--", 0)]
    [InlineData("<--DO_NOT_TOUCH!--<--DO_NOT_TOUCH!-->", 1)]
    [InlineData("<<--DO_NOT_TOUCH!-->>", 1)]
    [InlineData("<--DO_NOT_TOUCH!-->-->", 1)]
    [InlineData("<--do_not_touch!-->", 0)]
    public void CountPlaceholders_ShouldCountWholeMarkersWithoutOverlap(string text, int expected)
    {
        // Act
        int count = TranslationSource.CountPlaceholders(text);

        // Assert
        count.ShouldBe(expected);
    }

    [Fact]
    public void Placeholder_ShouldBeTheFileContractMarker()
    {
        // Assert: the translators type it and the file carries it; the string itself is the contract.
        DatFormatConstants.Placeholder.ShouldBe("<--DO_NOT_TOUCH!-->");
    }

    [Fact]
    public void CheckPlaceholderCountOf_WithNullText_ShouldThrow()
    {
        // Arrange
        TranslationSource source = Source(EnglishWithoutABreak);

        // Act
        Action check = () => source.CheckPlaceholderCountOf(null!);

        // Assert
        check.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void CountPlaceholders_WithNullText_ShouldThrow()
    {
        // Act
        Action count = () => TranslationSource.CountPlaceholders(null!);

        // Assert
        count.ShouldThrow<ArgumentNullException>();
    }

    private static TranslationSource Source(string text) => TranslationSource.Create(text, null, null).Value;
}
