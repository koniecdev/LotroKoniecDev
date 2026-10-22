using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Settings;

/// <summary>
/// #1156: proves that the API refuses to start when a sign-in server address carries a user name and
/// password, and that the boot error does not print them. The full matrix of values lives in
/// <c>AuthSettingsValidatorTests</c> in the unit suite.
/// </summary>
[Collection("TranslationApi")]
public sealed class AuthSettingsBootTests
{
    private readonly TranslationSystemApiFactory _factory;

    [Theory]
    [InlineData("Auth:Issuer")]
    [InlineData("Auth:Authority")]
    public void Boot_WithAddressCarryingUserInfo_ShouldFailNamingTheKeyWithoutTheCredentials(string key)
    {
        // Arrange
        using WebApplicationFactory<Program> userInfoHost = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { key, "https://deploy:Zq8Wx3@localhost:5003" }
                });
            });
        });

        // Act
        OptionsValidationException exception =
            Should.Throw<OptionsValidationException>(() => userInfoHost.CreateClient());

        // Assert: the boot log writes the whole exception, inner errors included.
        string bootError = exception.ToString();
        bootError.ShouldContain($"{key} must not carry a user name or password", Case.Sensitive);
        bootError.ShouldNotContain("deploy:", Case.Insensitive);
        bootError.ShouldNotContain("Zq8Wx3", Case.Insensitive);
    }

    public AuthSettingsBootTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }
}
