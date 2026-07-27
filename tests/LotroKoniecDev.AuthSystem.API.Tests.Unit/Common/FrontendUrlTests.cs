using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Common;

/// <summary>
/// The auth pages link and redirect to the frontend using the web client's app root as the only
/// origin source, so this is what keeps a browser from being dropped on the auth host — whose root
/// serves the API discovery JSON — or on a bogus target when the client is unconfigured.
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
        // The app root doubles as the post-logout landing page, so it may carry a path — the frontend
        // routes below are absolute and must not be appended to it.
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
