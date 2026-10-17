using System.Globalization;
using FluentValidation.Results;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Constants;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

/// <summary>
/// #1092: the default messages stay English on a machine whose OS has another language
/// (<see cref="ValidationMessageLanguage"/>).
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
