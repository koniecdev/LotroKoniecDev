using FluentValidation.Results;
using LotroKoniecDev.AuthSystem.Persistence.Settings;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Settings;

/// <summary>
/// The AuthSystem connection-string startup guard (M6-05). The connection string is required in every
/// environment, so the rule is unconditional.
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

    [Fact]
    public void Validate_WithQuotedPasswordThatContainsASemicolon_Passes()
    {
        ConnectionStringSettings settings = new()
        {
            AuthDatabase = "Host=db;Database=lotro_auth;Username=deploy;Password=\"Zq8;Wx3=Kp5\""
        };

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeTrue();
    }

    /// <summary>
    /// #1157: Npgsql's own error names the part it could not use, and here that part is a piece of the
    /// password. The message names the key and carries none of the password.
    /// </summary>
    [Theory]
    [InlineData("Host=db;Database=lotro_auth;Username=deploy;Password=Zq8;Wx3=Kp5", "Zq8", "Wx3", "Kp5")]
    [InlineData("Host=db;Database=lotro_auth;Username=deploy;Password=Zq8;Wx3=", "Zq8", "Wx3")]
    [InlineData("Host=db;Port=Zq8Wx3Kp5;Database=lotro_auth;Username=deploy;Password=x", "Zq8Wx3Kp5")]
    [InlineData("Host=db;Database=lotro_auth;Username=deploy;Ssl Mode=Zq8Wx3Kp5;Password=x", "Zq8Wx3Kp5")]
    [InlineData("Host=db;Database=lotro_auth;Username=deploy;Password='Zq8;Wx3Kp5", "Zq8", "Wx3Kp5")]
    [InlineData("postgres://deploy:Zq8Wx3Kp5@db/lotro_auth", "Zq8Wx3Kp5")]
    [InlineData("Host=db;Port=99999999999;Database=lotro_auth;Username=deploy;Password=Zq8Wx3Kp5", "Zq8Wx3Kp5")]
    public void Validate_WithUnreadableConnectionString_FailsNamingTheKeyWithoutThePassword(
        string connectionString,
        params string[] passwordParts)
    {
        ConnectionStringSettings settings = new() { AuthDatabase = connectionString };

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        ValidationFailure failure = result.Errors.ShouldHaveSingleItem();
        failure.ErrorMessage.ShouldContain("ConnectionStrings:AuthDatabase", Case.Sensitive);
        foreach (string passwordPart in passwordParts)
        {
            failure.ErrorMessage.ShouldNotContain(passwordPart, Case.Insensitive);
        }
    }
}
