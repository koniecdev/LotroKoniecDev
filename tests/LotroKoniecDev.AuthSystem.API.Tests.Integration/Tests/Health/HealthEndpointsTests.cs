namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Health;

[Collection("AuthApi")]
public sealed class HealthEndpointsTests
{
    private readonly AuthSystemApiFactory _factory;

    public HealthEndpointsTests(AuthSystemApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetHealth_WithoutToken_ShouldSurfaceDbAndSmtpChecks()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();

        // Act
        HttpResponseMessage response = await client.GetAsync("/health");
        string body = await response.Content.ReadAsStringAsync();

        // Assert — Testing points SMTP at a dead port, so the full report is Unhealthy (503) by
        // design; what this test pins is that the db + smtp checks stay reachable on demand here.
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        body.ShouldContain("authdb");
        body.ShouldContain("smtp");
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

        // Assert — ACA probes this path every few seconds; it must never touch Postgres (ADR-0025).
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain("\"status\": \"Healthy\"");
        body.ShouldNotContain("authdb");
    }
}
