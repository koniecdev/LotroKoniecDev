using System.Net.Http.Headers;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// #829: calls the API refuses with 401 or 403 count against fixed-by-ip, and a call over the limit
/// creates no Translator profile. The limiter is forced on for a derived host, and every test creates
/// its own host, so its buckets are its own. In Testing <c>UseForwardedHeaders</c> trusts every peer,
/// so <c>X-Forwarded-For</c> names the caller.
/// </summary>
[Collection("TranslationApi")]
public sealed class RefusedCallsRateLimitingTests : IAsyncLifetime
{
    /// <summary>Mirrors the fixed-by-ip policy every TMS endpoint sits on: 100 requests per minute per client.</summary>
    private const int Bucket = 100;

    private const string ForwardedForHeader = "X-Forwarded-For";
    private const string DiscoveryPath = "/";
    private const string GameVersionsPath = "/api/v1/game-versions";

    private readonly TranslationSystemApiFactory _factory;

    public RefusedCallsRateLimitingTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public enum RefusedToken
    {
        None,
        Expired,
        UnknownSigningKey,
        Malformed,
        RoleWithoutAccess
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"Translators\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(RefusedToken.None, HttpStatusCode.Unauthorized)]
    [InlineData(RefusedToken.Expired, HttpStatusCode.Unauthorized)]
    [InlineData(RefusedToken.UnknownSigningKey, HttpStatusCode.Unauthorized)]
    [InlineData(RefusedToken.Malformed, HttpStatusCode.Unauthorized)]
    [InlineData(RefusedToken.RoleWithoutAccess, HttpStatusCode.Forbidden)]
    public async Task GetGameVersions_WhenRefusedCallsSpentTheBucket_ShouldReturnTooManyRequests(
        RefusedToken refusedToken,
        HttpStatusCode refusal)
    {
        // Arrange: a caller spends a whole bucket on calls the API refuses
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        const string callerAddress = "203.0.113.70";
        string? accessToken = CreateToken(refusedToken);
        await SpendBucketOnRefusedCallsAsync(client, callerAddress, accessToken, refusal);

        // Act
        using HttpResponseMessage overTheLimit = await GetAsync(client, GameVersionsPath, callerAddress, accessToken);

        // Assert
        overTheLimit.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task GetGameVersions_WhenANewTranslatorIsOverTheLimit_ShouldNotCreateATranslatorProfile()
    {
        // Arrange: a first translator under the limit gets a profile, which proves provisioning works on
        // this host. The second caller's bucket is full, and that translator has never called the API.
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        await ProvisionControlTranslatorAsync(client, "203.0.113.72");
        const string callerAddress = "203.0.113.71";
        await ExhaustBucketAsync(client, callerAddress);
        string newTranslatorToken = TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator);

        // Act
        using HttpResponseMessage overTheLimit = await GetAsync(client, GameVersionsPath, callerAddress, newTranslatorToken);

        // Assert: the limiter stopped the call before the provisioning could write a second profile
        overTheLimit.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await CountTranslatorsAsync()).ShouldBe(1);
    }

    private static string? CreateToken(RefusedToken refusedToken) => refusedToken switch
    {
        RefusedToken.None => null,
        RefusedToken.Expired => TranslationSystemApiFactory.CreateExpiredAccessToken(),
        RefusedToken.UnknownSigningKey => TranslationSystemApiFactory.CreateTokenSignedWithUnknownKey(),
        RefusedToken.Malformed => "not-a-jwt",
        RefusedToken.RoleWithoutAccess => TranslationSystemApiFactory.CreateAccessToken("Reviewer"),
        _ => throw new ArgumentOutOfRangeException(nameof(refusedToken), refusedToken, null)
    };

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string path,
        string callerAddress,
        string? accessToken = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add(ForwardedForHeader, callerAddress);

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await client.SendAsync(request);
    }

    // Spends a whole bucket on calls the API refuses. Not an assertion: a call that got through would
    // prove nothing about refused calls, so it throws.
    private static async Task SpendBucketOnRefusedCallsAsync(
        HttpClient client,
        string callerAddress,
        string? accessToken,
        HttpStatusCode refusal)
    {
        for (int i = 0; i < Bucket; i++)
        {
            using HttpResponseMessage response = await GetAsync(client, GameVersionsPath, callerAddress, accessToken);
            if (response.StatusCode != refusal)
            {
                throw new InvalidOperationException(
                    $"Call {i + 1} from {callerAddress} answered {response.StatusCode}, not {refusal}.");
            }
        }
    }

    // Spends a whole bucket on the anonymous discovery root. Not an assertion: a bucket that did not
    // fill is a broken precondition for the test that called this, so it throws.
    private static async Task ExhaustBucketAsync(HttpClient client, string callerAddress)
    {
        for (int i = 0; i < Bucket; i++)
        {
            using HttpResponseMessage response = await GetAsync(client, DiscoveryPath, callerAddress);
        }

        using HttpResponseMessage probe = await GetAsync(client, DiscoveryPath, callerAddress);
        if (probe.StatusCode != HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException(
                $"The bucket did not fill after {Bucket} requests from {callerAddress} (the probe answered {probe.StatusCode}).");
        }
    }

    // Not an assertion either: without a profile here, a count of one after the over-limit call would
    // not prove the limiter stopped anything.
    private async Task ProvisionControlTranslatorAsync(HttpClient client, string callerAddress)
    {
        string controlToken = TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator);
        using HttpResponseMessage response = await GetAsync(client, GameVersionsPath, callerAddress, controlToken);
        int translators = await CountTranslatorsAsync();
        if (response.StatusCode != HttpStatusCode.OK || translators != 1)
        {
            throw new InvalidOperationException(
                $"The control translator's call answered {response.StatusCode} and left {translators} profile(s), not 200 and 1.");
        }
    }

    private async Task<int> CountTranslatorsAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        return await dbContext.Translators.CountAsync();
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
