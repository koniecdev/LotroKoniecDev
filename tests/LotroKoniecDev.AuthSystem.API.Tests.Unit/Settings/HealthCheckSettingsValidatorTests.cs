using LotroKoniecDev.AuthSystem.API.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Settings;

/// <summary>
/// The health check key guard on the auth API (ADR-0058, #853). Without the key the full /health is open to
/// every caller, so a deployed host refuses to boot without it.
/// </summary>
public sealed class HealthCheckSettingsValidatorTests
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
        HealthCheckSettingsValidator validator = CreateValidator(environmentName);
        HealthCheckSettings settings = new();

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
        HealthCheckSettingsValidator validator = CreateValidator(environmentName);
        HealthCheckSettings settings = new() { Key = key };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure => failure.Contains("HealthCheck:Key", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Development)]
    [InlineData(Testing)]
    [InlineData(Production)]
    public void Validate_KeyShorterThanTheMinimum_FailsInEveryEnvironment(string environmentName)
    {
        HealthCheckSettingsValidator validator = CreateValidator(environmentName);
        HealthCheckSettings settings = new() { Key = new string('k', HealthCheckSettingsValidator.MinimumKeyLength - 1) };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure => failure.Contains("at least 32 characters", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HealthCheckSettingsValidator.MinimumKeyLength)]
    [InlineData(44)]
    public void Validate_ProductionWithAKeyOfAtLeastTheMinimum_Succeeds(int keyLength)
    {
        HealthCheckSettingsValidator validator = CreateValidator(Production);
        HealthCheckSettings settings = new() { Key = new string('k', keyLength) };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    private static HealthCheckSettingsValidator CreateValidator(string environmentName)
    {
        IWebHostEnvironment environment = Substitute.For<IWebHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        return new HealthCheckSettingsValidator(environment);
    }
}
