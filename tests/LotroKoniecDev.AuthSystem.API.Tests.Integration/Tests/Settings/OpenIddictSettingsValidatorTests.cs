using LotroKoniecDev.AuthSystem.API.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Settings;

/// <summary>
/// Pure unit coverage for the AuthSystem OpenIddict startup guard (M6-05). It lives in the
/// integration project only because the AuthSystem has no API unit project; it instantiates no
/// factory and starts no container.
/// </summary>
public sealed class OpenIddictSettingsValidatorTests
{
    private const string Production = "Production";
    private const string Staging = "Staging";
    private const string Development = "Development";
    private const string Testing = "Testing";

    // The validator only checks these two for presence (IsNullOrWhiteSpace), never for shape, so a
    // plain non-empty placeholder is sufficient and keeps high-entropy strings out of the repo.
    private const string ValidEncryptionKey = "dummy-non-empty-encryption-key";
    private const string ValidSigningKeyXml = "dummy-non-empty-signing-key-xml";
    private const string ValidApiClientSecret = "a-strong-and-sufficiently-long-secret-value";
    private const string ValidIssuer = "https://auth.lotro-translator.pl";

    // Redirect URIs are full callback URLs (path included), so they are validated as absolute http(s)
    // URLs — not as bare CORS origins.
    private static readonly string[] ValidRedirectUris = ["https://lotro-translator.pl/callback"];
    private static readonly string[] ValidPostLogoutRedirectUris = ["https://lotro-translator.pl"];

    [Theory]
    [InlineData(Development)]
    [InlineData(Testing)]
    public void Validate_NonDeployedEnvironmentWithEmptyKeyMaterial_Succeeds(string environmentName)
    {
        OpenIddictSettingsValidator validator = CreateValidator(environmentName);
        OpenIddictSettings settings = SettingsWith(
            encryptionKey: string.Empty,
            signingKeyXml: string.Empty,
            apiClientSecret: string.Empty,
            issuer: "https://localhost:5003");

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(Production)]
    [InlineData(Staging)]
    public void Validate_DeployedEnvironmentWithCompleteValidSettings_Succeeds(string environmentName)
    {
        OpenIddictSettingsValidator validator = CreateValidator(environmentName);
        OpenIddictSettings settings = SettingsWith();

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ProductionWithMissingEncryptionKey_FailsNamingTheKeyAndEnvironment(string? encryptionKey)
    {
        OpenIddictSettingsValidator validator = CreateValidator(Production);
        OpenIddictSettings settings = SettingsWith(encryptionKey: encryptionKey);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("OpenIddict:EncryptionKey:Key", StringComparison.Ordinal)
            && failure.Contains(Production, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ProductionWithMissingSigningKey_FailsNamingTheKeyAndEnvironment(string? signingKeyXml)
    {
        OpenIddictSettingsValidator validator = CreateValidator(Production);
        OpenIddictSettings settings = SettingsWith(signingKeyXml: signingKeyXml);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("OpenIddict:SigningKey:RsaPrivateKeyXml", StringComparison.Ordinal)
            && failure.Contains(Production, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ProductionWithMissingApiClientSecret_FailsNamingTheKeyAndEnvironment(string? apiClientSecret)
    {
        OpenIddictSettingsValidator validator = CreateValidator(Production);
        OpenIddictSettings settings = SettingsWith(apiClientSecret: apiClientSecret);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("OpenIddict:ApiClientSecret", StringComparison.Ordinal)
            && failure.Contains(Production, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ProductionWithTooShortApiClientSecret_FailsNamingTheKey()
    {
        OpenIddictSettingsValidator validator = CreateValidator(Production);
        OpenIddictSettings settings = SettingsWith(apiClientSecret: "too-short");

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("OpenIddict:ApiClientSecret", StringComparison.Ordinal)
            && failure.Contains("at least 32", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ProductionWithApiClientSecretOneCharBelowMinimum_FailsNamingTheKey()
    {
        OpenIddictSettingsValidator validator = CreateValidator(Production);
        OpenIddictSettings settings = SettingsWith(apiClientSecret: new string('a', 31));

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("OpenIddict:ApiClientSecret", StringComparison.Ordinal)
            && failure.Contains("at least 32", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ProductionWithApiClientSecretAtExactMinimum_Succeeds()
    {
        OpenIddictSettingsValidator validator = CreateValidator(Production);
        OpenIddictSettings settings = SettingsWith(apiClientSecret: new string('a', 32));

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ProductionWithMissingIssuer_FailsNamingTheKeyAndEnvironment(string? issuer)
    {
        OpenIddictSettingsValidator validator = CreateValidator(Production);
        OpenIddictSettings settings = SettingsWith(issuer: issuer);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("OpenIddict:Issuer", StringComparison.Ordinal)
            && failure.Contains(Production, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://auth.lotro-translator.pl")]
    [InlineData("auth.lotro-translator.pl")]
    public void Validate_ProductionWithNonHttpIssuer_FailsNamingTheKey(string issuer)
    {
        OpenIddictSettingsValidator validator = CreateValidator(Production);
        OpenIddictSettings settings = SettingsWith(issuer: issuer);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("OpenIddict:Issuer", StringComparison.Ordinal)
            && failure.Contains("absolute", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ProductionWithLocalhostIssuer_FailsNamingTheKey()
    {
        OpenIddictSettingsValidator validator = CreateValidator(Production);
        OpenIddictSettings settings = SettingsWith(issuer: "https://localhost:5003");

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("OpenIddict:Issuer", StringComparison.Ordinal)
            && failure.Contains("localhost", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ProductionWithEverythingMissing_ReportsEveryMissingKey()
    {
        OpenIddictSettingsValidator validator = CreateValidator(Production);
        OpenIddictSettings settings = SettingsWith(
            encryptionKey: string.Empty,
            signingKeyXml: string.Empty,
            apiClientSecret: string.Empty,
            issuer: string.Empty,
            redirectUris: [],
            postLogoutRedirectUris: []);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure => failure.Contains("OpenIddict:EncryptionKey:Key", StringComparison.Ordinal));
        result.Failures.ShouldContain(failure => failure.Contains("OpenIddict:SigningKey:RsaPrivateKeyXml", StringComparison.Ordinal));
        result.Failures.ShouldContain(failure => failure.Contains("OpenIddict:ApiClientSecret", StringComparison.Ordinal));
        result.Failures.ShouldContain(failure => failure.Contains("OpenIddict:Issuer", StringComparison.Ordinal));
        result.Failures.ShouldContain(failure => failure.Contains("OpenIddict:WebClient:RedirectUris", StringComparison.Ordinal));
        result.Failures.ShouldContain(failure => failure.Contains("OpenIddict:WebClient:PostLogoutRedirectUris", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Development)]
    [InlineData(Testing)]
    public void Validate_NonDeployedEnvironmentWithEmptyRedirectUris_Succeeds(string environmentName)
    {
        OpenIddictSettingsValidator validator = CreateValidator(environmentName);
        OpenIddictSettings settings = SettingsWith(redirectUris: [], postLogoutRedirectUris: []);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ProductionWithEmptyRedirectUris_FailsNamingTheKeyAndEnvironment()
    {
        OpenIddictSettingsValidator validator = CreateValidator(Production);
        OpenIddictSettings settings = SettingsWith(redirectUris: []);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("OpenIddict:WebClient:RedirectUris", StringComparison.Ordinal)
            && failure.Contains(Production, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://lotro-translator.pl/callback")]
    [InlineData("lotro-translator.pl/callback")]
    public void Validate_ProductionWithNonHttpRedirectUri_FailsNamingTheKey(string redirectUri)
    {
        OpenIddictSettingsValidator validator = CreateValidator(Production);
        OpenIddictSettings settings = SettingsWith(redirectUris: [redirectUri]);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("OpenIddict:WebClient:RedirectUris", StringComparison.Ordinal)
            && failure.Contains("absolute", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ProductionWithEmptyPostLogoutRedirectUris_FailsNamingTheKeyAndEnvironment()
    {
        OpenIddictSettingsValidator validator = CreateValidator(Production);
        OpenIddictSettings settings = SettingsWith(postLogoutRedirectUris: []);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("OpenIddict:WebClient:PostLogoutRedirectUris", StringComparison.Ordinal)
            && failure.Contains(Production, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://lotro-translator.pl")]
    [InlineData("lotro-translator.pl")]
    public void Validate_ProductionWithNonHttpPostLogoutRedirectUri_FailsNamingTheKey(string postLogoutRedirectUri)
    {
        OpenIddictSettingsValidator validator = CreateValidator(Production);
        OpenIddictSettings settings = SettingsWith(postLogoutRedirectUris: [postLogoutRedirectUri]);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("OpenIddict:WebClient:PostLogoutRedirectUris", StringComparison.Ordinal)
            && failure.Contains("absolute", StringComparison.Ordinal));
    }

    private static OpenIddictSettings SettingsWith(
        string? encryptionKey = ValidEncryptionKey,
        string? signingKeyXml = ValidSigningKeyXml,
        string? apiClientSecret = ValidApiClientSecret,
        string? issuer = ValidIssuer,
        string[]? redirectUris = null,
        string[]? postLogoutRedirectUris = null) => new()
        {
            Issuer = issuer!,
            ApiClientSecret = apiClientSecret!,
            EncryptionKey = new EncryptionKeySettings { Key = encryptionKey! },
            SigningKey = new SigningKeySettings { RsaPrivateKeyXml = signingKeyXml! },
            WebClient = new WebClientSettings
            {
                RedirectUris = redirectUris ?? ValidRedirectUris,
                PostLogoutRedirectUris = postLogoutRedirectUris ?? ValidPostLogoutRedirectUris
            }
        };

    private static OpenIddictSettingsValidator CreateValidator(string environmentName)
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
