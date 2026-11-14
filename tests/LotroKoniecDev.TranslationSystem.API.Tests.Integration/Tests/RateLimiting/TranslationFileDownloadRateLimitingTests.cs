using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// Pins the download's own budget (#697, ADR-0064). The budget is spent with an unsupported language: the
/// limiter answers before the endpoint, so a 400 spends a permit like a 200, and no derived host ever
/// serves the file from the disk-copy folder it shares with the base host. Each test has its own host,
/// so its own buckets. In Testing <c>X-Forwarded-For</c> names the caller.
/// </summary>
[Collection("TranslationApi")]
public sealed class TranslationFileDownloadRateLimitingTests
{
    /// <summary>Must match the translation-file-download policy in Program.cs.</summary>
    private const int Budget = 5;

    private const string FrontendAddress = "10.60.0.7";
    private const string ForwardedForHeader = "X-Forwarded-For";
    private const string DiscoveryPath = "/";
    private const string UnsupportedLanguagePath = "/api/v1/translation-files/de";

    // Built rather than written out, so no secret scanner mistakes test data for a key.
    private static readonly string FrontendKey = new('k', 40);

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly TranslationSystemApiFactory _factory;

    [Fact]
    public async Task Download_WhenTheBudgetIsSpent_ShouldAnswer429AndSayWhenToTryAgain()
    {
        // Arrange
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        Caller caller = Caller.Direct("203.0.113.90");
        await SpendBudgetAsync(client, caller);

        // Act
        using HttpResponseMessage overTheBudget = await GetAsync(client, UnsupportedLanguagePath, caller);

        // Assert: the whole window, as for fixed-by-ip (#892)
        overTheBudget.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        overTheBudget.Headers.RetryAfter.ShouldNotBeNull();
        overTheBudget.Headers.RetryAfter!.Delta.ShouldBe(Window);
    }

    [Fact]
    public async Task Download_WhenTheBudgetIsSpent_ShouldLeaveOtherEndpointsOpenForTheSameCaller()
    {
        // Arrange
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        Caller caller = Caller.Direct("203.0.113.91");
        await SpendBudgetAsync(client, caller);

        // Act
        using HttpResponseMessage discovery = await GetAsync(client, DiscoveryPath, caller);

        // Assert: the download budget is a bucket of its own, not a cut of fixed-by-ip
        discovery.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Download_WhenOneAddressSpentTheBudget_ShouldStillAnswerAnotherAddress()
    {
        // Arrange
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        await SpendBudgetAsync(client, Caller.Direct("203.0.113.92"));

        // Act
        using HttpResponseMessage response = await GetAsync(client, UnsupportedLanguagePath, Caller.Direct("203.0.113.93"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Download_TwoVisitorsBehindTheFrontend_ShouldEachGetTheirOwnBudget()
    {
        // Arrange: every landing-page download comes from the frontend container (ADR-0054).
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        await SpendBudgetAsync(client, Caller.ThroughTheFrontend("203.0.113.94"));

        // Act
        using HttpResponseMessage bystander = await GetAsync(
            client, UnsupportedLanguagePath, Caller.ThroughTheFrontend("203.0.113.95"));

        // Assert: the bucket that filled is the first visitor's, not the container's
        bystander.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    public TranslationFileDownloadRateLimitingTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(string ConnectionAddress, IReadOnlyCollection<string> KeyValues, IReadOnlyCollection<string> AddressValues)
    {
        public static Caller Direct(string address) => new(address, [], []);

        public static Caller ThroughTheFrontend(string visitorAddress) => new(FrontendAddress, [FrontendKey], [visitorAddress]);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, Caller caller)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add(ForwardedForHeader, caller.ConnectionAddress);

        foreach (string keyValue in caller.KeyValues)
        {
            request.Headers.Add(FrontendCallerHeaders.Key, keyValue);
        }

        foreach (string addressValue in caller.AddressValues)
        {
            request.Headers.Add(FrontendCallerHeaders.ClientAddress, addressValue);
        }

        return await client.SendAsync(request);
    }

    private static async Task SpendBudgetAsync(HttpClient client, Caller caller)
    {
        for (int i = 0; i < Budget; i++)
        {
            using HttpResponseMessage response = await GetAsync(client, UnsupportedLanguagePath, caller);

            // Throws, not asserts: a refusal inside the budget would make the test prove nothing.
            if (response.StatusCode != HttpStatusCode.BadRequest)
            {
                throw new InvalidOperationException(
                    $"Call {i + 1} from {caller.ConnectionAddress} answered {response.StatusCode}, not {HttpStatusCode.BadRequest}.");
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
                    { "RateLimiting:ForceEnable", "true" },
                    { "FrontendCaller:Key", FrontendKey }
                });
            });
        });
    }
}
