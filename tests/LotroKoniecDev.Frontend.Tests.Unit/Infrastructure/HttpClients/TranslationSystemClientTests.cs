using System.Net;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;

public sealed class TranslationSystemClientTests
{
    private const string BaseUrl = "https://localhost:5004/";

    [Fact]
    public async Task GetHealthAsync_WhenApiReportsHealthy_ReturnsSuccessWithStatus()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            """{ "status": "Healthy", "totalDuration": "00:00:00.01", "checks": [] }""");
        ITranslationSystemClient client = CreateClient(handler);

        ApiResult<HealthStatusResponse> result = await client.GetHealthAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe("Healthy");
    }

    [Fact]
    public async Task GetHealthAsync_RequestsTheHealthEndpointRelativeToTheBaseAddress()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            """{ "status": "Healthy" }""");
        ITranslationSystemClient client = CreateClient(handler);

        await client.GetHealthAsync();

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri.ShouldBe(new Uri("https://localhost:5004/health"));
    }

    [Fact]
    public async Task GetApiResultAsync_WhenSuccessHasEmptyBody_ReturnsSuccessWithDefaultValue()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(HttpStatusCode.NoContent, string.Empty);
        ITranslationSystemClient client = CreateClient(handler);

        ApiResult<string> result = await client.GetApiResultAsync<string>("anything");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task GetHealthAsync_WhenApiReturnsServiceUnavailable_ReturnsFailureWithProblemDetails()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.ServiceUnavailable,
            """{ "title": "Unhealthy", "status": 503 }""");
        ITranslationSystemClient client = CreateClient(handler);

        ApiResult<HealthStatusResponse> result = await client.GetHealthAsync();

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails.ShouldNotBeNull();
        result.ProblemDetails!.Status.ShouldBe(503);
    }

    private static ITranslationSystemClient CreateClient(StubHttpMessageHandler handler)
    {
        HttpClient httpClient = new(handler)
        {
            BaseAddress = new Uri(BaseUrl)
        };
        return new TranslationSystemClient(httpClient);
    }
}
