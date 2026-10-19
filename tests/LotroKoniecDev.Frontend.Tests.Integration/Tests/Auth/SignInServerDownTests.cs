using System.Net;
using System.Text;
using LotroKoniecDev.Frontend.Tests.Integration.Shared;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Auth;

/// <summary>
/// #1153: a page that needs a login sends the visitor to the sign-in server, and the redirect is built from
/// the sign-in server's discovery document. Each host here has just started, so it holds no copy of that
/// document. While the sign-in server is down, the visitor must end on the "login temporarily unavailable"
/// page that the login button shows (#311), never on the error page.
/// </summary>
public sealed class SignInServerDownTests : IClassFixture<StagingFrontendFactory>
{
    private const string Authority = "https://auth.staging.invalid";
    private const string LoginUnavailableTitle = "<title>Logowanie niedostępne — lotro-translator.pl</title>";
    private const string ErrorTitle = "<title>Błąd — lotro-translator.pl</title>";
    private const string SignOutButton = "Wyloguj";

    private readonly StagingFrontendFactory _factory;

    /// <summary>
    /// Every page and route that needs a login, and a form post to each page that has a form. A form post
    /// reaches the login check before the antiforgery check, so it needs no token.
    /// </summary>
    public static TheoryData<string, string> RoutesThatNeedALogin() => new()
    {
        { "GET", "/dashboard" },
        { "GET", "/import-export" },
        { "GET", "/game-versions?page=2" },
        { "GET", "/editor/0b8f7c1e-5d2a-4c3b-9e8f-1a2b3c4d5e6f" },
        { "GET", "/account" },
        { "GET", "/account/change-password" },
        { "GET", "/account/change-email" },
        { "GET", "/account/export" },
        { "GET", "/account/delete" },
        { "POST", "/import-export" },
        { "POST", "/game-versions" },
        { "POST", "/editor/0b8f7c1e-5d2a-4c3b-9e8f-1a2b3c4d5e6f" },
        { "POST", "/account/change-password" },
        { "POST", "/account/change-email" },
        { "POST", "/account/delete" },
        { "POST", "/account/export/download" }
    };

    [Theory]
    [MemberData(nameof(RoutesThatNeedALogin))]
    public async Task Send_ToARouteThatNeedsALoginWhileTheSignInServerIsDown_EndsOnTheLoginUnavailablePage(
        string method,
        string pathAndQuery)
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(new StandInSignInServer(), new DashboardTranslationSystem());
        using CookieJarBrowser browser = new(host);

        // Act
        using HttpResponseMessage redirect = await browser.SendAsync(new HttpRequestMessage(new HttpMethod(method), pathAndQuery));
        using HttpResponseMessage page = await FollowAsync(browser, redirect);

        // Assert
        redirect.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        redirect.Headers.Location.ShouldNotBeNull().OriginalString
            .ShouldBe($"/auth/login?returnUrl={Uri.EscapeDataString(pathAndQuery)}");
        page.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        string markup = await page.Content.ReadAsStringAsync();
        markup.ShouldContain(LoginUnavailableTitle);
        markup.ShouldNotContain(ErrorTitle);
    }

    /// <summary>
    /// A refused access token marks the session dead, and the next request ends it (#1058). When that request
    /// goes to a page that needs a login, the visitor is sent to sign in again, which needs the sign-in server.
    /// The answer must still delete the cookie, and the page after it must not show the user as signed in.
    /// </summary>
    [Theory]
    [InlineData("GET", "/dashboard")]
    [InlineData("POST", "/game-versions")]
    public async Task Send_ToARouteThatNeedsALoginRightAfterTheSessionEndedWhileTheSignInServerIsDown_EndsTheSessionOnTheLoginUnavailablePage(
        string method,
        string path)
    {
        // Arrange
        DashboardTranslationSystem translationSystem = new();
        using WebApplicationFactory<Program> host = CreateHost(new StandInSignInServer(), translationSystem);
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);
        await LoadAsync(browser, PageRequest("/dashboard"));
        translationSystem.AcceptsTheToken = false;
        await LoadAsync(browser, PageRequest("/dashboard"));

        // Act
        using HttpResponseMessage redirect = await browser.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        using HttpResponseMessage page = await FollowAsync(browser, redirect);

        // Assert
        SetCookies(redirect)
            .ShouldContain(setCookie => setCookie.StartsWith($"{SignedInSession.CookieName}=;", StringComparison.Ordinal));
        page.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        string markup = await page.Content.ReadAsStringAsync();
        markup.ShouldContain(LoginUnavailableTitle);
        markup.ShouldNotContain(SignedInSession.UserName);
        markup.ShouldNotContain(SignOutButton);
        translationSystem.Unauthorized.ShouldBe(1);
    }

    /// <summary>
    /// The page asks the visitor to try again in a moment. Once the sign-in server answers again, reloading
    /// the page starts the sign-in.
    /// </summary>
    [Fact]
    public async Task Get_TheLoginUnavailablePageAgainOnceTheSignInServerAnswers_RedirectsToTheSignInServer()
    {
        // Arrange
        StandInSignInServer signInServer = new();
        using WebApplicationFactory<Program> host = CreateHost(signInServer, new DashboardTranslationSystem());
        using CookieJarBrowser browser = new(host);
        using HttpResponseMessage redirect = await browser.SendAsync(PageRequest("/dashboard"));
        Uri loginUnavailable = redirect.Headers.Location
            ?? throw new InvalidOperationException($"The page answered {redirect.StatusCode} with no redirect.");
        await LoadAsync(browser, PageRequest(loginUnavailable.OriginalString));
        signInServer.IsUp = true;

        // Act
        using HttpResponseMessage reload = await browser.SendAsync(PageRequest(loginUnavailable.OriginalString));

        // Assert
        reload.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        reload.Headers.Location.ShouldNotBeNull().OriginalString.ShouldStartWith($"{Authority}/connect/authorize?");
    }

    [Fact]
    public async Task Get_APageThatNeedsALoginWhileTheSignInServerAnswers_RedirectsToTheSignInServer()
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(
            new StandInSignInServer { IsUp = true },
            new DashboardTranslationSystem());
        using CookieJarBrowser browser = new(host);

        // Act
        using HttpResponseMessage page = await browser.SendAsync(PageRequest("/dashboard"));

        // Assert
        page.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        page.Headers.Location.ShouldNotBeNull().OriginalString.ShouldStartWith($"{Authority}/connect/authorize?");
    }

    /// <summary>
    /// Only a visitor who is not signed in is sent to the sign-in server. A signed-in user's session needs no
    /// call to it, so the page still shows (#1058).
    /// </summary>
    [Fact]
    public async Task Get_APageThatNeedsALoginForASignedInUserWhileTheSignInServerIsDown_ShowsThePage()
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(new StandInSignInServer(), new DashboardTranslationSystem());
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);

        // Act
        using HttpResponseMessage page = await browser.SendAsync(PageRequest("/dashboard"));

        // Assert
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        string markup = await page.Content.ReadAsStringAsync();
        markup.ShouldContain(SignedInSession.UserName);
        markup.ShouldNotContain(LoginUnavailableTitle);
    }

    /// <summary>
    /// The sign-out builds its end-session redirect from the configured address, not from the discovery
    /// document, and its own refresh-token revoke gives up quietly. So it still ends the session.
    /// </summary>
    [Fact]
    public async Task Post_LogoutWhileTheSignInServerIsDown_EndsTheSessionAndRedirectsToTheEndSessionPage()
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(new StandInSignInServer(), new DashboardTranslationSystem());
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);

        // Act
        using HttpResponseMessage logout = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/auth/logout"));

        // Assert
        logout.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        logout.Headers.Location.ShouldNotBeNull().OriginalString.ShouldStartWith($"{Authority}/connect/logout?");
        SetCookies(logout)
            .ShouldContain(setCookie => setCookie.StartsWith($"{SignedInSession.CookieName}=;", StringComparison.Ordinal));
    }

    public SignInServerDownTests(StagingFrontendFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// <see cref="OpenIdConnectOptions.Configuration"/> stays unset, so the OIDC handler builds its real
    /// configuration manager, which has fetched nothing yet.
    /// </summary>
    private WebApplicationFactory<Program> CreateHost(
        StandInSignInServer signInServer,
        DashboardTranslationSystem translationSystem) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options => options.BackchannelHttpHandler = signInServer);
            services.AddHttpClient(DashboardTranslationSystem.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => translationSystem);
        }));

    private static HttpRequestMessage PageRequest(string pathAndQuery) => new(HttpMethod.Get, pathAndQuery);

    /// <summary>Follows a redirect the way a browser does: with a GET, whatever the method before it.</summary>
    private static Task<HttpResponseMessage> FollowAsync(CookieJarBrowser browser, HttpResponseMessage redirect)
    {
        Uri location = redirect.Headers.Location
            ?? throw new InvalidOperationException($"The route answered {redirect.StatusCode} with no redirect.");
        return browser.SendAsync(PageRequest(location.OriginalString));
    }

    /// <summary>
    /// Reads the whole response: a streaming page makes its API calls while it streams, after the headers
    /// have arrived.
    /// </summary>
    private static async Task LoadAsync(CookieJarBrowser browser, HttpRequestMessage request)
    {
        using HttpResponseMessage response = await browser.SendAsync(request);
        await response.Content.ReadAsStringAsync();
    }

    private static string[] SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies) ? [.. setCookies] : [];

    /// <summary>
    /// The sign-in server's back channel. While it is down, every call fails before an answer arrives. While
    /// it is up, it answers its discovery document and nothing else.
    /// </summary>
    private sealed class StandInSignInServer : HttpMessageHandler
    {
        private const string DiscoveryAddress = $"{Authority}/.well-known/openid-configuration";
        private const string DiscoveryDocument = $$"""
            {
              "issuer": "{{Authority}}/",
              "authorization_endpoint": "{{Authority}}/connect/authorize",
              "token_endpoint": "{{Authority}}/connect/token",
              "end_session_endpoint": "{{Authority}}/connect/logout"
            }
            """;

        public bool IsUp { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!IsUp)
            {
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused."));
            }

            HttpResponseMessage response = request.RequestUri?.AbsoluteUri == DiscoveryAddress
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(DiscoveryDocument, Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound);

            return Task.FromResult(response);
        }
    }
}
