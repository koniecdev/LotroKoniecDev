using FluentValidation.Results;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Constants;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

/// <summary>
/// The copy into identical rows checks the typed text with the save's rule (#1587): every copy has the same
/// English, so one check covers all of them.
/// </summary>
public sealed class FillIdenticalTranslationsValidatorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(DatFormatConstants.MaxTranslatedTextLength)]
    public void Validate_TextUpToTheDatLimit_ShouldPass(int length)
    {
        // Arrange
        FillIdenticalTranslations.Command command = new(TranslationId.Create(), new string('ż', length));

        // Act
        ValidationResult result = new FillIdenticalTranslations.Validator().Validate(command);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_TextOneAboveTheDatLimit_ShouldBeRefusedAsTooLong()
    {
        // Arrange: the patcher could not write any of the rows (#598, ADR-0043).
        FillIdenticalTranslations.Command command = new(
            TranslationId.Create(), new string('ż', DatFormatConstants.MaxTranslatedTextLength + 1));

        // Act
        ValidationResult result = new FillIdenticalTranslations.Validator().Validate(command);

        // Assert
        result.Errors.ShouldHaveSingleItem().ErrorCode.ShouldBe("MaximumLengthValidator");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void Validate_BlankText_ShouldBeRefusedAsEmpty(string? text)
    {
        // Arrange: a JSON body with a null text binds to null.
        FillIdenticalTranslations.Command command = new(TranslationId.Create(), text!);

        // Act
        ValidationResult result = new FillIdenticalTranslations.Validator().Validate(command);

        // Assert
        result.Errors.ShouldHaveSingleItem().ErrorCode.ShouldBe("NotEmptyValidator");
    }
}
