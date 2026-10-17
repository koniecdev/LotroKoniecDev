using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// The limiter is off in Development and Testing, and every other test that needs a real 429 turns it on
/// with the <c>RateLimiting:ForceEnable</c> test switch. These tests boot the host the way a box runs it:
/// in Staging or Production, with the settings such a host needs to start, and without the switch
/// (#827). A gate that needed the switch would leave both deployed environments with no limit at all.
/// The TMS API has the same test (#823). Every test creates its own host, so its bucket is its own.
/// </summary>
public sealed class DeployedEnvironmentRateLimitingTests : EndpointsTestBase
{
    /// <summary>Mirrors the change-email-limit policy: 3 permits per hour per client.</summary>
    private const int ChangeEmailBucket = 3;

    private const string ForwardedForHeader = "X-Forwarded-For";
    private const string ForwardedProtoHeader = "X-Forwarded-Proto";
    private const string VisitorAddress = "203.0.113.70";

    private static readonly Uri ChangeEmailEndpoint = new("auth/account/change-email", UriKind.Relative);

    // Built rather than written out, so no secret scanner mistakes test data for a key.
    private static readonly string FrontendKey = new('k', 40);
    private static readonly string HealthCheckKey = new('h', 40);

    private readonly string _keyRingPath = Path.Combine(
        Path.GetTempPath(),
        $"lotro-auth-deployed-keyring-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task DeployedEnvironment_ShouldEnforceTheLimitWithoutTheTestSwitch(string environmentName)
    {
        // Arrange: the requests carry no token, like in ChangeEmailRateLimitingTests. The limiter runs
        // before authentication, so a 401 spends a permit, and only the limiter can answer 429 here.
        using WebApplicationFactory<Program> deployedHost = CreateDeployedHost(environmentName);
        using HttpClient client = deployedHost.CreateClient();

        for (int i = 0; i < ChangeEmailBucket; i++)
        {
            using HttpResponseMessage response = await PostChangeEmailAsync(client);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Act
        using HttpResponseMessage overTheLimit = await PostChangeEmailAsync(client);

        // Assert
        overTheLimit.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();

        if (Directory.Exists(_keyRingPath))
        {
            Directory.Delete(_keyRingPath, recursive: true);
        }
    }

    public DeployedEnvironmentRateLimitingTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    private static async Task<HttpResponseMessage> PostChangeEmailAsync(HttpClient client)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, ChangeEmailEndpoint);
        request.Headers.Add(ForwardedForHeader, VisitorAddress);

        // This is what Caddy sends. It keeps the HTTPS redirect of a deployed host out of the way.
        request.Headers.Add(ForwardedProtoHeader, "https");
        request.Content = JsonContent.Create(new ChangeEmailRequest("someone-else@lotro-translator.pl", "TestPass1!"));

        return await client.SendAsync(request);
    }

    private static string NewSigningKeyXml()
    {
        using RSA rsa = RSA.Create(2048);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ToXmlString(includePrivateParameters: true)));
    }

    /// <summary>
    /// The base factory already sets the encryption key, the API client secret and the web client
    /// addresses. A deployed host also needs a signing key, an issuer that is not localhost, the CORS
    /// origins, the frontend caller key, the health check key and a keyring folder. The test switch is
    /// set to false here, so no other configuration source can turn the limiter on for these tests.
    /// </summary>
    private WebApplicationFactory<Program> CreateDeployedHost(string environmentName)
    {
        return Factory.WithWebHostBuilder(builder =>
        {
            // UseSetting, not ConfigureAppConfiguration: Program.cs reads the keyring path before the
            // host is built, and only a setting passed this way is there in time.
            builder.UseEnvironment(environmentName);
            builder.UseSetting("DataProtection:KeyRingPath", _keyRingPath);
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "OpenIddict:Issuer", "https://auth.lotro.test" },
                    { "OpenIddict:SigningKey:RsaPrivateKeyXml", NewSigningKeyXml() },
                    { "Cors:AllowedOrigins:0", "https://app.lotro.test" },
                    { "FrontendCaller:Key", FrontendKey },
                    { "HealthCheck:Key", HealthCheckKey },
                    { "RateLimiting:ForceEnable", "false" }
                });
            });
        });
    }
}
