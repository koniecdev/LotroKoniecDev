using FluentValidation.Results;
using LotroKoniecDev.TranslationSystem.Persistence.Settings;
using Shouldly;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Settings;

/// <summary>
/// Pure unit coverage for the TMS connection-string startup guard (M6-05). It lives in the
/// integration project because the validator is internal to the Persistence assembly, whose
/// internals are visible only to this project; it instantiates no factory and starts no container.
/// The connection string is required in every environment, so the rule is unconditional.
/// </summary>
public sealed class ConnectionStringSettingsValidatorTests
{
    private readonly ConnectionStringSettingsValidator _validator = new();

    [Fact]
    public void Validate_WithPopulatedConnectionString_Passes()
    {
        ConnectionStringSettings settings = new()
        {
            TranslationDatabase = "Host=localhost;Database=lotro_translation;Username=postgres;Password=changeme"
        };

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithMissingConnectionString_FailsNamingTheKey(string? connectionString)
    {
        ConnectionStringSettings settings = new() { TranslationDatabase = connectionString! };

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(ConnectionStringSettings.TranslationDatabase));
        result.Errors.ShouldContain(error =>
            error.ErrorMessage.Contains("ConnectionStrings:TranslationDatabase", StringComparison.Ordinal));
    }
}
