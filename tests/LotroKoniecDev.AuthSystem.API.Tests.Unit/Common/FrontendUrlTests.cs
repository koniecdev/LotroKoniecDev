using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Common;

/// <summary>
/// The web client's app root is the auth pages' only source of a frontend origin, so a browser never lands
/// on this host's discovery JSON or on a nonsense target.
/// </summary>
public sealed class FrontendUrlTests
{
    [Theory]
    [InlineData("https://lotro-translator.pl", "/auth/login", "https://lotro-translator.pl/auth/login")]
    [InlineData("https://lotro-translator.pl/", "/regulamin", "https://lotro-translator.pl/regulamin")]
    [InlineData("https://localhost:7017", "/auth/login", "https://localhost:7017/auth/login")]
    [InlineData("http://localhost:7017", "/regulamin", "http://localhost:7017/regulamin")]
    public void For_BuildsTheAbsoluteUrl_FromTheConfiguredAppRoot(string appRoot, string path, string expected)
    {
        WebClientSettings webClient = new() { PostLogoutRedirectUris = [appRoot] };

        FrontendUrl.For(webClient, path).ShouldBe(expected);
    }

    [Fact]
    public void For_KeepsTheOriginOnly_WhenTheAppRootCarriesAPath()
    {
        // The app root is also the page users land on after logout, so it may include a path. The
        // frontend routes below are absolute and must not be added to it.
        WebClientSettings webClient = new() { PostLogoutRedirectUris = ["https://lotro-translator.pl/wylogowano"] };

        FrontendUrl.For(webClient, "/auth/login").ShouldBe("https://lotro-translator.pl/auth/login");
    }

    [Fact]
    public void For_IsNull_WhenTheWebClientHasNoConfiguredUri()
    {
        WebClientSettings webClient = new();

        FrontendUrl.For(webClient, "/auth/login").ShouldBeNull();
    }

    /// <summary>
    /// A bare path parses as an absolute <c>file://</c> URI on Unix, so the scheme screen is what keeps
    /// a misconfigured app root from producing a <c>file:///auth/login</c> redirect target.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-an-absolute-url")]
    [InlineData("/app")]
    [InlineData("ftp://lotro-translator.pl")]
    [InlineData("javascript:alert(1)")]
    public void For_IsNull_WhenTheConfiguredAppRootIsNotAnHttpUrl(string appRoot)
    {
        WebClientSettings webClient = new() { PostLogoutRedirectUris = [appRoot] };

        FrontendUrl.For(webClient, "/auth/login").ShouldBeNull();
    }
}
