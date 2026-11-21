using System.Net.Http.Headers;
using LotroKoniecDev.SharedKernel.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// Pins the account buckets of #1488: a token with Reviewer or Admin counts per account at 300 a minute,
/// and every other caller still counts per address at 100. Each test has its own host, so its own
/// buckets. In Testing <c>X-Forwarded-For</c> names the caller.
/// </summary>
[Collection("TranslationApi")]
public sealed class TrustedRoleRateLimitingTests : IAsyncLifetime
{
    /// <summary>Must match <c>RequestRateLimitPartitioner</c>.</summary>
    private const int AddressBucket = 100;

    /// <summary>Must match <c>RequestRateLimitPartitioner</c>.</summary>
    private const int TrustedAccountBucket = 300;

    private const string ForwardedForHeader = "X-Forwarded-For";
    private const string GameVersionsPath = "/api/v1/game-versions";

    private readonly TranslationSystemApiFactory _factory;

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"Translators\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(AuthConstants.Roles.Reviewer)]
    [InlineData(AuthConstants.Roles.Admin)]
    public async Task GetGameVersions_AsATrustedRole_ShouldPassTheAddressLimitAndStopAfterItsOwnBucket(string trustedRole)
    {
        // Arrange
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        const string callerAddress = "203.0.113.80";
        string token = TranslationSystemApiFactory.CreateAccessTokenWithRoles(
            Guid.NewGuid(), AuthConstants.Roles.Translator, trustedRole);
        await SpendOkCallsAsync(client, callerAddress, token, TrustedAccountBucket);

        // Act
        using HttpResponseMessage overTheLimit = await GetAsync(client, callerAddress, token);

        // Assert
        overTheLimit.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task GetGameVersions_AsAPlainTranslator_ShouldStillStopAfterTheAddressBucket()
    {
        // Arrange
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        const string callerAddress = "203.0.113.81";
        string token = TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator);
        await SpendOkCallsAsync(client, callerAddress, token, AddressBucket);

        // Act
        using HttpResponseMessage overTheLimit = await GetAsync(client, callerAddress, token);

        // Assert
        overTheLimit.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task GetGameVersions_TwoReviewersBehindOneAddress_ShouldEachGetTheirOwnBucket()
    {
        // Arrange: the first reviewer spends a whole bucket.
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        const string sharedAddress = "203.0.113.82";
        string firstReviewer = ReviewerToken();
        string secondReviewer = ReviewerToken();
        await SpendOkCallsAsync(client, sharedAddress, firstReviewer, TrustedAccountBucket);
        using HttpResponseMessage bucketSpent = await GetAsync(client, sharedAddress, firstReviewer);
        bucketSpent.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Act
        using HttpResponseMessage secondReviewersCall = await GetAsync(client, sharedAddress, secondReviewer);

        // Assert
        secondReviewersCall.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetGameVersions_AfterAReviewersCalls_ShouldLeaveTheAddressBucketWhole()
    {
        // Arrange: a reviewer's calls go to the account bucket, not to the address it shares.
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        const string sharedAddress = "203.0.113.83";
        await SpendOkCallsAsync(client, sharedAddress, ReviewerToken(), AddressBucket + 50);
        string translator = TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator);
        await SpendOkCallsAsync(client, sharedAddress, translator, AddressBucket - 1);

        // Act
        using HttpResponseMessage lastAddressPermit = await GetAsync(client, sharedAddress, translator);

        // Assert
        lastAddressPermit.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    public TrustedRoleRateLimitingTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    private static string ReviewerToken() =>
        TranslationSystemApiFactory.CreateAccessTokenWithRoles(
            Guid.NewGuid(), AuthConstants.Roles.Translator, AuthConstants.Roles.Reviewer);

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string callerAddress, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(GameVersionsPath, UriKind.Relative));
        request.Headers.Add(ForwardedForHeader, callerAddress);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await client.SendAsync(request);
    }

    // Throws, not asserts: a call refused before the bucket is spent is a broken precondition.
    private static async Task SpendOkCallsAsync(HttpClient client, string callerAddress, string accessToken, int calls)
    {
        for (int i = 0; i < calls; i++)
        {
            using HttpResponseMessage response = await GetAsync(client, callerAddress, accessToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException(
                    $"Call {i + 1} from {callerAddress} answered {response.StatusCode}, not OK.");
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
