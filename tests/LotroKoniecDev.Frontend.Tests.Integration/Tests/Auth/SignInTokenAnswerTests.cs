using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Auth;

/// <summary>
/// #1026: the first sign-in holds the token answer to the rules the background renewal uses (#974). Each
/// test runs the real sign-in in memory: the login redirect, then the browser's return to the website, with
/// a stand-in for the sign-in server's token endpoint. The stand-in advertises no userinfo endpoint, so the
/// token answer is the only thing that decides the outcome.
/// </summary>
public sealed class SignInTokenAnswerTests : IClassFixture<StagingFrontendFactory>, IDisposable
{
    private const string Issuer = "https://auth.staging.invalid";
    private const string ErrorPath = "/Error";

    private readonly StagingFrontendFactory _factory;
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _signingKey;

    public SignInTokenAnswerTests(StagingFrontendFactory factory)
    {
        _factory = factory;
        _signingKey = new RsaSecurityKey(_rsa) { KeyId = Guid.NewGuid().ToString("N") };
    }

    /// <summary>
    /// The framework's own protocol check already refuses a missing or empty access token, so only the blank
    /// rows depend on the new rule. All four stay, because together they pin the whole outcome.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public async Task SignIn_WhenTheAnswerHasNoUsableAccessToken_ShouldEndOnTheErrorPageWithNoSession(
        string? accessToken)
    {
        // Act
        SignInResult result = await SignInAsync(accessToken, expiresInJson: "300");

        // Assert
        result.Location.ShouldBe(ErrorPath);
        result.SessionCookieIssued.ShouldBeFalse();
    }

    /// <summary>
    /// The OIDC handler stores no expiry for a lifetime it cannot read as a whole number, so a fraction, a
    /// word or a number past <see cref="int.MaxValue"/> counts as a missing lifetime.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-2147483648")]
    [InlineData("\"\"")]
    [InlineData("\"soon\"")]
    [InlineData("300.5")]
    [InlineData("2147483648")]
    public async Task SignIn_WhenTheAnswerHasNoPositiveLifetime_ShouldEndOnTheErrorPageWithNoSession(
        string? expiresInJson)
    {
        // Act
        SignInResult result = await SignInAsync("the-access-token", expiresInJson);

        // Assert
        result.Location.ShouldBe(ErrorPath);
        result.SessionCookieIssued.ShouldBeFalse();
    }

    /// <summary>
    /// The handler would store a blank refresh token, and the cookie check ends such a session on the next
    /// request.
    /// </summary>
    [Theory]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public async Task SignIn_WhenTheAnswerHasABlankRefreshToken_ShouldEndOnTheErrorPageWithNoSession(
        string refreshToken)
    {
        // Act
        SignInResult result = await SignInAsync("the-access-token", expiresInJson: "300", refreshToken);

        // Assert
        result.Location.ShouldBe(ErrorPath);
        result.SessionCookieIssued.ShouldBeFalse();
    }

    /// <summary>
    /// The rule refuses only what the renewal refuses. One second is the smallest lifetime it keeps, and a
    /// number sent as a string is read by the handler like any other. A missing or empty refresh token is
    /// allowed, because OAuth makes it optional.
    /// </summary>
    [Theory]
    [InlineData("1", "the-refresh-token")]
    [InlineData("300", "the-refresh-token")]
    [InlineData("2147483647", "the-refresh-token")]
    [InlineData("\"300\"", "the-refresh-token")]
    [InlineData("300", null)]
    [InlineData("300", "")]
    public async Task SignIn_WhenTheAnswerHasAnAccessTokenAndAPositiveLifetime_ShouldStartTheSession(
        string expiresInJson,
        string? refreshToken)
    {
        // Act
        SignInResult result = await SignInAsync("the-access-token", expiresInJson, refreshToken);

        // Assert
        result.Location.ShouldBe("/");
        result.SessionCookieIssued.ShouldBeTrue();
    }

    public void Dispose()
    {
        _rsa.Dispose();
    }

    private async Task<SignInResult> SignInAsync(
        string? accessToken,
        string? expiresInJson,
        string? refreshToken = "the-refresh-token")
    {
        FakeTokenEndpoint tokenEndpoint = new(this, accessToken, expiresInJson, refreshToken);
        using WebApplicationFactory<Program> host = CreateHost(tokenEndpoint);
        using HttpClient browser = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        OpenIdConnectOptions openIdConnectOptions = host.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
        tokenEndpoint.ClientId = openIdConnectOptions.ClientId;
        string sessionCookieName = host.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme)
            .Cookie.Name!;

        using HttpResponseMessage challenge = await browser.GetAsync(new Uri("/auth/login", UriKind.Relative));
        Uri authorizeUri = challenge.Headers.Location
            ?? throw new InvalidOperationException($"The login answered {challenge.StatusCode} with no redirect.");
        Dictionary<string, StringValues> authorizeQuery = QueryHelpers.ParseQuery(authorizeUri.Query);
        tokenEndpoint.Nonce = authorizeQuery["nonce"].ToString();
        string state = authorizeQuery["state"].ToString();

        using HttpResponseMessage callback = await browser.GetAsync(
            new Uri(
                $"{openIdConnectOptions.CallbackPath}?code=the-code&state={Uri.EscapeDataString(state)}",
                UriKind.Relative));

        bool sessionCookieIssued = callback.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies)
            && cookies.Any(cookie => cookie.StartsWith(sessionCookieName, StringComparison.Ordinal));
        return new SignInResult(callback.Headers.Location?.OriginalString, sessionCookieIssued);
    }

    private WebApplicationFactory<Program> CreateHost(FakeTokenEndpoint tokenEndpoint)
    {
        OpenIdConnectConfiguration configuration = new()
        {
            Issuer = Issuer,
            AuthorizationEndpoint = Issuer + "/connect/authorize",
            TokenEndpoint = FakeTokenEndpoint.Address,
            EndSessionEndpoint = Issuer + "/connect/logout"
        };
        configuration.SigningKeys.Add(_signingKey);

        return _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options =>
                {
                    options.Configuration = configuration;
                    options.BackchannelHttpHandler = tokenEndpoint;
                })));
    }

    private string MintIdToken(string audience, string nonce)
    {
        DateTime now = DateTime.UtcNow;
        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = Issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", "11111111-1111-1111-1111-111111111111")]),
            Claims = new Dictionary<string, object> { ["nonce"] = nonce },
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private sealed record SignInResult(string? Location, bool SessionCookieIssued);

    /// <summary>
    /// Answers the code exchange with the token answer under test. A null token or lifetime leaves that field
    /// out of the answer. The lifetime is raw JSON, so a test can send a number or a string.
    /// </summary>
    private sealed class FakeTokenEndpoint : HttpMessageHandler
    {
        public const string Address = Issuer + "/connect/token";

        private readonly SignInTokenAnswerTests _tests;
        private readonly string? _accessToken;
        private readonly string? _expiresInJson;
        private readonly string? _refreshToken;

        public FakeTokenEndpoint(
            SignInTokenAnswerTests tests,
            string? accessToken,
            string? expiresInJson,
            string? refreshToken)
        {
            _tests = tests;
            _accessToken = accessToken;
            _expiresInJson = expiresInJson;
            _refreshToken = refreshToken;
        }

        public string? ClientId { get; set; }

        public string? Nonce { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsoluteUri != Address)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            JsonObject answer = new()
            {
                ["token_type"] = "Bearer",
                ["id_token"] = _tests.MintIdToken(ClientId!, Nonce!)
            };
            if (_accessToken is not null)
            {
                answer["access_token"] = _accessToken;
            }

            if (_refreshToken is not null)
            {
                answer["refresh_token"] = _refreshToken;
            }

            if (_expiresInJson is not null)
            {
                answer["expires_in"] = JsonNode.Parse(_expiresInJson);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json")
            });
        }
    }
}
