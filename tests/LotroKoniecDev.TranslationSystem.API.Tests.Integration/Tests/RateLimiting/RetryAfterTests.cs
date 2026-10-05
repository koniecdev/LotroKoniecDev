using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// #855: a 429 from fixed-by-ip says how long to wait, so a client such as the CLI does not have to
/// guess. The value is the whole window, the longest wait, not the time left in it (#892). The limiter is
/// forced on for a derived host, and every test creates its own host, so its buckets are its own. In
/// Testing <c>UseForwardedHeaders</c> trusts every peer, so <c>X-Forwarded-For</c> names the caller.
/// </summary>
[Collection("TranslationApi")]
public sealed class RetryAfterTests
{
    /// <summary>Mirrors the fixed-by-ip policy every TMS endpoint sits on: 100 requests per minute per client.</summary>
    private const int Bucket = 100;

    private const string ForwardedForHeader = "X-Forwarded-For";
    private const string DiscoveryPath = "/";
    private const string GameVersionsPath = "/api/v1/game-versions";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly TranslationSystemApiFactory _factory;

    public RetryAfterTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData(DiscoveryPath, HttpStatusCode.OK)]
    [InlineData(GameVersionsPath, HttpStatusCode.Unauthorized)]
    public async Task Get_WhenTheBucketIsSpent_ShouldSayWhenToTryAgain(string path, HttpStatusCode underTheLimit)
    {
        // Arrange: the anonymous discovery root, and an API route called without a token
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        const string callerAddress = "203.0.113.80";
        await SpendBucketAsync(client, path, callerAddress, underTheLimit);

        // Act
        using HttpResponseMessage overTheLimit = await GetAsync(client, path, callerAddress);

        // Assert: a fraction of a second would not parse as a delta, so a delta proves whole seconds
        overTheLimit.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        overTheLimit.Headers.RetryAfter.ShouldNotBeNull();
        overTheLimit.Headers.RetryAfter!.Delta.ShouldNotBeNull();
        overTheLimit.Headers.RetryAfter.Delta!.Value.ShouldBe(Window);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string callerAddress)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add(ForwardedForHeader, callerAddress);
        return await client.SendAsync(request);
    }

    private static async Task SpendBucketAsync(
        HttpClient client,
        string path,
        string callerAddress,
        HttpStatusCode underTheLimit)
    {
        for (int i = 0; i < Bucket; i++)
        {
            using HttpResponseMessage response = await GetAsync(client, path, callerAddress);

            // Not an assertion: a call refused inside the bucket means the next call is not the first one
            // over the limit, so the test would prove nothing.
            if (response.StatusCode != underTheLimit)
            {
                throw new InvalidOperationException(
                    $"Call {i + 1} to {path} from {callerAddress} answered {response.StatusCode}, not {underTheLimit}.");
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
