using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// #855: a 429 from fixed-by-ip says when the caller may try again, so a client such as the CLI does
/// not have to guess. The limiter is forced on for a derived host, and every test creates its own host,
/// so its buckets are its own. In Testing <c>UseForwardedHeaders</c> trusts every peer, so
/// <c>X-Forwarded-For</c> names the caller.
/// </summary>
[Collection("TranslationApi")]
public sealed class RetryAfterTests
{
    /// <summary>Mirrors the fixed-by-ip policy every TMS endpoint sits on: 100 requests per minute per client.</summary>
    private const int Bucket = 100;

    private const string ForwardedForHeader = "X-Forwarded-For";
    private const string DiscoveryPath = "/";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly TranslationSystemApiFactory _factory;

    public RetryAfterTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetDiscovery_WhenOverTheLimit_ShouldSayWhenToTryAgain()
    {
        // Arrange
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        const string callerAddress = "203.0.113.80";
        await SpendBucketAsync(client, callerAddress);

        // Act
        using HttpResponseMessage overTheLimit = await GetDiscoveryAsync(client, callerAddress);

        // Assert: a fraction of a second would not parse as a delta, so a delta proves whole seconds
        overTheLimit.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        overTheLimit.Headers.RetryAfter.ShouldNotBeNull();
        overTheLimit.Headers.RetryAfter!.Delta.ShouldNotBeNull();
        overTheLimit.Headers.RetryAfter.Delta!.Value.ShouldBeInRange(TimeSpan.FromSeconds(1), Window);
    }

    private static async Task<HttpResponseMessage> GetDiscoveryAsync(HttpClient client, string callerAddress)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(DiscoveryPath, UriKind.Relative));
        request.Headers.Add(ForwardedForHeader, callerAddress);
        return await client.SendAsync(request);
    }

    // Spends the whole bucket. Not an assertion: a call refused inside the bucket means the next call
    // is not the first one over the limit, so it throws.
    private static async Task SpendBucketAsync(HttpClient client, string callerAddress)
    {
        for (int i = 0; i < Bucket; i++)
        {
            using HttpResponseMessage response = await GetDiscoveryAsync(client, callerAddress);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException(
                    $"Call {i + 1} from {callerAddress} answered {response.StatusCode}, not {HttpStatusCode.OK}.");
            }
        }
    }

    private WebApplicationFactory<Program> CreateRateLimitedHost()
    {
        return _factory.WithWebHostBuilder(builder =>
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
