using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Auth;

/// <summary>
/// The sign-up button runs the real OIDC handler with <c>prompt=create</c>, so the sign-in server opens its
/// sign-up form (#1575). The login keeps asking for no prompt.
/// </summary>
public sealed class SignUpChallengeTests : IClassFixture<ProductionFrontendFactory>
{
    private const string Issuer = "https://auth.staging.invalid";

    private static readonly Uri AuthorizationEndpoint = new($"{Issuer}/connect/authorize");

    private readonly ProductionFrontendFactory _factory;

    [Fact]
    public async Task Register_SendsTheBrowserToTheSignInServerWithPromptCreate()
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost();
        using HttpClient browser = CreateBrowser(host);

        // Act
        using HttpResponseMessage challenge = await browser.GetAsync(new Uri("/auth/register", UriKind.Relative));

        // Assert
        challenge.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Uri authorizeAddress = challenge.Headers.Location.ShouldNotBeNull();
        authorizeAddress.GetLeftPart(UriPartial.Path).ShouldBe(AuthorizationEndpoint.AbsoluteUri);
        Dictionary<string, StringValues> query = QueryHelpers.ParseQuery(authorizeAddress.Query);
        query["prompt"].ToString().ShouldBe("create");
    }

    [Fact]
    public async Task Login_SendsTheBrowserToTheSignInServerWithNoPrompt()
    {
        // Arrange
        using WebApplicationFactory<Program> host = CreateHost();
        using HttpClient browser = CreateBrowser(host);

        // Act
        using HttpResponseMessage challenge = await browser.GetAsync(new Uri("/auth/login", UriKind.Relative));

        // Assert
        challenge.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Uri authorizeAddress = challenge.Headers.Location.ShouldNotBeNull();
        authorizeAddress.GetLeftPart(UriPartial.Path).ShouldBe(AuthorizationEndpoint.AbsoluteUri);
        QueryHelpers.ParseQuery(authorizeAddress.Query).ShouldNotContainKey("prompt");
    }

    public SignUpChallengeTests(ProductionFrontendFactory factory)
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

    /// <summary>A fixed configuration, so the challenge builds its redirect with no network call.</summary>
    private WebApplicationFactory<Program> CreateHost()
    {
        OpenIdConnectConfiguration configuration = new()
        {
            Issuer = Issuer,
            AuthorizationEndpoint = AuthorizationEndpoint.AbsoluteUri
        };

        return _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.Configure<OpenIdConnectOptions>(
                    OpenIdConnectDefaults.AuthenticationScheme,
                    options => options.Configuration = configuration)));
    }
}
