using System.Text.Json;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Health;

[Collection("AuthApi")]
public sealed class HealthEndpointsTests
{
    private const string HealthCheckKey = "a-health-check-key-of-at-least-32-characters";

    private readonly AuthSystemApiFactory _factory;

    public HealthEndpointsTests(AuthSystemApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetHealth_WithoutToken_ShouldSurfaceDbSmtpAndBrokerChecks()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();

        // Act
        HttpResponseMessage response = await client.GetAsync("/health");
        string body = await response.Content.ReadAsStringAsync();

        // Assert: Testing points SMTP and the broker at dead ports, so the full report is
        // Unhealthy (503) by design; what this test pins is that the db + smtp + broker checks
        // stay reachable on demand here.
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        body.ShouldContain("authdb");
        body.ShouldContain("smtp");
        body.ShouldContain("rabbitmq");
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

        // Assert: ACA probes this path every few seconds; it must never touch Postgres (ADR-0025)
        // nor the broker (a broker outage must not pull auth out of the ingress rotation).
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain("\"status\": \"Healthy\"");
        body.ShouldNotContain("authdb");
        body.ShouldNotContain("rabbitmq");
    }

    [Fact]
    public async Task GetHealth_WithFailedChecks_ShouldNotShowTheirErrorText()
    {
        // Arrange: Testing points SMTP at :59999 and the broker at :59998, and both checks put that
        // address into their failure text. The daily health ping prints this body into a public job log.
        using HttpClient client = _factory.CreateClient();

        // Act
        HttpResponseMessage response = await client.GetAsync("/health");
        string body = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        using JsonDocument report = JsonDocument.Parse(body);
        JsonElement[] checks = [.. report.RootElement.GetProperty("checks").EnumerateArray()];
        checks.ShouldContain(check => check.GetProperty("status").GetString() == "Unhealthy");
        checks.ShouldAllBe(check =>
            check.EnumerateObject().Select(property => property.Name).SequenceEqual(new[] { "name", "status", "duration" }));
        body.ShouldNotContain("localhost:59999");
        body.ShouldNotContain("localhost:59998");
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
        body.ShouldNotContain("authdb");
        body.ShouldNotContain("smtp");
        body.ShouldNotContain("rabbitmq");
    }

    [Fact]
    public async Task GetHealth_WhenAKeyIsConfiguredAndTheCallerSendsIt_ShouldRunTheDbSmtpAndBrokerChecks()
    {
        // Arrange
        using WebApplicationFactory<Program> keyedHost = CreateKeyedHost();
        using HttpClient client = keyedHost.CreateClient();

        // Act
        using HttpResponseMessage response = await GetAsync(client, "/health", HealthCheckKey);
        string body = await response.Content.ReadAsStringAsync();

        // Assert: Unhealthy (503) by design here, see the first test; what matters is that the checks ran
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        body.ShouldContain("authdb");
        body.ShouldContain("smtp");
        body.ShouldContain("rabbitmq");
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
    public void Boot_WithAKeyShorterThanTheMinimum_ShouldFailNamingTheKey()
    {
        // Arrange: the validator refuses a short key in every environment, so a Testing host proves that
        // the validator is registered and runs at startup. Without that, a deployed host with no key would
        // boot with the full /health open.
        using WebApplicationFactory<Program> shortKeyHost = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "HealthCheck:Key", new string('h', 31) }
                });
            });
        });

        // Act
        Exception exception = Should.Throw<Exception>(() => shortKeyHost.CreateClient());

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
