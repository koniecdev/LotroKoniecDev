using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Auth;

/// <summary>
/// #1025: the first sign-in stores the refresh time of the first token. The unit tests raise the OIDC
/// event by hand, so this test runs the real handler through the login redirect and the callback against
/// a stubbed sign-in server. It proves the event runs after the handler stored the tokens, and that its
/// value reaches the cookie the browser gets.
/// </summary>
public sealed class FirstSignInRefreshTimeTests : IClassFixture<StagingFrontendFactory>, IDisposable
{
    private const string AuthCookieName = ".lotrokoniecdev.auth";
    private const string ClientId = "lotrokoniecdev-web";
    private const string Issuer = "https://auth.staging.invalid";
    private const string Subject = "11111111-1111-1111-1111-111111111111";

    private static readonly Uri AuthorizationEndpoint = new($"{Issuer}/connect/authorize");
    private static readonly Uri TokenEndpoint = new($"{Issuer}/connect/token");
    private static readonly Uri UserInfoEndpoint = new($"{Issuer}/connect/userinfo");

    private readonly StagingFrontendFactory _factory;
    private readonly RSA _signingRsa = RSA.Create(2048);

    public FirstSignInRefreshTimeTests(StagingFrontendFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData(60, 30)]
    [InlineData(300, 240)]
    public async Task Callback_WithTheFirstToken_StoresItsRefreshTimeInTheSessionCookie(
        int expiresIn,
        int secondsUntilRefresh)
    {
        // Arrange: the clock stands still, so the stored times are exact. It starts at the real time,
        // because the handler also validates the ID token and the nonce against the real clock.
        FakeTimeProvider time = new(DateTimeOffset.UtcNow);
        RsaSecurityKey signingKey = new(_signingRsa) { KeyId = "test-signing-key" };
        StubSignInServer signInServer = new(expiresIn);
        using WebApplicationFactory<Program> host = CreateHost(signingKey, signInServer, time);
        using HttpClient browser = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });

        using HttpResponseMessage challenge = await browser.GetAsync(new Uri("/auth/login", UriKind.Relative));
        challenge.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Dictionary<string, string> authorizeQuery = ParseQuery(challenge.Headers.Location.ShouldNotBeNull());
        signInServer.IdToken = MintIdToken(signingKey, authorizeQuery["nonce"]);

        using HttpRequestMessage callback = new(
            HttpMethod.Get,
            $"/callback?code=test-code&state={Uri.EscapeDataString(authorizeQuery["state"])}");
        callback.Headers.Add("Cookie", string.Join("; ", CookiePairs(challenge)));

        // Act
        using HttpResponseMessage signedIn = await browser.SendAsync(callback);

        // Assert: a failed callback also answers 302, but to the error page
        signedIn.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        signedIn.Headers.Location.ShouldNotBeNull().OriginalString.ShouldBe("/");
        AuthenticationProperties properties = ReadSessionCookie(host, signedIn);
        ParseMoment(properties.GetTokenValue("expires_at")).ShouldBe(time.GetUtcNow().AddSeconds(expiresIn));
        ParseMoment(properties.GetTokenValue("refresh_at")).ShouldBe(time.GetUtcNow().AddSeconds(secondsUntilRefresh));
    }

    public void Dispose()
    {
        _signingRsa.Dispose();
    }

    private WebApplicationFactory<Program> CreateHost(
        SecurityKey signingKey,
        StubSignInServer signInServer,
        TimeProvider time)
    {
        OpenIdConnectConfiguration configuration = new()
        {
            Issuer = Issuer,
            AuthorizationEndpoint = AuthorizationEndpoint.AbsoluteUri,
            TokenEndpoint = TokenEndpoint.AbsoluteUri,
            UserInfoEndpoint = UserInfoEndpoint.AbsoluteUri
        };
        configuration.SigningKeys.Add(signingKey);

        // Registered after the app's own setup, so these win: the handler reads the stub's endpoints and keys
        // and the frozen clock, and makes no network call.
        return _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(time);
            services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.Configuration = configuration;
                options.BackchannelHttpHandler = signInServer;
            });
        }));
    }

    private static AuthenticationProperties ReadSessionCookie(WebApplicationFactory<Program> host, HttpResponseMessage response)
    {
        DefaultHttpContext cookieReader = new();
        cookieReader.Request.Headers.Cookie = string.Join(
            "; ",
            CookiePairs(response).Where(pair => pair.StartsWith(AuthCookieName, StringComparison.Ordinal)));
        string protectedTicket = new ChunkingCookieManager()
            .GetRequestCookie(cookieReader, AuthCookieName)
            .ShouldNotBeNull();

        CookieAuthenticationOptions cookieOptions = host.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        return cookieOptions.TicketDataFormat.Unprotect(protectedTicket).ShouldNotBeNull().Properties;
    }

    private static IEnumerable<string> CookiePairs(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies)
            ? setCookies.Select(setCookie => setCookie.Split(';', 2)[0])
            : [];

    private static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

    private static DateTimeOffset ParseMoment(string? value) =>
        DateTimeOffset.Parse(value.ShouldNotBeNull(), CultureInfo.InvariantCulture);

    private static string MintIdToken(SecurityKey signingKey, string nonce)
    {
        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = Issuer,
            Audience = ClientId,
            Subject = new ClaimsIdentity([new Claim("sub", Subject), new Claim("nonce", nonce)]),
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>
    /// The token and userinfo endpoints of the sign-in server, as the OIDC handler's back-channel sees them.
    /// </summary>
    private sealed class StubSignInServer : HttpMessageHandler
    {
        private readonly int _expiresIn;

        public StubSignInServer(int expiresIn)
        {
            _expiresIn = expiresIn;
        }

        public string? IdToken { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = request.RequestUri switch
            {
                { } uri when uri == TokenEndpoint => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new Dictionary<string, object?>
                    {
                        ["access_token"] = "first-access-token",
                        ["token_type"] = "Bearer",
                        ["expires_in"] = _expiresIn,
                        ["refresh_token"] = "first-refresh-token",
                        ["id_token"] = IdToken
                    })
                },
                { } uri when uri == UserInfoEndpoint => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new Dictionary<string, object?> { ["sub"] = Subject })
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };

            return Task.FromResult(response);
        }
    }
}
