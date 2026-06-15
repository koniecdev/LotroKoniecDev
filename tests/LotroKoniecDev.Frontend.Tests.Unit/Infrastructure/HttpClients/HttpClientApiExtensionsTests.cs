using System.Net;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;

public sealed class HttpClientApiExtensionsTests
{
    [Fact]
    public async Task GetApiResultAsync_WhenCircuitIsOpen_MapsToServiceUnavailableProblem()
    {
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.Throw(new BrokenCircuitException()));

        ApiResult<string> result = await httpClient.GetApiResultAsync<string>("health");

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(503);
    }

    [Fact]
    public async Task GetApiResultAsync_WhenRequestTimesOut_MapsToGatewayTimeoutProblem()
    {
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.Throw(new TimeoutRejectedException()));

        ApiResult<string> result = await httpClient.GetApiResultAsync<string>("health");

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(504);
    }

    [Fact]
    public async Task GetApiResultAsync_WhenTransportFails_MapsToServiceUnavailableProblem()
    {
        HttpClient httpClient = CreateClient(
            StubHttpMessageHandler.Throw(new HttpRequestException("connection refused")));

        ApiResult<string> result = await httpClient.GetApiResultAsync<string>("health");

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(503);
        result.ProblemDetails.Title.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetTextAsync_OnSuccess_ReturnsTheRawBodyWithoutJsonParsing()
    {
        // The body is a plain text file, not JSON — it must survive verbatim (the JSON helpers would
        // throw on it). This is the load-bearing difference between GetTextAsync and GetApiResultAsync.
        const string body = "# polish.txt\n620756992||1001||Witaj||NULL||NULL||1";
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, body));

        ApiResult<string> result = await httpClient.GetTextAsync("api/v1/translation-files/pl");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(body);
    }

    [Fact]
    public async Task GetTextAsync_WhenApiReturnsProblem_MapsToFailureWithProblemDetails()
    {
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.NotFound,
            """{ "title": "Brak pliku tłumaczenia", "status": 404 }"""));

        ApiResult<string> result = await httpClient.GetTextAsync("api/v1/translation-files/pl");

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(404);
    }

    [Fact]
    public async Task GetTextAsync_WhenTransportFails_MapsToServiceUnavailableProblem()
    {
        HttpClient httpClient = CreateClient(
            StubHttpMessageHandler.Throw(new HttpRequestException("connection refused")));

        ApiResult<string> result = await httpClient.GetTextAsync("api/v1/translation-files/pl");

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(503);
    }

    private static HttpClient CreateClient(StubHttpMessageHandler handler)
    {
        return new HttpClient(handler)
        {
            BaseAddress = new Uri("https://localhost:5004/")
        };
    }
}
