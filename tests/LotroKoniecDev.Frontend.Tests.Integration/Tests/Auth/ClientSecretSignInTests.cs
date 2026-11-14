using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Auth;

/// <summary>
/// ADR-0074: the real OIDC handler against a stand-in sign-in server. The website proves itself with its
/// secret on the code exchange and keeps PKCE, and the secret never goes to the browser.
/// </summary>
public sealed class ClientSecretSignInTests : IClassFixture<ProductionFrontendFactory>, IDisposable
{
    private const string ClientId = "lotrokoniecdev-frontend";
    private const string Issuer = "https://auth.staging.invalid";
    private const string Subject = "11111111-1111-1111-1111-111111111111";

    private static readonly Uri AuthorizationEndpoint = new($"{Issuer}/connect/authorize");
    private static readonly Uri TokenEndpoint = new($"{Issuer}/connect/token");
    private static readonly Uri UserInfoEndpoint = new($"{Issuer}/connect/userinfo");

    // Built rather than written out, so no secret scanner mistakes test data for a key.
    private static readonly string ClientSecret = new('c', 44);

    private readonly ProductionFrontendFactory _factory;
    private readonly RSA _signingRsa = RSA.Create(2048);

    [Fact]
    public async Task SignIn_ExchangesTheCodeWithTheClientSecretAndTheVerifierOfThePkceChallenge()
    {
        // Arrange
        RsaSecurityKey signingKey = new(_signingRsa) { KeyId = "test-signing-key" };
        StubSignInServer signInServer = new();
        using WebApplicationFactory<Program> host = CreateHost(signingKey, signInServer);
        using HttpClient browser = CreateBrowser(host);
        using HttpResponseMessage challenge = await browser.GetAsync(new Uri("/auth/login", UriKind.Relative));
        Dictionary<string, StringValues> authorizeQuery =
            QueryHelpers.ParseQuery(challenge.Headers.Location.ShouldNotBeNull().Query);
        signInServer.IdToken = MintIdToken(signingKey, authorizeQuery["nonce"].ToString());

        // Act
        using HttpResponseMessage signedIn = await SendCallbackAsync(browser, challenge, authorizeQuery["state"].ToString());

        // Assert: a failed callback also answers 302, but to the error page
        signedIn.Headers.Location.ShouldNotBeNull().OriginalString.ShouldBe("/");
        Dictionary<string, StringValues> tokenRequest =
            QueryHelpers.ParseQuery(signInServer.TokenRequestBody.ShouldNotBeNull());
        tokenRequest["client_id"].ToString().ShouldBe(ClientId);
        tokenRequest["client_secret"].ToString().ShouldBe(ClientSecret);
        string codeVerifier = tokenRequest["code_verifier"].ToString();
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)))
            .ShouldBe(authorizeQuery["code_challenge"].ToString());
    }

    [Fact]
    public async Task SignIn_SendsTheBrowserToTheSignInServerWithoutTheClientSecret()
    {
        // Arrange
        RsaSecurityKey signingKey = new(_signingRsa) { KeyId = "test-signing-key" };
        using WebApplicationFactory<Program> host = CreateHost(signingKey, new StubSignInServer());
        using HttpClient browser = CreateBrowser(host);

        // Act
        using HttpResponseMessage challenge = await browser.GetAsync(new Uri("/auth/login", UriKind.Relative));

        // Assert
        challenge.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Uri authorizeAddress = challenge.Headers.Location.ShouldNotBeNull();
        authorizeAddress.GetLeftPart(UriPartial.Path).ShouldBe(AuthorizationEndpoint.AbsoluteUri);
        QueryHelpers.ParseQuery(authorizeAddress.Query).ShouldNotContainKey("client_secret");
        challenge.ToString().ShouldNotContain(ClientSecret);
        (await challenge.Content.ReadAsStringAsync()).ShouldNotContain(ClientSecret);
    }

    public void Dispose()
    {
        _signingRsa.Dispose();
    }

    public ClientSecretSignInTests(ProductionFrontendFactory factory)
    {
        _factory = factory;
    }

    private static HttpClient CreateBrowser(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });

    private static async Task<HttpResponseMessage> SendCallbackAsync(
        HttpClient browser,
        HttpResponseMessage challenge,
        string state)
    {
        using HttpRequestMessage callback = new(
            HttpMethod.Get,
            $"/callback?code=test-code&state={Uri.EscapeDataString(state)}");
        callback.Headers.Add(
            "Cookie",
            string.Join("; ", challenge.Headers.GetValues("Set-Cookie").Select(setCookie => setCookie.Split(';', 2)[0])));

        return await browser.SendAsync(callback);
    }

    private WebApplicationFactory<Program> CreateHost(SecurityKey signingKey, StubSignInServer signInServer)
    {
        OpenIdConnectConfiguration configuration = new()
        {
            Issuer = Issuer,
            AuthorizationEndpoint = AuthorizationEndpoint.AbsoluteUri,
            TokenEndpoint = TokenEndpoint.AbsoluteUri,
            UserInfoEndpoint = UserInfoEndpoint.AbsoluteUri
        };
        configuration.SigningKeys.Add(signingKey);

        // Registered after the app's own setup, so these win. No network call is made.
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "AuthSystem:ClientSecret", ClientSecret }
                }));

            builder.ConfigureTestServices(services =>
                services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                {
                    options.Configuration = configuration;
                    options.BackchannelHttpHandler = signInServer;
                }));
        });
    }

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

    private sealed class StubSignInServer : HttpMessageHandler
    {
        public string? IdToken { get; set; }

        public string? TokenRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri == TokenEndpoint)
            {
                TokenRequestBody = await request.Content.ShouldNotBeNull().ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new Dictionary<string, object?>
                    {
                        ["access_token"] = "first-access-token",
                        ["token_type"] = "Bearer",
                        ["expires_in"] = 300,
                        ["refresh_token"] = "first-refresh-token",
                        ["id_token"] = IdToken
                    })
                };
            }

            return request.RequestUri == UserInfoEndpoint
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new Dictionary<string, object?> { ["sub"] = Subject })
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
