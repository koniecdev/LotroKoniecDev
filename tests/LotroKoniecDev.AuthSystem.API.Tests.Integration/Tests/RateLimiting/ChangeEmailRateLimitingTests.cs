using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// The e-mail change endpoint mails an address the caller types, so it can be aimed at a stranger's inbox.
/// Its budget matches forgot-password's and is keyed by the client IP. These tests turn the limiter on for a
/// derived host.
/// </summary>
public sealed class ChangeEmailRateLimitingTests : EndpointsTestBase
{
    /// <summary>Mirrors the change-email-limit policy: 3 permits per hour per client IP.</summary>
    private const int ChangeEmailPermitLimit = 3;

    private static readonly Uri ChangeEmailEndpoint = new("auth/account/change-email", UriKind.Relative);

    public ChangeEmailRateLimitingTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Fact]
    public async Task ChangeEmail_ShouldThrottleTheFourthAttemptWithinTheHour()
    {
        // The requests are unauthenticated: UseRateLimiter runs before UseAuthentication, so the limiter
        // never sees a principal, which is why the key is the IP.
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();

        HttpStatusCode[] statusCodes = new HttpStatusCode[ChangeEmailPermitLimit + 1];

        for (int i = 0; i < statusCodes.Length; i++)
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                ChangeEmailEndpoint,
                new ChangeEmailRequest($"burst-{i}@lotro-translator.pl", "TestPass1!"));
            statusCodes[i] = response.StatusCode;
        }

        statusCodes.Take(ChangeEmailPermitLimit)
            .ShouldAllBe(statusCode => statusCode != HttpStatusCode.TooManyRequests);
        statusCodes[ChangeEmailPermitLimit].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    private WebApplicationFactory<Program> CreateRateLimitedHost()
    {
        return Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "RateLimiting:ForceEnable", "true" }
                });
            });
        });
    }
}
