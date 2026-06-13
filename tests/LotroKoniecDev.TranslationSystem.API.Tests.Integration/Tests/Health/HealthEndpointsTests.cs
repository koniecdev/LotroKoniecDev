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
    public async Task GetHealthReady_WithoutToken_ShouldReturn200AndHealthyPostgres()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();

        // Act
        HttpResponseMessage response = await client.GetAsync("/health/ready");
        string body = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain("translationdb");
        body.ShouldContain("Healthy");
    }
}
