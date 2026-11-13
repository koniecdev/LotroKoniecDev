using FluentValidation.Results;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

public sealed class TranslationSaveBudgetSettingsValidatorTests
{
    private readonly TranslationSaveBudgetSettingsValidator _validator = new();

    [Fact]
    public void Validate_TheDefaults_ShouldPass()
    {
        // Act
        ValidationResult result = _validator.Validate(new TranslationSaveBudgetSettings());

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_EveryThresholdEqualToItsLimitAndTheLongestWindow_ShouldPass()
    {
        // Arrange: 720 hours as a literal, because it is the privacy policy's 30 days, not a free choice.
        TranslationSaveBudgetSettings settings = new()
        {
            SaveLimit = 1,
            SaveAlertThreshold = 1,
            OverwriteLimit = 1,
            OverwriteAlertThreshold = 1,
            WindowHours = 720
        };

        // Act
        ValidationResult result = _validator.Validate(settings);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 1, 100, 50, 24, "TranslationSaveBudget:SaveLimit")]
    [InlineData(1_000, 0, 100, 50, 24, "TranslationSaveBudget:SaveAlertThreshold")]
    [InlineData(1_000, 1_001, 100, 50, 24, "TranslationSaveBudget:SaveAlertThreshold")]
    [InlineData(1_000, 500, 0, 1, 24, "TranslationSaveBudget:OverwriteLimit")]
    [InlineData(1_000, 500, 100, 0, 24, "TranslationSaveBudget:OverwriteAlertThreshold")]
    [InlineData(1_000, 500, 100, 101, 24, "TranslationSaveBudget:OverwriteAlertThreshold")]
    [InlineData(1_000, 500, 100, 50, 0, "TranslationSaveBudget:WindowHours")]
    [InlineData(1_000, 500, 100, 50, 721, "TranslationSaveBudget:WindowHours")]
    public void Validate_AValueThatSwitchesTheGuardOrItsAlertOff_ShouldFailNamingTheKey(
        int saveLimit,
        int saveAlertThreshold,
        int overwriteLimit,
        int overwriteAlertThreshold,
        int windowHours,
        string expectedKey)
    {
        // Arrange
        TranslationSaveBudgetSettings settings = new()
        {
            SaveLimit = saveLimit,
            SaveAlertThreshold = saveAlertThreshold,
            OverwriteLimit = overwriteLimit,
            OverwriteAlertThreshold = overwriteAlertThreshold,
            WindowHours = windowHours
        };

        // Act
        ValidationResult result = _validator.Validate(settings);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(failure => failure.ErrorMessage.StartsWith(expectedKey, StringComparison.Ordinal));
    }
}
