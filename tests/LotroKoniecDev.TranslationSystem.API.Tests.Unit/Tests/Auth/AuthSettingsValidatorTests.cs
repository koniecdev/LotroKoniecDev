using FluentValidation.Results;
using LotroKoniecDev.TranslationSystem.API.Auth;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Auth;

public sealed class AuthSettingsValidatorTests
{
    private readonly AuthSettingsValidator _validator = new();

    [Fact]
    public void Validate_WithCompleteValidSettings_Passes()
    {
        AuthSettings settings = Settings();

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_WithNullAuthority_Passes()
    {
        // Authority is optional; it falls back to Issuer when absent (EffectiveAuthority).
        AuthSettings settings = Settings(authority: null);

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithMissingIssuer_FailsNamingTheKey(string? issuer)
    {
        AuthSettings settings = Settings(issuer: issuer!);

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(AuthSettings.Issuer));
        result.Errors.ShouldContain(error => error.ErrorMessage.Contains("Auth:Issuer", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://auth.lotro-translator.pl")]
    [InlineData("auth.lotro-translator.pl")]
    public void Validate_WithNonHttpIssuer_FailsNamingTheKey(string issuer)
    {
        AuthSettings settings = Settings(issuer: issuer);

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(AuthSettings.Issuer));
        result.Errors.ShouldContain(error => error.ErrorMessage.Contains("Auth:Issuer", StringComparison.Ordinal));
    }

    /// <summary>
    /// The messages must never print the address (#1131). A '/' in the password stops it parsing; a
    /// scheme typo makes it the wrong kind.
    /// </summary>
    [Theory]
    [InlineData("https://deploy:hun/ter2@auth.lotro-translator.pl", "hun/ter2")]
    [InlineData("htps://deploy:hunter2@auth.lotro-translator.pl", "hunter2")]
    public void Validate_WithNonHttpIssuerCarryingAPassword_FailsNamingTheKeyWithoutTheValue(
        string issuer,
        string password)
    {
        AuthSettings settings = Settings(issuer: issuer);

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        ValidationFailure error = result.Errors.ShouldHaveSingleItem();
        error.ErrorMessage.ShouldContain("Auth:Issuer", Case.Sensitive);
        error.ErrorMessage.ShouldNotContain(issuer, Case.Sensitive);
        error.ErrorMessage.ShouldNotContain(password);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithMissingAudience_FailsNamingTheKey(string? audience)
    {
        AuthSettings settings = Settings(audience: audience!);

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(AuthSettings.Audience));
        result.Errors.ShouldContain(error => error.ErrorMessage.Contains("Auth:Audience", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://auth.lotro-translator.pl")]
    public void Validate_WithNonHttpAuthority_FailsNamingTheKey(string authority)
    {
        AuthSettings settings = Settings(authority: authority);

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(AuthSettings.Authority));
        result.Errors.ShouldContain(error => error.ErrorMessage.Contains("Auth:Authority", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://deploy:hun/ter2@auth.lotro-translator.pl", "hun/ter2")]
    [InlineData("htps://deploy:hunter2@auth.lotro-translator.pl", "hunter2")]
    public void Validate_WithNonHttpAuthorityCarryingAPassword_FailsNamingTheKeyWithoutTheValue(
        string authority,
        string password)
    {
        AuthSettings settings = Settings(authority: authority);

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        ValidationFailure error = result.Errors.ShouldHaveSingleItem();
        error.ErrorMessage.ShouldContain("Auth:Authority", Case.Sensitive);
        error.ErrorMessage.ShouldNotContain(authority, Case.Sensitive);
        error.ErrorMessage.ShouldNotContain(password);
    }

    private static AuthSettings Settings(
        string issuer = "https://auth.lotro-translator.pl",
        string audience = "lotrokoniecdev-api",
        string? authority = "https://auth.lotro-translator.pl") => new()
        {
            Issuer = issuer,
            Audience = audience,
            Authority = authority
        };
}
