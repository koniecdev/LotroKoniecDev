using LotroKoniecDev.TranslationSystem.API.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Settings;

/// <summary>
/// The health check key guard on the TMS API (ADR-0058, #853). Without the key the full /health is open to
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

    [Theory]
    [InlineData(" a-health-check-key-of-at-least-32-characters")]
    [InlineData("a-health-check-key-of-at-least-32-characters ")]
    [InlineData("a-health-check-key-of-at-least-32-characters\n")]
    public void Validate_KeyWithWhitespaceAroundIt_FailsBecauseNoCallerCouldSendIt(string key)
    {
        HealthCheckSettingsValidator validator = CreateValidator(Production);
        HealthCheckSettings settings = new() { Key = key };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure => failure.Contains("whitespace", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0x0A)]
    [InlineData(0x0D)]
    [InlineData(0x00)]
    [InlineData(0x09)]
    [InlineData(0x01)]
    [InlineData(0x1F)]
    [InlineData(0x7F)]
    [InlineData(0xA0)]
    [InlineData(0xE9)]
    [InlineData(0x2028)]
    [InlineData(0x1F600)]
    public void Validate_KeyWithACharacterOutsidePrintableAscii_Fails(int codePoint)
    {
        HealthCheckSettingsValidator validator = CreateValidator(Production);
        HealthCheckSettings settings = new()
        {
            Key = "a-health-check-key" + char.ConvertFromUtf32(codePoint) + "of-at-least-32-characters"
        };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure => failure.Contains("printable ASCII", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("abcdefghijklmnopqrstuvwxyz+ABCDEFGHIJ/012345=")]
    // A space inside the key can be sent and still matches, so it stays allowed (#877).
    [InlineData("a health check key with spaces inside it")]
    public void Validate_ProductionWithAPrintableAsciiKey_Succeeds(string key)
    {
        HealthCheckSettingsValidator validator = CreateValidator(Production);
        HealthCheckSettings settings = new() { Key = key };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ProductionWithEveryPrintableAsciiCharacterInsideTheKey_Succeeds()
    {
        HealthCheckSettingsValidator validator = CreateValidator(Production);
        string everyPrintableAsciiCharacter = new(Enumerable.Range(' ', '~' - ' ' + 1).Select(code => (char)code).ToArray());
        HealthCheckSettings settings = new() { Key = "k" + everyPrintableAsciiCharacter + "k" };

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
