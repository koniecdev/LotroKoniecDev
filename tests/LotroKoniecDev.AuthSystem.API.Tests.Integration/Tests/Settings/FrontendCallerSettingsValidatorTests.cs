using LotroKoniecDev.AuthSystem.API.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Settings;

/// <summary>
/// Pure unit coverage for the frontend caller key guard (ADR-0054 §6). It lives in the integration
/// project next to <see cref="CorsSettingsValidatorTests"/>; it instantiates no factory and starts no
/// container. A box without the key would quietly put every visitor's frontend calls back into one
/// bucket, so a deployed host refuses to boot without it.
/// </summary>
public sealed class FrontendCallerSettingsValidatorTests
{
    private const string Production = "Production";
    private const string Staging = "Staging";
    private const string Development = "Development";
    private const string Testing = "Testing";

    [Theory]
    [InlineData(Development)]
    [InlineData(Testing)]
    public void Validate_NonDeployedEnvironmentWithNoKey_Succeeds(string environmentName)
    {
        FrontendCallerSettingsValidator validator = CreateValidator(environmentName);
        FrontendCallerSettings settings = new();

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(Production, null)]
    [InlineData(Production, "")]
    [InlineData(Production, "   ")]
    [InlineData(Staging, null)]
    public void Validate_DeployedEnvironmentWithNoKey_FailsNamingTheKey(string environmentName, string? key)
    {
        FrontendCallerSettingsValidator validator = CreateValidator(environmentName);
        FrontendCallerSettings settings = new() { Key = key };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure => failure.Contains("FrontendCaller:Key", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Development)]
    [InlineData(Testing)]
    [InlineData(Production)]
    public void Validate_KeyShorterThanTheMinimum_FailsInEveryEnvironment(string environmentName)
    {
        FrontendCallerSettingsValidator validator = CreateValidator(environmentName);
        FrontendCallerSettings settings = new() { Key = new string('k', FrontendCallerSettingsValidator.MinimumKeyLength - 1) };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure => failure.Contains("at least 32 characters", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(FrontendCallerSettingsValidator.MinimumKeyLength)]
    [InlineData(44)]
    public void Validate_ProductionWithAKeyOfAtLeastTheMinimum_Succeeds(int keyLength)
    {
        FrontendCallerSettingsValidator validator = CreateValidator(Production);
        FrontendCallerSettings settings = new() { Key = new string('k', keyLength) };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    private static FrontendCallerSettingsValidator CreateValidator(string environmentName)
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
