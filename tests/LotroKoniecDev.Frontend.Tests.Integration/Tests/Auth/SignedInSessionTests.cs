using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Discovery;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Auth;

/// <summary>
/// #1058: the website does not check the access token itself, because the translation API checks every token
/// it receives (ADR-0067 decision 2). Each host here has just started, so it holds no copy of the sign-in
/// server's metadata, and the sign-in server cannot be reached. The real cookie check runs on every request.
/// </summary>
public sealed class SignedInSessionTests : IClassFixture<StagingFrontendFactory>
{
    private const string AuthCookieName = ".lotrokoniecdev.auth";
    private const string TmsOrigin = "https://tms.staging.invalid";
    private const string Subject = "11111111-1111-1111-1111-111111111111";
    private const string UserName = "Anna";
    private const string SignOutButton = "Wyloguj";

    /// <summary>The name ASP.NET Core gives the typed client, which is internal to the Frontend.</summary>
    private const string TranslationSystemClientName = "ITranslationSystemClient";

    private readonly StagingFrontendFactory _factory;

    [Fact]
    public async Task Get_WhileTheSignInServerCannotBeReachedRightAfterARestart_ShowsThePageToTheSignedInUser()
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost(new StubTranslationSystem());
        using HttpClient browser = CreateBrowser(host);

        // Act
        using HttpResponseMessage page = await browser.SendAsync(PageRequest("/terms", SessionCookie(host)));

        // Assert
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        SetCookies(page).ShouldNotContain(setCookie => setCookie.StartsWith(AuthCookieName, StringComparison.Ordinal));
        string markup = await page.Content.ReadAsStringAsync();
        markup.ShouldContain(UserName);
        markup.ShouldContain(SignOutButton);
    }

    [Fact]
    public async Task Get_TheNextPageAfterTheTranslationApiRefusedTheAccessToken_EndsTheSession()
    {
        // Arrange: the dashboard loads while the API accepts the token, so the service document is cached for
        // this account. Then the signing key changes. The cached document still names the statistics, the API
        // refuses the token there with a 401, and the refused call marks the session dead.
        StubTranslationSystem translationSystem = new();
        using WebApplicationFactory<Program> host = CreateHost(translationSystem);
        using HttpClient browser = CreateBrowser(host);
        string sessionCookie = SessionCookie(host);
        await LoadAsync(browser, PageRequest("/dashboard", sessionCookie));
        translationSystem.AcceptsTheToken = false;
        await LoadAsync(browser, PageRequest("/dashboard", sessionCookie));

        // Act
        using HttpResponseMessage nextPage = await browser.SendAsync(PageRequest("/terms", sessionCookie));

        // Assert
        nextPage.StatusCode.ShouldBe(HttpStatusCode.OK);
        SetCookies(nextPage)
            .ShouldContain(setCookie => setCookie.StartsWith($"{AuthCookieName}=;", StringComparison.Ordinal));
        string markup = await nextPage.Content.ReadAsStringAsync();
        markup.ShouldNotContain(UserName);
        markup.ShouldNotContain(SignOutButton);
        translationSystem.Unauthorized.ShouldBe(1);
    }

    [Fact]
    public async Task Get_TheNextPageAfterTheServiceDocumentCameBackWithoutTheSignedInLinks_EndsTheSession()
    {
        // Arrange: nothing is cached yet, and the API refuses the token. Its service document is open to
        // everyone, so it answers with the anonymous links, which marks the session dead.
        using WebApplicationFactory<Program> host = CreateHost(new StubTranslationSystem { AcceptsTheToken = false });
        using HttpClient browser = CreateBrowser(host);
        string sessionCookie = SessionCookie(host);
        await LoadAsync(browser, PageRequest("/dashboard", sessionCookie));

        // Act
        using HttpResponseMessage nextPage = await browser.SendAsync(PageRequest("/terms", sessionCookie));

        // Assert
        nextPage.StatusCode.ShouldBe(HttpStatusCode.OK);
        SetCookies(nextPage)
            .ShouldContain(setCookie => setCookie.StartsWith($"{AuthCookieName}=;", StringComparison.Ordinal));
        string markup = await nextPage.Content.ReadAsStringAsync();
        markup.ShouldNotContain(UserName);
        markup.ShouldNotContain(SignOutButton);
    }

    public SignedInSessionTests(StagingFrontendFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// <see cref="OpenIdConnectOptions.Configuration"/> stays unset, so the OIDC handler builds its real
    /// configuration manager, which has fetched nothing yet. Every call to the sign-in server fails the way a
    /// call to a server that is down does.
    /// </summary>
    private WebApplicationFactory<Program> CreateHost(StubTranslationSystem translationSystem) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Configure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options => options.BackchannelHttpHandler = new UnreachableServer());
            services.AddHttpClient(TranslationSystemClientName)
                .ConfigurePrimaryHttpMessageHandler(() => translationSystem);
        }));

    private static HttpClient CreateBrowser(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });

    /// <summary>
    /// The session cookie a sign-in leaves, protected with the host's own keys. Its access token is far from
    /// expiry, so the cookie check has no renewal to make. The token is opaque to the website, so any text
    /// serves.
    /// </summary>
    private static string SessionCookie(WebApplicationFactory<Program> host)
    {
        ClaimsPrincipal user = new(new ClaimsIdentity(
            [new Claim("sub", Subject), new Claim("name", UserName)],
            authenticationType: OpenIdConnectDefaults.AuthenticationScheme,
            nameType: "name",
            roleType: "role"));

        AuthenticationProperties properties = new();
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = "opaque-access-token" },
            new AuthenticationToken { Name = "refresh_token", Value = "opaque-refresh-token" },
            new AuthenticationToken
            {
                Name = "expires_at",
                Value = DateTimeOffset.UtcNow.AddMinutes(5).ToString("o", CultureInfo.InvariantCulture)
            }
        ]);

        CookieAuthenticationOptions cookieOptions = host.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        string protectedTicket = cookieOptions.TicketDataFormat.Protect(
            new AuthenticationTicket(user, properties, CookieAuthenticationDefaults.AuthenticationScheme));

        return $"{AuthCookieName}={protectedTicket}";
    }

    private static HttpRequestMessage PageRequest(string path, string sessionCookie)
    {
        HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Add("Cookie", sessionCookie);
        return request;
    }

    /// <summary>
    /// Reads the whole response: a streaming page makes its API calls while it streams, after the headers
    /// have arrived.
    /// </summary>
    private static async Task LoadAsync(HttpClient browser, HttpRequestMessage request)
    {
        using (request)
        {
            using HttpResponseMessage response = await browser.SendAsync(request);
            await response.Content.ReadAsStringAsync();
        }
    }

    private static string[] SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies) ? [.. setCookies] : [];

    /// <summary>A sign-in server that is down: every call fails before an answer arrives.</summary>
    private sealed class UnreachableServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused."));
    }

    /// <summary>
    /// The translation API as the website's typed client sees it. The service document is open to everyone,
    /// so a refused token gets the anonymous links there, never a 401. The statistics need a sign-in, so a
    /// refused token gets a 401 there.
    /// </summary>
    private sealed class StubTranslationSystem : HttpMessageHandler
    {
        private const string StatsHref = $"{TmsOrigin}/advertised/translation-stats";

        private int _unauthorized;

        public bool AcceptsTheToken { get; set; } = true;

        /// <summary>How many 401 answers the API sent, so a test can tell which path marked the session.</summary>
        public int Unauthorized => _unauthorized;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("A call with no address.");
            HttpResponseMessage response = uri.GetLeftPart(UriPartial.Path) switch
            {
                $"{TmsOrigin}/" => Json(new DiscoveryResponse("tms")
                {
                    Links = AcceptsTheToken
                        ?
                        [
                            new LinkDto(StatsHref, Rels.TranslationStats, "GET"),
                            new LinkDto($"{TmsOrigin}/advertised/contributions", Rels.ContributionDataExport, "GET")
                        ]
                        : []
                }),
                StatsHref when AcceptsTheToken => Json(new TranslationStatsResponse(4, 3, 2, 1)),
                StatsHref => Refuse(),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };

            return Task.FromResult(response);
        }

        private HttpResponseMessage Refuse()
        {
            Interlocked.Increment(ref _unauthorized);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        private static HttpResponseMessage Json<T>(T body) =>
            new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(body, mediaType: new MediaTypeHeaderValue(MediaTypes.HateoasJson))
            };
    }
}
