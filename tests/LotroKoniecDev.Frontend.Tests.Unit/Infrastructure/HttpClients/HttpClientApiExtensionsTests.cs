using System.Net;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
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

    [Fact]
    public async Task PostForHeadersApiResultAsync_OnSuccess_CapturesTheResponseHeaders()
    {
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.RespondWithHeaders(
            HttpStatusCode.NoContent,
            new Dictionary<string, string>
            {
                ["X-Deletion-Scheduled-At"] = "2026-07-11T10:00:00.0000000+00:00",
                ["X-Deletion-Finalizes-At"] = "2026-07-25T10:00:00.0000000+00:00"
            }));

        ApiResult<ApiResponseHeaders> result = await httpClient.PostForHeadersApiResultAsync(
            "auth/account/delete",
            new { Password = "x" });

        result.IsSuccess.ShouldBeTrue();
        result.Value.GetValueOrDefault("X-Deletion-Finalizes-At").ShouldBe("2026-07-25T10:00:00.0000000+00:00");
        // Header names are case-insensitive per RFC 9110 — the capture must honor that.
        result.Value.GetValueOrDefault("x-deletion-scheduled-at").ShouldBe("2026-07-11T10:00:00.0000000+00:00");
        result.Value.GetValueOrDefault("X-Missing").ShouldBeNull();
    }

    [Fact]
    public async Task PostForHeadersApiResultAsync_WhenApiReturnsProblem_MapsToFailureWithProblemDetails()
    {
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.UnprocessableEntity,
            """{ "title": "Nieprawidłowe hasło", "status": 422 }"""));

        ApiResult<ApiResponseHeaders> result = await httpClient.PostForHeadersApiResultAsync(
            "auth/account/delete",
            new { Password = "x" });

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(422);
    }

    [Fact]
    public async Task PostForHeadersApiResultAsync_WhenTransportFails_MapsToServiceUnavailableProblem()
    {
        HttpClient httpClient = CreateClient(
            StubHttpMessageHandler.Throw(new HttpRequestException("connection refused")));

        ApiResult<ApiResponseHeaders> result = await httpClient.PostForHeadersApiResultAsync(
            "auth/account/delete",
            new { Password = "x" });

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(503);
    }

    [Fact]
    public async Task GetApiResultAsync_WhenErrorBodyIsEmpty_SynthesizesAProblemCarryingTheStatusCode()
    {
        // A bare 401 from the JWT bearer challenge has no body — the seam must not crash on the empty
        // JSON and must keep the status so IsUnauthorized classification works.
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.Unauthorized,
            string.Empty));

        ApiResult<string> result = await httpClient.GetApiResultAsync<string>("auth/account/data-export");

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(401);
        result.IsUnauthorized.ShouldBeTrue();
    }

    [Fact]
    public async Task GetApiResultAsync_WhenErrorBodyIsNotJson_SynthesizesAProblemCarryingTheStatusCode()
    {
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.Forbidden,
            "<html>Forbidden</html>"));

        ApiResult<string> result = await httpClient.GetApiResultAsync<string>("auth/account/data-export");

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(403);
        result.IsForbidden.ShouldBeTrue();
        result.IsUnauthorized.ShouldBeFalse();
    }

    [Theory]
    [InlineData("<html><head><title>502 Bad Gateway</title></head><body></body></html>")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("plain text")]
    public async Task GetApiResultAsync_WhenTheErrorBodyCarriesNoProblem_SynthesizesOneThatStaysTranslatable(string body)
    {
        // The marker means "already Polish, render as-is", so stamping it here skipped the status
        // ladder and rendered a placeholder during the staging outage (#637).
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.BadGateway, body));

        ApiResult<string> result = await httpClient.GetApiResultAsync<string>("translation-files/pl");

        result.ProblemDetails!.Extensions.ShouldNotContainKey(ApiProblemCopy.FrontendAuthoredExtensionKey);
        result.ProblemDetails.Title.ShouldBeNull();
        result.ProblemDetails.Detail.ShouldBeNull();
        result.ProblemDetails.Status.ShouldBe(502);
    }

    [Fact]
    public async Task GetApiResultAsync_WhenProblemBodyLacksAStatus_BackfillsItFromTheResponse()
    {
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.Unauthorized,
            """{ "title": "Brak autoryzacji" }"""));

        ApiResult<string> result = await httpClient.GetApiResultAsync<string>("auth/account/data-export");

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Title.ShouldBe("Brak autoryzacji");
        result.ProblemDetails.Status.ShouldBe(401);
        result.IsUnauthorized.ShouldBeTrue();
    }

    private static HttpClient CreateClient(StubHttpMessageHandler handler)
    {
        return new HttpClient(handler)
        {
            BaseAddress = new Uri("https://localhost:5002/")
        };
    }
}
