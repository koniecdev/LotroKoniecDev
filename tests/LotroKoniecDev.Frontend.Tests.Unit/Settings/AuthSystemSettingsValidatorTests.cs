using FluentValidation.Results;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Settings;

public sealed class AuthSystemSettingsValidatorTests
{
    private const string Development = "Development";
    private const string Testing = "Testing";
    private const string Staging = "Staging";
    private const string Production = "Production";

    private readonly AuthSystemSettingsValidator _validator = CreateValidator(Development);

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

    [Theory]
    [InlineData(Development)]
    [InlineData(Testing)]
    public void Validate_NonDeployedEnvironmentWithNoCallerKey_Passes(string environmentName)
    {
        // ADR-0054 §6: these two run against an auth API whose limiter is off, so the key decides nothing.
        AuthSystemSettings settings = Settings(callerKey: null);

        ValidationResult result = CreateValidator(environmentName).Validate(settings);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(Staging, null)]
    [InlineData(Staging, "")]
    [InlineData(Production, null)]
    [InlineData(Production, "   ")]
    public void Validate_DeployedEnvironmentWithNoCallerKey_FailsNamingTheKey(string environmentName, string? callerKey)
    {
        // A box without the key would quietly send every visitor's auth API calls into this container's
        // one bucket, so the boot fails instead.
        AuthSystemSettings settings = Settings(callerKey: callerKey);

        ValidationResult result = CreateValidator(environmentName).Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(AuthSystemSettings.CallerKey));
        result.Errors.ShouldContain(error =>
            error.ErrorMessage.Contains("AuthSystem:CallerKey", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Development)]
    [InlineData(Production)]
    public void Validate_CallerKeyShorterThanTheMinimum_FailsInEveryEnvironment(string environmentName)
    {
        AuthSystemSettings settings = Settings(callerKey: new string('k', AuthSystemSettingsValidator.MinimumCallerKeyLength - 1));

        ValidationResult result = CreateValidator(environmentName).Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(AuthSystemSettings.CallerKey));
    }

    [Fact]
    public void Validate_ProductionWithACallerKeyOfTheMinimumLength_Passes()
    {
        AuthSystemSettings settings = Settings(callerKey: new string('k', AuthSystemSettingsValidator.MinimumCallerKeyLength));

        ValidationResult result = CreateValidator(Production).Validate(settings);

        result.IsValid.ShouldBeTrue();
    }

    private static AuthSystemSettingsValidator CreateValidator(string environmentName)
    {
        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        return new AuthSystemSettingsValidator(environment);
    }

    private static AuthSystemSettings Settings(
        string baseUrl = "https://localhost:5003/",
        string authority = "https://localhost:5003",
        string clientId = "lotrokoniecdev-web",
        string callbackPath = "/callback",
        IReadOnlyList<string>? scopes = null,
        string? callerKey = null) => new()
        {
            BaseUrl = baseUrl,
            Authority = authority,
            ClientId = clientId,
            CallbackPath = callbackPath,
            SignedOutCallbackPath = "/signout-callback-oidc",
            Scopes = scopes ?? ["openid", "profile", "api"],
            CallerKey = callerKey
        };
}
