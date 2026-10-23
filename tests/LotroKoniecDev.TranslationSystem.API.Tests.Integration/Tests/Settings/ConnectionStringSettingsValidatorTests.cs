using FluentValidation.Results;
using LotroKoniecDev.TranslationSystem.Persistence.Settings;
using Shouldly;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Settings;

/// <summary>
/// Pure unit tests, here because only this project sees the Persistence internals. No factory, no
/// container.
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

    [Fact]
    public void Validate_WithQuotedPasswordThatContainsASemicolon_Passes()
    {
        ConnectionStringSettings settings = new()
        {
            TranslationDatabase = "Host=db;Database=lotro_translation;Username=deploy;Password=\"Zq8;Wx3=Kp5\""
        };

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeTrue();
    }

    /// <summary>
    /// Here the part Npgsql's own error would name is a piece of the password (#1157).
    /// </summary>
    [Theory]
    [InlineData("Host=db;Database=lotro_translation;Username=deploy;Password=Zq8;Wx3=Kp5", "Zq8", "Wx3", "Kp5")]
    [InlineData("Host=db;Database=lotro_translation;Username=deploy;Password=Zq8;Wx3=", "Zq8", "Wx3")]
    [InlineData("Host=db;Port=Zq8Wx3Kp5;Database=lotro_translation;Username=deploy;Password=x", "Zq8Wx3Kp5")]
    [InlineData("Host=db;Database=lotro_translation;Username=deploy;Ssl Mode=Zq8Wx3Kp5;Password=x", "Zq8Wx3Kp5")]
    [InlineData("Host=db;Database=lotro_translation;Username=deploy;Password='Zq8;Wx3Kp5", "Zq8", "Wx3Kp5")]
    [InlineData("postgres://deploy:Zq8Wx3Kp5@db/lotro_translation", "Zq8Wx3Kp5")]
    [InlineData("Host=db;Port=99999999999;Database=lotro_translation;Username=deploy;Password=Zq8Wx3Kp5", "Zq8Wx3Kp5")]
    public void Validate_WithUnreadableConnectionString_FailsNamingTheKeyWithoutThePassword(
        string connectionString,
        params string[] passwordParts)
    {
        ConnectionStringSettings settings = new() { TranslationDatabase = connectionString };

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        ValidationFailure failure = result.Errors.ShouldHaveSingleItem();
        failure.ErrorMessage.ShouldContain("ConnectionStrings:TranslationDatabase", Case.Sensitive);
        foreach (string passwordPart in passwordParts)
        {
            failure.ErrorMessage.ShouldNotContain(passwordPart, Case.Insensitive);
        }
    }
}
