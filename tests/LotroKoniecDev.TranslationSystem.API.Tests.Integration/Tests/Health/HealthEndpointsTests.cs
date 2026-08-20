namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Health;

[Collection("TranslationApi")]
public sealed class HealthEndpointsTests
{
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
}
