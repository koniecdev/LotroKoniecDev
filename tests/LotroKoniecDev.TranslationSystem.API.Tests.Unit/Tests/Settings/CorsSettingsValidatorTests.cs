using LotroKoniecDev.TranslationSystem.API.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Settings;

public sealed class CorsSettingsValidatorTests
{
    private const string Production = "Production";
    private const string Staging = "Staging";
    private const string Development = "Development";
    private const string Testing = "Testing";

    [Theory]
    [InlineData(Development)]
    [InlineData(Testing)]
    public void Validate_NonDeployedEnvironmentWithNoOrigins_Succeeds(string environmentName)
    {
        CorsSettingsValidator validator = CreateValidator(environmentName);
        CorsSettings settings = new() { AllowedOrigins = [] };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validate_DevelopmentWithMalformedOrigin_Succeeds()
    {
        // Development never reads the list (permissive AllowAnyOrigin policy), so even a malformed
        // value must not block boot — the skip is unconditional, not "skip only when empty".
        CorsSettingsValidator validator = CreateValidator(Development);
        CorsSettings settings = new() { AllowedOrigins = ["not-a-url"] };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(Production)]
    [InlineData(Staging)]
    public void Validate_DeployedEnvironmentWithNoOrigins_FailsNamingTheKey(string environmentName)
    {
        CorsSettingsValidator validator = CreateValidator(environmentName);
        CorsSettings settings = new() { AllowedOrigins = [] };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure => failure.Contains("Cors:AllowedOrigins", StringComparison.Ordinal));
        result.Failures.ShouldContain(failure => failure.Contains(environmentName, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ProductionWithSingleValidOrigin_Succeeds()
    {
        CorsSettingsValidator validator = CreateValidator(Production);
        CorsSettings settings = new() { AllowedOrigins = ["https://lotro-translator.pl"] };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ProductionWithMultipleValidOrigins_Succeeds()
    {
        CorsSettingsValidator validator = CreateValidator(Production);
        CorsSettings settings = new()
        {
            AllowedOrigins = ["https://lotro-translator.pl", "https://staging.lotro-translator.pl", "http://localhost:7017"]
        };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://lotro-translator.pl")]
    [InlineData("https://lotro-translator.pl/")]
    [InlineData("https://lotro-translator.pl/api")]
    [InlineData("https://user:pass@lotro-translator.pl")]
    public void Validate_ProductionWithMalformedOrigin_FailsNamingTheKey(string? origin)
    {
        CorsSettingsValidator validator = CreateValidator(Production);
        // origin! — a sparse/explicit-null config binding can place a null element into the array;
        // the validator must reject it, so the test deliberately passes one.
        CorsSettings settings = new() { AllowedOrigins = [origin!] };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure => failure.Contains("Cors:AllowedOrigins", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ProductionWithOneValidAndOneMalformedOrigin_Fails()
    {
        CorsSettingsValidator validator = CreateValidator(Production);
        CorsSettings settings = new() { AllowedOrigins = ["https://lotro-translator.pl", "https://bad.example/path"] };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
    }

    private static CorsSettingsValidator CreateValidator(string environmentName)
    {
        IWebHostEnvironment environment = Substitute.For<IWebHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        return new CorsSettingsValidator(environment);
    }
}
