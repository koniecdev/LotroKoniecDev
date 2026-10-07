using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
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

    // A redirect URI is a full callback URL with a path, so it is checked as an absolute http or https
    // URL and not as a plain CORS origin.
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
            postLogoutRedirectUris: [],
            accessTokenLifetimeMinutes: 0);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure => failure.Contains("OpenIddict:EncryptionKey:Key", StringComparison.Ordinal));
        result.Failures.ShouldContain(failure => failure.Contains("OpenIddict:SigningKey:RsaPrivateKeyXml", StringComparison.Ordinal));
        result.Failures.ShouldContain(failure => failure.Contains("OpenIddict:ApiClientSecret", StringComparison.Ordinal));
        result.Failures.ShouldContain(failure => failure.Contains("OpenIddict:Issuer", StringComparison.Ordinal));
        result.Failures.ShouldContain(failure => failure.Contains("OpenIddict:WebClient:RedirectUris", StringComparison.Ordinal));
        result.Failures.ShouldContain(failure => failure.Contains("OpenIddict:WebClient:PostLogoutRedirectUris", StringComparison.Ordinal));
        result.Failures.ShouldContain(failure => failure.Contains("OpenIddict:AccessTokenLifetimeMinutes", StringComparison.Ordinal));
    }

    /// <summary>
    /// #1025: a client renews a token a minute before it runs out, so a token that lives a minute or less
    /// makes it renew the sign-in on every page, and zero or less hands out tokens that are already dead.
    /// That breaks every environment alike, so Development and Testing are checked too.
    /// </summary>
    [Theory]
    [InlineData(Production, 1)]
    [InlineData(Production, 0)]
    [InlineData(Production, -1)]
    [InlineData(Production, int.MinValue)]
    [InlineData(Staging, 1)]
    [InlineData(Development, 1)]
    [InlineData(Development, 0)]
    [InlineData(Testing, 1)]
    [InlineData(Testing, -5)]
    public void Validate_AccessTokenLifetimeUnderTwoMinutes_FailsNamingTheKeyAndEnvironment(
        string environmentName,
        int accessTokenLifetimeMinutes)
    {
        OpenIddictSettingsValidator validator = CreateValidator(environmentName);
        OpenIddictSettings settings = SettingsWith(accessTokenLifetimeMinutes: accessTokenLifetimeMinutes);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        string failure = result.Failures.ShouldHaveSingleItem();
        failure.ShouldContain("OpenIddict:AccessTokenLifetimeMinutes", Case.Sensitive);
        failure.ShouldContain(environmentName, Case.Sensitive);
        failure.ShouldContain("at least 2", Case.Sensitive);
    }

    [Theory]
    [InlineData(Production, 2)]
    [InlineData(Production, 5)]
    [InlineData(Staging, 2)]
    [InlineData(Development, 2)]
    [InlineData(Testing, 2)]
    [InlineData(Testing, 60)]
    public void Validate_AccessTokenLifetimeOfAtLeastTwoMinutes_Succeeds(
        string environmentName,
        int accessTokenLifetimeMinutes)
    {
        OpenIddictSettingsValidator validator = CreateValidator(environmentName);
        OpenIddictSettings settings = SettingsWith(accessTokenLifetimeMinutes: accessTokenLifetimeMinutes);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
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
        string[]? postLogoutRedirectUris = null,
        int accessTokenLifetimeMinutes = 5) => new()
        {
            Issuer = issuer!,
            ApiClientSecret = apiClientSecret!,
            AccessTokenLifetimeMinutes = accessTokenLifetimeMinutes,
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
