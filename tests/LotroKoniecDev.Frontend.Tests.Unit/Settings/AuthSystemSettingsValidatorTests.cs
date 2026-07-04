using FluentValidation.Results;
using LotroKoniecDev.Frontend.Settings;

namespace LotroKoniecDev.Frontend.Tests.Unit.Settings;

public sealed class AuthSystemSettingsValidatorTests
{
    private readonly AuthSystemSettingsValidator _validator = new();

    [Fact]
    public void Validate_WithCompleteValidSettings_Passes()
    {
        AuthSystemSettings settings = Settings();

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://localhost:5003")]
    public void Validate_WithNonHttpAuthority_Fails(string authority)
    {
        AuthSystemSettings settings = Settings(authority: authority);

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(AuthSystemSettings.Authority));
    }

    [Theory]
    [InlineData("")]
    [InlineData("callback")]
    public void Validate_WithNonRootedCallbackPath_Fails(string callbackPath)
    {
        AuthSystemSettings settings = Settings(callbackPath: callbackPath);

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(AuthSystemSettings.CallbackPath));
    }

    [Fact]
    public void Validate_WithNoScopes_Fails()
    {
        AuthSystemSettings settings = Settings(scopes: []);

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(AuthSystemSettings.Scopes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://localhost:5003")]
    public void Validate_WithNonHttpBaseUrl_FailsNamingTheKey(string baseUrl)
    {
        AuthSystemSettings settings = Settings(baseUrl: baseUrl);

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(AuthSystemSettings.BaseUrl));
        result.Errors.ShouldContain(error =>
            error.ErrorMessage.Contains("AuthSystem:BaseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WithMissingClientId_FailsNamingTheKey()
    {
        AuthSystemSettings settings = Settings(clientId: "");

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(AuthSystemSettings.ClientId));
        result.Errors.ShouldContain(error =>
            error.ErrorMessage.Contains("AuthSystem:ClientId", StringComparison.Ordinal));
    }

    private static AuthSystemSettings Settings(
        string baseUrl = "https://localhost:5003/",
        string authority = "https://localhost:5003",
        string clientId = "lotrokoniecdev-web",
        string callbackPath = "/callback",
        IReadOnlyList<string>? scopes = null) => new()
        {
            BaseUrl = baseUrl,
            Authority = authority,
            ClientId = clientId,
            CallbackPath = callbackPath,
            SignedOutCallbackPath = "/signout-callback-oidc",
            Scopes = scopes ?? ["openid", "profile", "api"]
        };
}
