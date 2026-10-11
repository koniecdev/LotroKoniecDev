using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Common;

/// <summary>
/// The logo on every account page leads here, so the address must be the website of the environment
/// the auth server runs in, never a fixed production one (#1074).
/// </summary>
public sealed class FrontendHomeTests
{
    [Theory]
    [InlineData("https://lotro-translator.pl", "https://lotro-translator.pl/")]
    [InlineData("https://staging.lotro-translator.pl/", "https://staging.lotro-translator.pl/")]
    [InlineData("https://localhost:7017", "https://localhost:7017/")]
    [InlineData("https://lotro-translator.pl/wylogowano", "https://lotro-translator.pl/")]
    public void Url_ConfiguredAppRoot_IsTheRootOfItsOrigin(string appRoot, string expected)
    {
        // Arrange
        IOptions<OpenIddictSettings> options = OptionsWith(new WebClientSettings { PostLogoutRedirectUris = [appRoot] });

        // Act
        FrontendHome frontendHome = new(options);

        // Assert
        frontendHome.Url.ShouldBe(expected);
    }

    [Fact]
    public void Url_NoConfiguredAppRoot_IsNull()
    {
        // Arrange
        IOptions<OpenIddictSettings> options = OptionsWith(new WebClientSettings());

        // Act
        FrontendHome frontendHome = new(options);

        // Assert
        frontendHome.Url.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("/app")]
    [InlineData("javascript:alert(1)")]
    public void Url_AppRootThatIsNotAnHttpUrl_IsNull(string appRoot)
    {
        // Arrange
        IOptions<OpenIddictSettings> options = OptionsWith(new WebClientSettings { PostLogoutRedirectUris = [appRoot] });

        // Act
        FrontendHome frontendHome = new(options);

        // Assert
        frontendHome.Url.ShouldBeNull();
    }

    private static IOptions<OpenIddictSettings> OptionsWith(WebClientSettings webClient) =>
        Microsoft.Extensions.Options.Options.Create(new OpenIddictSettings
        {
            Issuer = "https://auth.lotro-translator.pl",
            WebClient = webClient
        });
}
