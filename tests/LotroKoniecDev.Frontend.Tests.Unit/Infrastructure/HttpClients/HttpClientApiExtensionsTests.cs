using System.Net;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Progress;
using LotroKoniecDev.TranslationSystem.Contracts.Translators;
using Microsoft.AspNetCore.Http;
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
        // The body is a plain text file and not JSON, so it has to come back unchanged. The JSON helpers
        // would throw on it. That is the whole difference between GetTextAsync and GetApiResultAsync.
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
        // RFC 9110 says header names ignore case, and the capture has to do the same.
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
        // A plain 401 from the JWT bearer challenge has no body. The code must not crash on the empty
        // JSON and must keep the status, so the IsUnauthorized check still works.
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

    [Theory]
    [InlineData("<html><head><title>Maintenance</title></head><body>Back soon</body></html>")]
    [InlineData("<!DOCTYPE html><html><body><form action=\"/login\"></form></body></html>")]
    [InlineData("plain text")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"a string where an object was expected\"")]
    [InlineData("""{ "total": "abc" }""")]
    [InlineData("""{ "total": 1""")]
    public async Task GetApiResultAsync_WhenASuccessBodyIsNotTheApisJson_FailsWithATranslatableBadGatewayProblem(string body)
    {
        // A proxy serving its maintenance page with a 200, or an auth redirect putting a login page on an
        // API URL: the status says success, but the body is not the API's answer. That used to throw a
        // JsonException out of the render (#638). It belongs to the #637 outage class and is handled the
        // same way: a problem with only a status, which the Polish text ladder answers, never an
        // exception.
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, body));

        ApiResult<PublicProgressResponse> result =
            await httpClient.GetApiResultAsync<PublicProgressResponse>("progress");

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(StatusCodes.Status502BadGateway);
        result.ProblemDetails.Extensions.ShouldNotContainKey(ApiProblemCopy.FrontendAuthoredExtensionKey);
        result.ProblemDetails.Title.ShouldBeNull();
        result.ProblemDetails.Detail.ShouldBeNull();
        result.IsUnauthorized.ShouldBeFalse();
        result.IsForbidden.ShouldBeFalse();
    }

    [Fact]
    public async Task GetApiResultAsync_WhenAStronglyTypedIdInTheSuccessBodyIsMalformed_FailsWithATranslatableBadGatewayProblem()
    {
        // StronglyTypedIdJsonConverter is the one converter here that is ours and not System.Text.Json's.
        // A bad GUID still has to come out as a JsonException, which we catch, and not as a
        // FormatException, which would escape the render the way the raw JsonException used to (#638).
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            """
            {
              "profile": {
                "translatorId": "not-a-guid",
                "identityId": "also-not-a-guid",
                "displayName": "Frodo",
                "email": null,
                "provisionedAt": "2026-07-11T10:00:00+00:00"
              },
              "contributions": null
            }
            """));

        ApiResult<TranslatorDataExportResponse> result =
            await httpClient.GetApiResultAsync<TranslatorDataExportResponse>("translators/me/data-export");

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(StatusCodes.Status502BadGateway);
        result.ProblemDetails.Extensions.ShouldNotContainKey(ApiProblemCopy.FrontendAuthoredExtensionKey);
    }

    [Fact]
    public async Task GetApiResultAsync_WhenASuccessBodyIsNotTheApisJson_DescribesAsTheSameCopyAsAProxyBadGateway()
    {
        // The promise this code makes: what the page shows for a maintenance page with a 200 is exactly
        // what it shows for the proxy's own 502. One kind of outage, one sentence.
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            "<html><body>Maintenance</body></html>"));

        ApiResult<PublicProgressResponse> result =
            await httpClient.GetApiResultAsync<PublicProgressResponse>("progress");

        ApiProblemCopy.Describe(result.ProblemDetails, "Nie udało się wczytać postępu.").Message
            .ShouldBe("Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.");
    }

    [Fact]
    public async Task PostApiResultAsync_WhenASuccessBodyIsNotTheApisJson_FailsWithATranslatableBadGatewayProblem()
    {
        // Every generic verb goes through the same code, so this rule is not only about GET.
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.Created,
            "<html><body>Maintenance</body></html>"));

        ApiResult<PublicProgressResponse> result = await httpClient.PostApiResultAsync<PublicProgressResponse>(
            "progress",
            new { Name = "x" });

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(StatusCodes.Status502BadGateway);
        result.ProblemDetails.Extensions.ShouldNotContainKey(ApiProblemCopy.FrontendAuthoredExtensionKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetApiResultAsync_WhenASuccessBodyIsEmpty_FailsWithATranslatableBadGatewayProblem(string body)
    {
        // The rule next to #638's: a call that promises a value and gets nothing back was not answered
        // either. It used to succeed with a null Value that every caller then used (#653). Now it counts
        // as the same unreadable body as a maintenance page and is handled the same way. Only the
        // body-less verbs and PostForHeadersApiResultAsync accept a 204.
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, body));

        ApiResult<PublicProgressResponse> result =
            await httpClient.GetApiResultAsync<PublicProgressResponse>("progress");

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(StatusCodes.Status502BadGateway);
        result.ProblemDetails.Extensions.ShouldNotContainKey(ApiProblemCopy.FrontendAuthoredExtensionKey);
        result.ProblemDetails.Title.ShouldBeNull();
        result.ProblemDetails.Detail.ShouldBeNull();
    }

    [Fact]
    public async Task GetApiResultAsync_WhenASuccessBodyIsEmpty_DescribesAsTheSameCopyAsAProxyBadGateway()
    {
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty));

        ApiResult<PublicProgressResponse> result =
            await httpClient.GetApiResultAsync<PublicProgressResponse>("progress");

        ApiProblemCopy.Describe(result.ProblemDetails, "Nie udało się wczytać postępu.").Message
            .ShouldBe("Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.");
    }

    [Fact]
    public async Task PostApiResultAsync_WhenTheBodyLessVerbGetsNoContent_Succeeds()
    {
        // The other side of that line: a 204 is still a success where nothing was promised. This is the
        // approve and delete path, and it must not take on the rule for calls that promise a value.
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.NoContent, string.Empty));

        ApiResult result = await httpClient.PostApiResultAsync("translations/1/approve", new { });

        result.IsSuccess.ShouldBeTrue();
        result.ProblemDetails.ShouldBeNull();
    }

    [Fact]
    public async Task GetApiResultAsync_WhenASuccessBodyIsTheApisJson_DeserializesIt()
    {
        HttpClient httpClient = CreateClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            """{ "total": 200, "translated": 150, "approved": 80, "currentGameVersion": "48.1" }"""));

        ApiResult<PublicProgressResponse> result =
            await httpClient.GetApiResultAsync<PublicProgressResponse>("progress");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new PublicProgressResponse(200, 150, 80, "48.1"));
    }

    private static HttpClient CreateClient(StubHttpMessageHandler handler)
    {
        return new HttpClient(handler)
        {
            BaseAddress = new Uri("https://localhost:5002/")
        };
    }
}
