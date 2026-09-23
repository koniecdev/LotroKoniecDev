using LotroKoniecDev.TranslationSystem.API.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Settings;

/// <summary>
/// The frontend caller key guard on the TMS API (ADR-0054 §6, #823). A box without the key would quietly
/// put every translator's frontend calls back into one bucket, so a deployed host refuses to boot
/// without it.
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
    {
        IWebHostEnvironment environment = Substitute.For<IWebHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        return new FrontendCallerSettingsValidator(environment);
    }
}
