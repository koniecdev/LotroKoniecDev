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

    /// <summary>
    /// #1156: a failed read of the sign-in server's keys and a token from the wrong issuer both print the
    /// address in full, and the log goes to Grafana.
    /// </summary>
    [Theory]
    [InlineData("https://deploy:hunter2@auth.lotro-translator.pl", "deploy:hunter2")]
    [InlineData("https://deploy@auth.lotro-translator.pl", "deploy@")]
    [InlineData("https://:hunter2@auth.lotro-translator.pl", "hunter2")]
    [InlineData("https://deploy:@auth.lotro-translator.pl", "deploy:")]
    [InlineData("HTTPS://deploy:hunter2@auth.lotro-translator.pl", "hunter2")]
    [InlineData("http://deploy:hunter2@auth-api:8080", "hunter2")]
    [InlineData("https://deploy:hunter2@localhost:5003", "hunter2")]
    [InlineData("https://deploy:hunter2@auth.lotro-translator.pl/realm?x=1", "hunter2")]
    [InlineData("  https://deploy:hunter2@auth.lotro-translator.pl  ", "hunter2")]
    [InlineData(@"https:\\deploy:hunter2@auth.lotro-translator.pl", "hunter2")]
    public void Validate_WithIssuerCarryingUserInfo_FailsNamingTheKeyWithoutTheCredentials(
        string issuer,
        string credentials)
    {
        AuthSettings settings = Settings(issuer: issuer);

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        ValidationFailure error = result.Errors.ShouldHaveSingleItem();
        error.PropertyName.ShouldBe(nameof(AuthSettings.Issuer));
        error.ErrorMessage.ShouldContain("Auth:Issuer must not carry a user name or password", Case.Sensitive);
        error.ErrorMessage.ShouldNotContain(credentials);
    }

    /// <summary>
    /// The issuer is also the address the keys are read from when Authority is left out.
    /// </summary>
    [Fact]
    public void Validate_WithIssuerCarryingUserInfoAndNoAuthority_FailsNamingTheIssuer()
    {
        AuthSettings settings = Settings(issuer: "https://deploy:hunter2@auth.lotro-translator.pl", authority: null);

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        ValidationFailure error = result.Errors.ShouldHaveSingleItem();
        error.ErrorMessage.ShouldContain("Auth:Issuer must not carry a user name or password", Case.Sensitive);
        error.ErrorMessage.ShouldNotContain("hunter2");
    }

    [Theory]
    [InlineData("https://deploy:hunter2@auth.lotro-translator.pl", "deploy:hunter2")]
    [InlineData("https://deploy@auth.lotro-translator.pl", "deploy@")]
    [InlineData("https://:hunter2@auth.lotro-translator.pl", "hunter2")]
    [InlineData("https://deploy:@auth.lotro-translator.pl", "deploy:")]
    [InlineData("HTTPS://deploy:hunter2@auth.lotro-translator.pl", "hunter2")]
    [InlineData("http://deploy:hunter2@auth-api:8080", "hunter2")]
    [InlineData("https://deploy:hunter2@localhost:5003", "hunter2")]
    [InlineData("https://deploy:hunter2@auth.lotro-translator.pl/realm?x=1", "hunter2")]
    [InlineData("  https://deploy:hunter2@auth.lotro-translator.pl  ", "hunter2")]
    [InlineData(@"https:\\deploy:hunter2@auth.lotro-translator.pl", "hunter2")]
    public void Validate_WithAuthorityCarryingUserInfo_FailsNamingTheKeyWithoutTheCredentials(
        string authority,
        string credentials)
    {
        AuthSettings settings = Settings(authority: authority);

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        ValidationFailure error = result.Errors.ShouldHaveSingleItem();
        error.PropertyName.ShouldBe(nameof(AuthSettings.Authority));
        error.ErrorMessage.ShouldContain("Auth:Authority must not carry a user name or password", Case.Sensitive);
        error.ErrorMessage.ShouldNotContain(credentials);
    }

    [Fact]
    public void Validate_WithIssuerAndAuthorityCarryingUserInfo_FailsNamingBothKeys()
    {
        AuthSettings settings = Settings(
            issuer: "https://deploy:hunter2@auth.lotro-translator.pl",
            authority: "https://deploy:hunter2@auth.lotro-translator.pl");

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.Count.ShouldBe(2);
        result.Errors.ShouldContain(error =>
            error.ErrorMessage.Contains("Auth:Issuer must not carry a user name or password", StringComparison.Ordinal));
        result.Errors.ShouldContain(error =>
            error.ErrorMessage.Contains("Auth:Authority must not carry a user name or password", StringComparison.Ordinal));
        result.Errors.ShouldAllBe(error => !error.ErrorMessage.Contains("hunter2"));
    }

    /// <summary>
    /// Only an '@' before the host carries a user name or password. One in the path, the query or the
    /// fragment is part of an ordinary address. An empty user part holds no secret. The values the dev
    /// host, the containers and the boxes use start the API as before.
    /// </summary>
    [Theory]
    [InlineData("https://localhost:5003")]
    [InlineData("http://auth-api:8080")]
    [InlineData("https://auth.lotro-translator.pl/")]
    [InlineData("https://auth.lotro-translator.pl/realm@lotro")]
    [InlineData("https://auth.lotro-translator.pl/realm%40lotro")]
    [InlineData("https://auth.lotro-translator.pl/?contact=deploy@lotro-translator.pl")]
    [InlineData("https://auth.lotro-translator.pl/#deploy@lotro")]
    [InlineData("https://@auth.lotro-translator.pl")]
    public void Validate_WithAddressWithoutUserInfo_Passes(string address)
    {
        AuthSettings settings = Settings(issuer: address, authority: address);

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeTrue();
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
