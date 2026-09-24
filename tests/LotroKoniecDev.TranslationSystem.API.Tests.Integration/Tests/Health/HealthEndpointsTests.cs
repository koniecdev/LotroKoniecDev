using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Health;

[Collection("TranslationApi")]
public sealed class HealthEndpointsTests
{
    private const string HealthCheckKey = "a-health-check-key-of-at-least-32-characters";

    private readonly TranslationSystemApiFactory _factory;

    public HealthEndpointsTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetHealth_WithoutToken_ShouldReturn200AndHealthy()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();

        // Act
        HttpResponseMessage response = await client.GetAsync("/health");
        string body = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain("Healthy");
        body.ShouldContain("translationdb");
    }

    [Fact]
    public async Task GetHealthLive_WithoutToken_ShouldReturn200()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();

        // Act
        HttpResponseMessage response = await client.GetAsync("/health/live");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetHealthReady_WithoutToken_ShouldReturn200WithoutDatabaseCheck()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();

        // Act
        HttpResponseMessage response = await client.GetAsync("/health/ready");
        string body = await response.Content.ReadAsStringAsync();

        // Assert: ACA probes this path every few seconds; it must never touch Postgres (ADR-0025).
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain("\"status\": \"Healthy\"");
        body.ShouldNotContain("translationdb");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("a-wrong-key-that-is-long-enough-to-pass")]
    public async Task GetHealth_WhenAKeyIsConfiguredAndTheCallerLacksIt_ShouldReturn404WithoutTheReport(string? presentedKey)
    {
        // Arrange
        using WebApplicationFactory<Program> keyedHost = CreateKeyedHost();
        using HttpClient client = keyedHost.CreateClient();

        // Act
        using HttpResponseMessage response = await GetAsync(client, "/health", presentedKey);
        string body = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        body.ShouldNotContain("translationdb");
    }

    [Fact]
    public async Task GetHealth_WhenAKeyIsConfiguredAndTheCallerSendsIt_ShouldRunTheDatabaseCheck()
    {
        // Arrange
        using WebApplicationFactory<Program> keyedHost = CreateKeyedHost();
        using HttpClient client = keyedHost.CreateClient();

        // Act
        using HttpResponseMessage response = await GetAsync(client, "/health", HealthCheckKey);
        string body = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain("translationdb");
        body.ShouldContain("\"status\": \"Healthy\"");
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task GetProbe_WhenAKeyIsConfigured_ShouldStayOpenWithoutTheKey(string path)
    {
        // Arrange: the probes run no checks, so the key does not guard them (ADR-0025, ADR-0058)
        using WebApplicationFactory<Program> keyedHost = CreateKeyedHost();
        using HttpClient client = keyedHost.CreateClient();

        // Act
        using HttpResponseMessage response = await GetAsync(client, path, presentedKey: null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void Boot_InADeployedEnvironmentWithoutTheKey_ShouldFailNamingTheKey()
    {
        // Arrange: Staging with every other setting a deployed host needs. FrontendCallerKeyTests boots
        // the same host with the key, so the missing key is the one reason this boot can fail.
        using WebApplicationFactory<Program> stagingHost = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Staging");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "Cors:AllowedOrigins:0", "https://app.lotro.test" },
                    { "FrontendCaller:Key", new string('k', 40) }
                });
            });
        });

        // Act
        Exception exception = Should.Throw<Exception>(() => stagingHost.CreateClient());

        // Assert
        exception.ToString().ShouldContain("HealthCheck:Key");
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? presentedKey)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        if (presentedKey is not null)
        {
            request.Headers.Add(HealthCheckHeaders.Key, presentedKey);
        }

        return await client.SendAsync(request);
    }

    private WebApplicationFactory<Program> CreateKeyedHost()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "HealthCheck:Key", HealthCheckKey }
                });
            });
        });
    }
}
