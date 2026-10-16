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
        // Development never reads the list, because it uses the open AllowAnyOrigin policy, so even a
        // malformed value must not stop the boot. The check is skipped always, not only when the list is
        // empty.
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
    public void Validate_ProductionWithMalformedOrigin_FailsNamingTheEntry(string? origin)
    {
        CorsSettingsValidator validator = CreateValidator(Production);
        // origin! is deliberate: configuration binding can put a null element into the array, and the
        // validator has to reject it, so the test passes one.
        CorsSettings settings = new() { AllowedOrigins = [origin!] };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure => failure.Contains("Cors:AllowedOrigins:0", StringComparison.Ordinal));
    }

    /// <summary>
    /// #1131: the first and the last origin parse and are refused for their user name and password. In
    /// the others a '/' or '?' in the password stops the origin from parsing, or a typo in the scheme
    /// makes it the wrong kind.
    /// </summary>
    [Theory]
    [InlineData(Production, "https://deploy:hunter2@lotro-translator.pl", "hunter2")]
    [InlineData(Production, "https://deploy:hun/ter2@lotro-translator.pl", "hun/ter2")]
    [InlineData(Production, "https://deploy:hunter2?@lotro-translator.pl", "hunter2")]
    [InlineData(Production, "htps://deploy:hunter2@lotro-translator.pl", "hunter2")]
    [InlineData(Staging, "https://deploy:hunter2@staging.lotro-translator.pl", "hunter2")]
    public void Validate_DeployedEnvironmentWithOriginCarryingAPassword_FailsNamingTheEntryWithoutTheValue(
        string environmentName,
        string origin,
        string password)
    {
        CorsSettingsValidator validator = CreateValidator(environmentName);
        CorsSettings settings = new() { AllowedOrigins = ["https://lotro-translator.pl", origin] };

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        string failure = result.Failures.ShouldHaveSingleItem();
        failure.ShouldContain("Cors:AllowedOrigins:1", Case.Sensitive);
        failure.ShouldNotContain(origin, Case.Sensitive);
        failure.ShouldNotContain(password);
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
