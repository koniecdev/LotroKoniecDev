using FluentValidation.Results;
using LotroKoniecDev.Frontend.Settings;

namespace LotroKoniecDev.Frontend.Tests.Unit.Settings;

public sealed class TranslationSystemSettingsValidatorTests
{
    private readonly TranslationSystemSettingsValidator _validator = new();

    [Fact]
    public void Validate_WithAbsoluteHttpsUrl_Passes()
    {
        TranslationSystemSettings settings = new() { BaseUrl = "https://localhost:5004/" };

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://localhost:5004")]
    public void Validate_WithNonHttpBaseUrl_Fails(string baseUrl)
    {
        TranslationSystemSettings settings = new() { BaseUrl = baseUrl };

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(TranslationSystemSettings.BaseUrl));
    }
}
