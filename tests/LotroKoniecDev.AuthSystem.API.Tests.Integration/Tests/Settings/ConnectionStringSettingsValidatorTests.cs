using FluentValidation.Results;
using LotroKoniecDev.AuthSystem.Persistence.Settings;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Settings;

/// <summary>
/// Pure unit coverage for the AuthSystem connection-string startup guard (M6-05). It lives in the
/// integration project only because the AuthSystem has no API unit project; it instantiates no
/// factory and starts no container. The connection string is required in every environment, so the
/// rule is unconditional.
/// </summary>
public sealed class ConnectionStringSettingsValidatorTests
{
    private readonly ConnectionStringSettingsValidator _validator = new();

    [Fact]
    public void Validate_WithPopulatedConnectionString_Passes()
    {
        ConnectionStringSettings settings = new()
        {
            AuthDatabase = "Host=localhost;Database=lotro_auth;Username=postgres;Password=changeme"
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
        ConnectionStringSettings settings = new() { AuthDatabase = connectionString! };

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(ConnectionStringSettings.AuthDatabase));
        result.Errors.ShouldContain(error =>
            error.ErrorMessage.Contains("ConnectionStrings:AuthDatabase", StringComparison.Ordinal));
    }
}
