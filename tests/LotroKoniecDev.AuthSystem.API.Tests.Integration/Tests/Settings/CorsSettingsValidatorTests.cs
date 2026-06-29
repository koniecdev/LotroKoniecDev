using LotroKoniecDev.AuthSystem.API.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Settings;

/// <summary>
/// Pure unit coverage for the AuthSystem CORS startup guard (M6-03). It lives in the integration
/// project only because the AuthSystem has no API unit project; it instantiates no factory and
/// starts no container.
/// </summary>
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
    }

    [Fact]
    public void Validate_ProductionWithMultipleValidOrigins_Succeeds()
    {
        CorsSettingsValidator validator = CreateValidator(Production);
        CorsSettings settings = new()
        {
            AllowedOrigins = ["https://lotro-translator.pl", "https://staging.lotro-translator.pl"]
        };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
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

    private static CorsSettingsValidator CreateValidator(string environmentName)
        => new(new FakeWebHostEnvironment(environmentName));
}

file sealed class FakeWebHostEnvironment : IWebHostEnvironment
{
    public FakeWebHostEnvironment(string environmentName)
    {
        EnvironmentName = environmentName;
    }

    public string EnvironmentName { get; set; }
    public string ApplicationName { get; set; } = "auth-tests";
    public string ContentRootPath { get; set; } = string.Empty;
    public IFileProvider ContentRootFileProvider { get; set; } = null!;
    public string WebRootPath { get; set; } = string.Empty;
    public IFileProvider WebRootFileProvider { get; set; } = null!;
}
