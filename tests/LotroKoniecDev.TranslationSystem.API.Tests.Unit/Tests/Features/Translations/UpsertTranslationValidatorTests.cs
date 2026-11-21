using System.Globalization;
using FluentValidation.Results;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Constants;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

/// <summary>
/// The text rules, and #1092: the default messages stay English on a machine whose OS has another
/// language (<see cref="ValidationMessageLanguage"/>).
/// </summary>
public sealed class UpsertTranslationValidatorTests
{
    private const int FileId = 620756992;

    [Theory]
    [InlineData("pl-PL")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void Validate_BlankTextOnAMachineInAnotherLanguage_ShouldAnswerInEnglish(string machineCulture)
    {
        // Arrange
        UpsertTranslation.Command command = new(FileId, 1, "   ");

        // Act
        ValidationResult result = ValidateIn(CultureInfo.GetCultureInfo(machineCulture), command);

        // Assert
        result.Errors.ShouldHaveSingleItem().ErrorMessage.ShouldBe("'Translated Text' must not be empty.");
    }

    [Theory]
    [InlineData("pl-PL")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void Validate_TooLongTextOnAMachineInAnotherLanguage_ShouldAnswerInEnglishWithPlainNumbers(string machineCulture)
    {
        // Arrange: the limit and the entered length are the numbers FluentValidation writes into the
        // message with the culture of the machine.
        UpsertTranslation.Command command = new(FileId, 1, new string('a', DatFormatConstants.MaxTranslatedTextLength + 1));

        // Act
        ValidationResult result = ValidateIn(CultureInfo.GetCultureInfo(machineCulture), command);

        // Assert
        result.Errors.ShouldHaveSingleItem().ErrorMessage.ShouldBe(
            "The length of 'Translated Text' must be 32767 characters or fewer. You entered 32768 characters.");
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r\n\r\n")]
    public void Validate_TextOfOnlyLineBreaks_ShouldBeRefusedAsEmpty(string lineBreaks)
    {
        // Arrange: #1476 removes trailing breaks at the save, so a text of only breaks is an empty save.
        UpsertTranslation.Command command = new(FileId, 1, lineBreaks);

        // Act
        ValidationResult result = new UpsertTranslation.Validator().Validate(command);

        // Assert
        result.Errors.ShouldHaveSingleItem().ErrorCode.ShouldBe("NotEmptyValidator");
    }

    [Fact]
    public void Validate_TextAtTheLimitOnlyAfterItsTrailingLineBreaksGo_ShouldBeRefusedAsTooLong()
    {
        // Arrange: the validator cannot see the English, which decides whether the break stays (#1476),
        // so it counts the text as sent, like the domain guard behind it.
        string text = new string('a', DatFormatConstants.MaxTranslatedTextLength - 1) + "\r\n";
        UpsertTranslation.Command command = new(FileId, 1, text);

        // Act
        ValidationResult result = new UpsertTranslation.Validator().Validate(command);

        // Assert
        result.Errors.ShouldHaveSingleItem().ErrorCode.ShouldBe("MaximumLengthValidator");
    }

    private static ValidationResult ValidateIn(CultureInfo culture, UpsertTranslation.Command command)
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            UpsertTranslation.Validator validator = new();
            return validator.Validate(command);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
