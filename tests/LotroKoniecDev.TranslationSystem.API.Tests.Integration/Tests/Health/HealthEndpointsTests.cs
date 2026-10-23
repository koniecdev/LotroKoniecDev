using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

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

        // Assert: probed every few seconds, so it must never touch Postgres (ADR-0025).
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
        // Arrange: the probes run no checks (ADR-0025, ADR-0058)
        using WebApplicationFactory<Program> keyedHost = CreateKeyedHost();
        using HttpClient client = keyedHost.CreateClient();

        // Act
        using HttpResponseMessage response = await GetAsync(client, path, presentedKey: null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Boot_InADeployedEnvironmentWithoutTheKey_ShouldFailNamingTheKey(string environmentName)
    {
        // Arrange: FrontendCallerKeyTests boots this host with every setting, so the missing key is the
        // one reason this boot can fail (#1187).
        using WebApplicationFactory<Program> deployedHost = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environmentName);
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
        OptionsValidationException exception = Should.Throw<OptionsValidationException>(() => deployedHost.CreateClient());

        // Assert
        exception.Message.ShouldContain("HealthCheck:Key", Case.Sensitive);
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
