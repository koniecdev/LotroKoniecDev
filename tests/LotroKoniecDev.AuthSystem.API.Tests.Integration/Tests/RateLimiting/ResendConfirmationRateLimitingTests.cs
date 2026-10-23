using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// The resend limiter's budget belongs to sending, not to viewing the form: a Razor Page is one endpoint for
/// GET and POST, so a verb-blind limit would let three page views lock the user out of the form that
/// ADR-0046 sends blocked logins to. These tests turn the limiter on for a derived host.
/// </summary>
public sealed class ResendConfirmationRateLimitingTests : EndpointsTestBase
{
    /// <summary>Mirrors the resend-confirmation-limit policy: 3 permits per 15 minutes per client IP.</summary>
    private const int ResendConfirmationPermitLimit = 3;

    private static readonly Uri ResendConfirmationPage = new("/Account/ResendConfirmation", UriKind.Relative);

    public ResendConfirmationRateLimitingTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Fact]
    public async Task ResendConfirmationPage_ShouldNeverThrottleAPageView()
    {
        // Arrange: well past the permit limit, the traffic a user generates by opening the page,
        // going back to the login and following the resend link again
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();

        HttpStatusCode[] statusCodes = new HttpStatusCode[ResendConfirmationPermitLimit * 2 + 1];

        // Act
        for (int i = 0; i < statusCodes.Length; i++)
        {
            using HttpResponseMessage response = await client.GetAsync(ResendConfirmationPage);
            statusCodes[i] = response.StatusCode;
        }

        // Assert
        statusCodes.ShouldAllBe(statusCode => statusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResendConfirmationPage_ShouldStillThrottleSends()
    {
        // Arrange: page views must not have widened the hole the limiter exists to close
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();

        HttpStatusCode[] statusCodes = new HttpStatusCode[ResendConfirmationPermitLimit + 1];

        // Act
        for (int i = 0; i < statusCodes.Length; i++)
        {
            using FormUrlEncodedContent send = new(new Dictionary<string, string>
            {
                ["Email"] = $"burst-{i}@lotro-translator.pl"
            });
            using HttpResponseMessage response = await client.PostAsync(ResendConfirmationPage, send);
            statusCodes[i] = response.StatusCode;
        }

        // Assert
        statusCodes.Take(ResendConfirmationPermitLimit)
            .ShouldAllBe(statusCode => statusCode != HttpStatusCode.TooManyRequests);
        statusCodes[ResendConfirmationPermitLimit].ShouldBe(HttpStatusCode.TooManyRequests);
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
