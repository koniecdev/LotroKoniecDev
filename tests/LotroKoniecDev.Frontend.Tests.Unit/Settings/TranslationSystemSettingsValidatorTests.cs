using FluentValidation.Results;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Settings;

public sealed class TranslationSystemSettingsValidatorTests
{
    private const string Development = "Development";
    private const string Testing = "Testing";
    private const string Staging = "Staging";
    private const string Production = "Production";
    private const string BaseUrl = "https://localhost:5002/";

    private readonly TranslationSystemSettingsValidator _validator = CreateValidator(Development);

    [Fact]
    public void Validate_WithAbsoluteHttpsUrl_Passes()
    {
        TranslationSystemSettings settings = new() { BaseUrl = BaseUrl };

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://localhost:5002")]
    public void Validate_WithNonHttpBaseUrl_Fails(string baseUrl)
    {
        TranslationSystemSettings settings = new() { BaseUrl = baseUrl };

        ValidationResult result = _validator.Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(TranslationSystemSettings.BaseUrl));
        result.Errors.ShouldContain(error =>
            error.ErrorMessage.Contains("TranslationSystem:BaseUrl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Development)]
    [InlineData(Testing)]
    public void Validate_NonDeployedEnvironmentWithNoCallerKey_Passes(string environmentName)
    {
        // ADR-0054 §6: these two run against a TMS API whose limiter is off, so the key decides nothing.
        TranslationSystemSettings settings = new() { BaseUrl = BaseUrl, CallerKey = null };

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
        // A box without the key would quietly send every translator's TMS API calls into this
        // container's one bucket, so the boot fails instead.
        TranslationSystemSettings settings = new() { BaseUrl = BaseUrl, CallerKey = callerKey };

        ValidationResult result = CreateValidator(environmentName).Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(TranslationSystemSettings.CallerKey));
        result.Errors.ShouldContain(error =>
            error.ErrorMessage.Contains("TranslationSystem:CallerKey", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Development)]
    [InlineData(Testing)]
    [InlineData(Staging)]
    [InlineData(Production)]
    public void Validate_CallerKeyShorterThanTheMinimum_FailsInEveryEnvironment(string environmentName)
    {
        TranslationSystemSettings settings = new()
        {
            BaseUrl = BaseUrl,
            CallerKey = new string('k', TranslationSystemSettingsValidator.MinimumCallerKeyLength - 1)
        };

        ValidationResult result = CreateValidator(environmentName).Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == nameof(TranslationSystemSettings.CallerKey));
    }

    [Fact]
    public void Validate_ProductionWithACallerKeyOfTheMinimumLength_Passes()
    {
        TranslationSystemSettings settings = new()
        {
            BaseUrl = BaseUrl,
            CallerKey = new string('k', TranslationSystemSettingsValidator.MinimumCallerKeyLength)
        };

        ValidationResult result = CreateValidator(Production).Validate(settings);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(Development, " a-frontend-caller-key-of-at-least-32-characters")]
    [InlineData(Testing, "a-frontend-caller-key-of-at-least-32-characters ")]
    [InlineData(Production, " a-frontend-caller-key-of-at-least-32-characters")]
    [InlineData(Production, "a-frontend-caller-key-of-at-least-32-characters ")]
    [InlineData(Production, "a-frontend-caller-key-of-at-least-32-characters\n")]
    public void Validate_CallerKeyWithWhitespaceAroundIt_FailsInEveryEnvironment(string environmentName, string callerKey)
    {
        TranslationSystemSettings settings = new() { BaseUrl = BaseUrl, CallerKey = callerKey };

        ValidationResult result = CreateValidator(environmentName).Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error =>
            error.PropertyName == nameof(TranslationSystemSettings.CallerKey)
            && error.ErrorMessage.Contains("whitespace", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Development, 0x0A)]
    [InlineData(Testing, 0x0D)]
    [InlineData(Production, 0x00)]
    [InlineData(Production, 0x09)]
    [InlineData(Production, 0x01)]
    [InlineData(Production, 0x1F)]
    [InlineData(Production, 0x7F)]
    [InlineData(Production, 0xA0)]
    [InlineData(Production, 0xE9)]
    [InlineData(Production, 0x2028)]
    [InlineData(Production, 0x1F600)]
    public void Validate_CallerKeyWithACharacterOutsidePrintableAscii_FailsInEveryEnvironment(
        string environmentName,
        int codePoint)
    {
        TranslationSystemSettings settings = new()
        {
            BaseUrl = BaseUrl,
            CallerKey = "a-frontend-caller-key" + char.ConvertFromUtf32(codePoint) + "of-at-least-32-characters"
        };

        ValidationResult result = CreateValidator(environmentName).Validate(settings);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error =>
            error.PropertyName == nameof(TranslationSystemSettings.CallerKey)
            && error.ErrorMessage.Contains("printable ASCII", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("abcdefghijklmnopqrstuvwxyz+ABCDEFGHIJ/012345=")]
    // A space inside the key can be sent and still matches, so it stays allowed (#877).
    [InlineData("a frontend caller key with spaces inside it")]
    public void Validate_ProductionWithAPrintableAsciiCallerKey_Passes(string callerKey)
    {
        TranslationSystemSettings settings = new() { BaseUrl = BaseUrl, CallerKey = callerKey };

        ValidationResult result = CreateValidator(Production).Validate(settings);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ProductionWithEveryPrintableAsciiCharacterInsideTheCallerKey_Passes()
    {
        string everyPrintableAsciiCharacter = new(Enumerable.Range(' ', '~' - ' ' + 1).Select(code => (char)code).ToArray());
        TranslationSystemSettings settings = new()
        {
            BaseUrl = BaseUrl,
            CallerKey = "k" + everyPrintableAsciiCharacter + "k"
        };

        ValidationResult result = CreateValidator(Production).Validate(settings);

        result.IsValid.ShouldBeTrue();
    }

    private static TranslationSystemSettingsValidator CreateValidator(string environmentName)
    {
        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        return new TranslationSystemSettingsValidator(environment);
    }
}
