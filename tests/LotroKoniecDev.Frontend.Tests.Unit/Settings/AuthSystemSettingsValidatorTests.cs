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

    private static AuthSystemSettings Settings(
        string authority = "https://localhost:5003",
        string callbackPath = "/callback",
        IReadOnlyList<string>? scopes = null) => new()
    {
        BaseUrl = "https://localhost:5003/",
        Authority = authority,
        ClientId = "lotrokoniecdev-web",
        CallbackPath = callbackPath,
        SignedOutCallbackPath = "/signout-callback-oidc",
        Scopes = scopes ?? ["openid", "profile", "api"]
    };
}
