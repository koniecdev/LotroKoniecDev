using System.Net;
using System.Text;
using LotroKoniecDev.Frontend.Tests.Integration.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Auth;

/// <summary>
/// #1153: each host has just started, so it holds no copy of the discovery document. While the sign-in
/// server is down, the visitor must end on the "login temporarily unavailable" page (#311), never on the
/// error page.
/// </summary>
public sealed class SignInServerDownTests : IClassFixture<ProductionFrontendFactory>
{
    private const string Authority = "https://auth.staging.invalid";
    private const string LoginPath = "/auth/login";
    private const string LoginUnavailableTitle = "<title>Logowanie niedostępne — lotro-translator.pl</title>";
    private const string ErrorTitle = "<title>Błąd — lotro-translator.pl</title>";
    private const string SignOutButton = "Wyloguj";

    /// <summary>The origin <see cref="CookieJarBrowser"/> sends to, for a redirect that names only a path.</summary>
    private static readonly Uri Origin = new("https://localhost");

    private readonly ProductionFrontendFactory _factory;

    /// <summary>
    /// A form post reaches the login check before the antiforgery check, so it needs no token.
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
        Uri location = redirect.Headers.Location.ShouldNotBeNull();
        location.AbsolutePath.ShouldBe(LoginPath);
        QueryHelpers.ParseQuery(location.Query)["ReturnUrl"].ToString().ShouldBe(pathAndQuery);
        page.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        string markup = await page.Content.ReadAsStringAsync();
        markup.ShouldContain(LoginUnavailableTitle);
        markup.ShouldNotContain(ErrorTitle);
    }

    /// <summary>
    /// #1058: the request that ends a dead session goes to a page that needs a login, which needs the
    /// sign-in server. The answer must still delete the cookie.
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
        await browser.LoadAsync("/dashboard");
        translationSystem.AcceptsTheToken = false;
        await browser.LoadAsync("/dashboard");

        // Act
        using HttpResponseMessage redirect = await browser.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        using HttpResponseMessage page = await FollowAsync(browser, redirect);

        // Assert
        CookieJarBrowser.SetCookiesOf(redirect)
            .ShouldContain(setCookie => setCookie.StartsWith($"{SignedInSession.CookieName}=;", StringComparison.Ordinal));
        page.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        string markup = await page.Content.ReadAsStringAsync();
        markup.ShouldContain(LoginUnavailableTitle);
        markup.ShouldNotContain(SignedInSession.UserName);
        markup.ShouldNotContain(SignOutButton);
    }

    /// <summary>
    /// Even a rule that makes every page need a login must not send the login route back to itself.
    /// </summary>
    [Fact]
    public async Task Get_APageThatNeedsALoginWhileTheSignInServerIsDownAndEveryPageNeedsALogin_EndsOnTheLoginUnavailablePage()
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(
            new StandInSignInServer(),
            new DashboardTranslationSystem(),
            services => services.AddAuthorizationBuilder()
                .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()));
        using CookieJarBrowser browser = new(host);

        // Act
        using HttpResponseMessage redirect = await browser.GetAsync("/dashboard");
        using HttpResponseMessage page = await FollowAsync(browser, redirect);

        // Assert
        page.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        string markup = await page.Content.ReadAsStringAsync();
        markup.ShouldContain(LoginUnavailableTitle);
    }

    /// <summary>
    /// The page asks the visitor to try again; a reload still comes back to the page the visitor asked for.
    /// </summary>
    [Fact]
    public async Task Get_TheLoginUnavailablePageAgainOnceTheSignInServerAnswers_StartsTheSignInForTheSamePage()
    {
        // Arrange
        StandInSignInServer signInServer = new();
        using WebApplicationFactory<Program> host = CreateHost(signInServer, new DashboardTranslationSystem());
        using CookieJarBrowser browser = new(host);
        using HttpResponseMessage redirect = await browser.GetAsync("/game-versions?page=2");
        Uri loginUnavailable = redirect.Headers.Location
            ?? throw new InvalidOperationException($"The page answered {redirect.StatusCode} with no redirect.");
        await browser.LoadAsync(loginUnavailable.OriginalString);
        signInServer.IsUp = true;

        // Act
        using HttpResponseMessage reload = await browser.GetAsync(loginUnavailable.OriginalString);

        // Assert
        reload.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Uri authorize = reload.Headers.Location.ShouldNotBeNull();
        authorize.OriginalString.ShouldStartWith($"{Authority}/connect/authorize?");
        AuthenticationProperties? state = OpenIdConnectOptionsOf(host)
            .StateDataFormat.Unprotect(QueryHelpers.ParseQuery(authorize.Query)["state"].ToString());
        state.ShouldNotBeNull().RedirectUri.ShouldBe("/game-versions?page=2");
    }

    [Fact]
    public async Task Get_APageThatNeedsALoginWhileTheSignInServerAnswers_SendsTheVisitorOnToTheSignInServer()
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(
            new StandInSignInServer { IsUp = true },
            new DashboardTranslationSystem());
        using CookieJarBrowser browser = new(host);

        // Act
        using HttpResponseMessage redirect = await browser.GetAsync("/dashboard");
        using HttpResponseMessage login = await FollowAsync(browser, redirect);

        // Assert
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        login.Headers.Location.ShouldNotBeNull().OriginalString.ShouldStartWith($"{Authority}/connect/authorize?");
    }

    /// <summary>
    /// The sign-in starts on a host whose sign-in server answers; the return goes to a second host from the
    /// same factory, which shares its keys like the website after a restart.
    /// </summary>
    [Fact]
    public async Task Get_TheSignInReturnAfterARestartWhileTheSignInServerIsDown_EndsOnTheLoginUnavailablePageForTheSamePage()
    {
        // Arrange
        using WebApplicationFactory<Program> beforeTheRestart = CreateHost(
            new StandInSignInServer { IsUp = true },
            new DashboardTranslationSystem());
        using CookieJarBrowser signInStart = new(beforeTheRestart);
        using HttpResponseMessage challenge = await signInStart.GetAsync($"{LoginPath}?returnUrl=/dashboard");
        string state = QueryHelpers.ParseQuery(challenge.Headers.Location.ShouldNotBeNull().Query)["state"].ToString();
        using WebApplicationFactory<Program> afterTheRestart = CreateHost(
            new StandInSignInServer(),
            new DashboardTranslationSystem());
        using CookieJarBrowser browser = new(afterTheRestart);
        foreach (string[] cookie in CookieJarBrowser.SetCookiesOf(challenge)
                     .Select(setCookie => setCookie.Split(';', 2)[0].Split('=', 2)))
        {
            browser.SetCookie(cookie[0], cookie[1]);
        }

        // Act
        using HttpResponseMessage redirect = await browser.GetAsync(
            $"{OpenIdConnectOptionsOf(afterTheRestart).CallbackPath}?code=the-code&state={Uri.EscapeDataString(state)}");
        using HttpResponseMessage page = await FollowAsync(browser, redirect);

        // Assert
        redirect.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Uri location = new(Origin, redirect.Headers.Location.ShouldNotBeNull());
        location.AbsolutePath.ShouldBe(LoginPath);
        QueryHelpers.ParseQuery(location.Query)["returnUrl"].ToString().ShouldBe("/dashboard");
        page.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        string markup = await page.Content.ReadAsStringAsync();
        markup.ShouldContain(LoginUnavailableTitle);
        markup.ShouldNotContain(ErrorTitle);
    }

    /// <summary>
    /// A return with no usable state has no page to come back to, so it uses the start page.
    /// </summary>
    [Theory]
    [InlineData("code=the-code")]
    [InlineData("code=the-code&state=not-a-state")]
    public async Task Get_TheSignInReturnWithNoUsableStateWhileTheSignInServerIsDown_EndsOnTheLoginUnavailablePage(
        string query)
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(new StandInSignInServer(), new DashboardTranslationSystem());
        using CookieJarBrowser browser = new(host);

        // Act
        using HttpResponseMessage redirect = await browser.GetAsync($"{OpenIdConnectOptionsOf(host).CallbackPath}?{query}");
        using HttpResponseMessage page = await FollowAsync(browser, redirect);

        // Assert
        redirect.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Uri location = new(Origin, redirect.Headers.Location.ShouldNotBeNull());
        location.AbsolutePath.ShouldBe(LoginPath);
        QueryHelpers.ParseQuery(location.Query)["returnUrl"].ToString().ShouldBe("/");
        page.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        string markup = await page.Content.ReadAsStringAsync();
        markup.ShouldContain(LoginUnavailableTitle);
    }

    /// <summary>
    /// A signed-in user's session needs no call to the sign-in server (#1058).
    /// </summary>
    [Fact]
    public async Task Get_APageThatNeedsALoginForASignedInUserWhileTheSignInServerIsDown_ShowsThePage()
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(new StandInSignInServer(), new DashboardTranslationSystem());
        using CookieJarBrowser browser = SignedInSession.OpenBrowser(host);

        // Act
        using HttpResponseMessage page = await browser.GetAsync("/dashboard");

        // Assert
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        string markup = await page.Content.ReadAsStringAsync();
        markup.ShouldContain(SignedInSession.UserName);
        markup.ShouldNotContain(LoginUnavailableTitle);
    }

    /// <summary>
    /// The end-session redirect comes from the configured address, and the revoke gives up quietly.
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
        CookieJarBrowser.SetCookiesOf(logout)
            .ShouldContain(setCookie => setCookie.StartsWith($"{SignedInSession.CookieName}=;", StringComparison.Ordinal));
    }

    public SignInServerDownTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// <see cref="OpenIdConnectOptions.Configuration"/> stays unset, so the OIDC handler builds its real
    /// configuration manager, which has fetched nothing yet.
    /// </summary>
    private WebApplicationFactory<Program> CreateHost(
        StandInSignInServer signInServer,
        DashboardTranslationSystem translationSystem,
        Action<IServiceCollection>? configureServices = null) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options => options.BackchannelHttpHandler = signInServer);
            services.AddHttpClient(DashboardTranslationSystem.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => translationSystem);
            configureServices?.Invoke(services);
        }));

    private static OpenIdConnectOptions OpenIdConnectOptionsOf(WebApplicationFactory<Program> host) =>
        host.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);

    /// <summary>Follows a redirect the way a browser does: with a GET, whatever the method before it.</summary>
    private static Task<HttpResponseMessage> FollowAsync(CookieJarBrowser browser, HttpResponseMessage redirect)
    {
        Uri location = redirect.Headers.Location
            ?? throw new InvalidOperationException($"The route answered {redirect.StatusCode} with no redirect.");
        return browser.GetAsync(location.OriginalString);
    }

    /// <summary>
    /// When up, it answers its discovery document and nothing else.
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
