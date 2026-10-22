using LotroKoniecDev.AuthSystem.API.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Settings;

/// <summary>
/// The AuthSystem OpenIddict startup guard (M6-05).
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

    [Theory]
    [InlineData(Production, "https://deploy:hun/ter2@auth.lotro-translator.pl", "hun/ter2")]
    [InlineData(Production, "https://deploy:hunter2#@auth.lotro-translator.pl", "hunter2")]
    [InlineData(Production, "htps://deploy:hunter2@auth.lotro-translator.pl", "hunter2")]
    [InlineData(Staging, "https://deploy:hun/ter2@auth.staging.lotro-translator.pl", "hun/ter2")]
    public void Validate_DeployedEnvironmentWithNonHttpIssuerCarryingAPassword_FailsNamingTheKeyWithoutTheValue(
        string environmentName,
        string issuer,
        string password)
    {
        OpenIddictSettingsValidator validator = CreateValidator(environmentName);
        OpenIddictSettings settings = SettingsWith(issuer: issuer);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        string failure = result.Failures.ShouldHaveSingleItem();
        failure.ShouldContain("OpenIddict:Issuer", Case.Sensitive);
        failure.ShouldContain("absolute http(s) URL", Case.Sensitive);
        failure.ShouldContain(environmentName, Case.Sensitive);
        failure.ShouldNotContain(issuer, Case.Sensitive);
        failure.ShouldNotContain(password);
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
    public void Validate_ProductionWithNonHttpRedirectUri_FailsNamingTheEntry(string redirectUri)
    {
        OpenIddictSettingsValidator validator = CreateValidator(Production);
        OpenIddictSettings settings = SettingsWith(redirectUris: [redirectUri]);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("OpenIddict:WebClient:RedirectUris:0", StringComparison.Ordinal)
            && failure.Contains("absolute", StringComparison.Ordinal)
            && !failure.Contains(redirectUri, StringComparison.Ordinal));
    }

    /// <summary>
    /// #1131: a '/', '?', '#' or second '@' in the password stops the address from parsing, and so does a
    /// port over 65535. A typo in the scheme makes it an address of the wrong kind. All of them reach the
    /// message that used to print the value.
    /// </summary>
    [Theory]
    [InlineData(Production, "https://deploy:hun/ter2@lotro-translator.pl/callback", "hun/ter2")]
    [InlineData(Production, "https://deploy:hunter2?@lotro-translator.pl/callback", "hunter2")]
    [InlineData(Production, "https://deploy:hunter2#@lotro-translator.pl/callback", "hunter2")]
    [InlineData(Production, "https://deploy:hun@ter2@lotro-translator.pl/callback", "hun@ter2")]
    [InlineData(Production, "https://deploy:hunter2@lotro-translator.pl:99999/callback", "hunter2")]
    [InlineData(Production, "htps://deploy:hunter2@lotro-translator.pl/callback", "hunter2")]
    [InlineData(Staging, "https://deploy:hun/ter2@staging.lotro-translator.pl/callback", "hun/ter2")]
    public void Validate_DeployedEnvironmentWithNonHttpRedirectUriCarryingAPassword_FailsNamingTheEntryWithoutTheValue(
        string environmentName,
        string redirectUri,
        string password)
    {
        OpenIddictSettingsValidator validator = CreateValidator(environmentName);
        OpenIddictSettings settings = SettingsWith(redirectUris: ["https://lotro-translator.pl/callback", redirectUri]);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        string failure = result.Failures.ShouldHaveSingleItem();
        failure.ShouldContain("OpenIddict:WebClient:RedirectUris:1", Case.Sensitive);
        failure.ShouldContain("absolute http(s) URL", Case.Sensitive);
        failure.ShouldContain(environmentName, Case.Sensitive);
        failure.ShouldNotContain(redirectUri, Case.Sensitive);
        failure.ShouldNotContain(password);
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
    public void Validate_ProductionWithNonHttpPostLogoutRedirectUri_FailsNamingTheEntry(string postLogoutRedirectUri)
    {
        OpenIddictSettingsValidator validator = CreateValidator(Production);
        OpenIddictSettings settings = SettingsWith(postLogoutRedirectUris: [postLogoutRedirectUri]);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("OpenIddict:WebClient:PostLogoutRedirectUris:0", StringComparison.Ordinal)
            && failure.Contains("absolute", StringComparison.Ordinal)
            && !failure.Contains(postLogoutRedirectUri, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Production, "https://deploy:hun/ter2@lotro-translator.pl", "hun/ter2")]
    [InlineData(Production, "https://deploy:hunter2?@lotro-translator.pl", "hunter2")]
    [InlineData(Production, "https://deploy:hunter2#@lotro-translator.pl", "hunter2")]
    [InlineData(Production, "https://deploy:hun@ter2@lotro-translator.pl", "hun@ter2")]
    [InlineData(Production, "htps://deploy:hunter2@lotro-translator.pl", "hunter2")]
    [InlineData(Staging, "https://deploy:hun/ter2@staging.lotro-translator.pl", "hun/ter2")]
    public void Validate_DeployedEnvironmentWithNonHttpPostLogoutRedirectUriCarryingAPassword_FailsNamingTheEntryWithoutTheValue(
        string environmentName,
        string postLogoutRedirectUri,
        string password)
    {
        OpenIddictSettingsValidator validator = CreateValidator(environmentName);
        OpenIddictSettings settings = SettingsWith(
            postLogoutRedirectUris: [postLogoutRedirectUri, "https://lotro-translator.pl/signed-out"]);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        string failure = result.Failures.ShouldHaveSingleItem();
        failure.ShouldContain("OpenIddict:WebClient:PostLogoutRedirectUris:0", Case.Sensitive);
        failure.ShouldContain("absolute http(s) URL", Case.Sensitive);
        failure.ShouldContain(environmentName, Case.Sensitive);
        failure.ShouldNotContain(postLogoutRedirectUri, Case.Sensitive);
        failure.ShouldNotContain(password);
    }

    [Theory]
    [InlineData(Production, "https://deploy:hunter2@auth.lotro-translator.pl", "deploy:hunter2")]
    [InlineData(Staging, "https://deploy:hunter2@auth.staging.lotro-translator.pl", "deploy:hunter2")]
    [InlineData(Production, "https://deploy@auth.lotro-translator.pl", "deploy@")]
    [InlineData(Production, "https://:hunter2@auth.lotro-translator.pl", "hunter2")]
    [InlineData(Production, "https://deploy:@auth.lotro-translator.pl", "deploy:")]
    [InlineData(Production, "HTTPS://deploy:hunter2@auth.lotro-translator.pl", "hunter2")]
    [InlineData(Production, "http://deploy:hunter2@auth.lotro-translator.pl:8080", "hunter2")]
    [InlineData(Production, "https://deploy:hunter2@localhost:5003", "hunter2")]
    public void Validate_DeployedEnvironmentWithIssuerCarryingUserInfo_FailsNamingTheKeyWithoutTheCredentials(
        string environmentName,
        string issuer,
        string credentials)
    {
        OpenIddictSettingsValidator validator = CreateValidator(environmentName);
        OpenIddictSettings settings = SettingsWith(issuer: issuer);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        string failure = result.Failures.ShouldHaveSingleItem();
        failure.ShouldContain("OpenIddict:Issuer", Case.Sensitive);
        failure.ShouldContain("user name or password", Case.Sensitive);
        failure.ShouldContain(environmentName, Case.Sensitive);
        failure.ShouldNotContain(credentials);
    }

    [Theory]
    [InlineData(Production, "https://deploy:hunter2@lotro-translator.pl/callback", "deploy:hunter2")]
    [InlineData(Staging, "https://deploy:hunter2@staging.lotro-translator.pl/callback", "deploy:hunter2")]
    [InlineData(Production, "https://deploy@lotro-translator.pl/callback", "deploy@")]
    [InlineData(Production, "https://:hunter2@lotro-translator.pl/callback", "hunter2")]
    public void Validate_DeployedEnvironmentWithRedirectUriCarryingUserInfo_FailsNamingTheEntryWithoutTheCredentials(
        string environmentName,
        string redirectUri,
        string credentials)
    {
        OpenIddictSettingsValidator validator = CreateValidator(environmentName);
        OpenIddictSettings settings = SettingsWith(redirectUris: ["https://lotro-translator.pl/callback", redirectUri]);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        string failure = result.Failures.ShouldHaveSingleItem();
        failure.ShouldContain("OpenIddict:WebClient:RedirectUris:1", Case.Sensitive);
        failure.ShouldContain("user name or password", Case.Sensitive);
        failure.ShouldContain(environmentName, Case.Sensitive);
        failure.ShouldNotContain(credentials);
    }

    [Theory]
    [InlineData(Production, "https://deploy:hunter2@lotro-translator.pl", "deploy:hunter2")]
    [InlineData(Staging, "https://deploy:hunter2@staging.lotro-translator.pl", "deploy:hunter2")]
    [InlineData(Production, "https://deploy@lotro-translator.pl", "deploy@")]
    [InlineData(Production, "https://:hunter2@lotro-translator.pl", "hunter2")]
    [InlineData(Production, "  https://deploy:hunter2@lotro-translator.pl  ", "hunter2")]
    [InlineData(Production, @"https:\\deploy:hunter2@lotro-translator.pl", "hunter2")]
    public void Validate_DeployedEnvironmentWithPostLogoutRedirectUriCarryingUserInfo_FailsNamingTheEntryWithoutTheCredentials(
        string environmentName,
        string postLogoutRedirectUri,
        string credentials)
    {
        OpenIddictSettingsValidator validator = CreateValidator(environmentName);
        OpenIddictSettings settings = SettingsWith(
            postLogoutRedirectUris: [postLogoutRedirectUri, "https://lotro-translator.pl/signed-out"]);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        string failure = result.Failures.ShouldHaveSingleItem();
        failure.ShouldContain("OpenIddict:WebClient:PostLogoutRedirectUris:0", Case.Sensitive);
        failure.ShouldContain("user name or password", Case.Sensitive);
        failure.ShouldContain(environmentName, Case.Sensitive);
        failure.ShouldNotContain(credentials);
    }

    [Fact]
    public void Validate_ProductionWithUserInfoInEveryAddress_ReportsEveryAddress()
    {
        OpenIddictSettingsValidator validator = CreateValidator(Production);
        OpenIddictSettings settings = SettingsWith(
            issuer: "https://deploy:hunter2@auth.lotro-translator.pl",
            redirectUris: ["https://deploy:hunter2@lotro-translator.pl/callback"],
            postLogoutRedirectUris: ["https://deploy:hunter2@lotro-translator.pl"]);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.Count().ShouldBe(3);
        result.Failures.ShouldContain(failure => failure.Contains("OpenIddict:Issuer", StringComparison.Ordinal));
        result.Failures.ShouldContain(failure => failure.Contains("OpenIddict:WebClient:RedirectUris:0", StringComparison.Ordinal));
        result.Failures.ShouldContain(failure => failure.Contains("OpenIddict:WebClient:PostLogoutRedirectUris:0", StringComparison.Ordinal));
    }

    /// <summary>
    /// Public addresses are checked only outside Development and Testing, where they point at localhost
    /// (ADR-0008 §3, point 6).
    /// </summary>
    [Theory]
    [InlineData(Development)]
    [InlineData(Testing)]
    public void Validate_NonDeployedEnvironmentWithUserInfoInEveryAddress_Succeeds(string environmentName)
    {
        OpenIddictSettingsValidator validator = CreateValidator(environmentName);
        OpenIddictSettings settings = SettingsWith(
            issuer: "https://deploy:hunter2@localhost:5003",
            redirectUris: ["https://deploy:hunter2@localhost:7017/callback"],
            postLogoutRedirectUris: ["https://deploy:hunter2@localhost:7017"]);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    /// <summary>
    /// Only an '@' before the host carries a user name or password. One in the path, the query or the
    /// fragment is part of an ordinary address, and the server starts as before.
    /// </summary>
    [Theory]
    [InlineData(Production, "https://lotro-translator.pl/callback?contact=team@lotro-translator.pl")]
    [InlineData(Production, "https://lotro-translator.pl/users/@me")]
    [InlineData(Production, "https://lotro-translator.pl/callback#deploy:hunter2@lotro-translator.pl")]
    [InlineData(Production, "https://lotro-translator.pl/a%40b")]
    [InlineData(Staging, "https://staging.lotro-translator.pl/callback?contact=team@lotro-translator.pl")]
    public void Validate_DeployedEnvironmentWithAtSignAfterTheHost_Succeeds(string environmentName, string address)
    {
        OpenIddictSettingsValidator validator = CreateValidator(environmentName);
        OpenIddictSettings settings = SettingsWith(redirectUris: [address], postLogoutRedirectUris: [address]);

        ValidateOptionsResult result = validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
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
    {
        IWebHostEnvironment environment = Substitute.For<IWebHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        return new OpenIddictSettingsValidator(environment);
    }
}
