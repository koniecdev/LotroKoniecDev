using System.Net;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;

public sealed class TranslationSystemClientTests
{
    private const string BaseUrl = "https://localhost:5002/";

    [Fact]
    public async Task GetApiResultAsync_RequestsTheUriRelativeToTheBaseAddress()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            """{ "value": 1 }""");
        ITranslationSystemClient client = CreateClient(handler);

        await client.GetApiResultAsync<object>("api/v1/anything");

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri.ShouldBe(new Uri("https://localhost:5002/api/v1/anything"));
    }

    [Fact]
    public async Task GetApiResultAsync_WhenSuccessHasEmptyBody_ReturnsFailureWithATranslatableBadGatewayProblem()
    {
        // A value-promising call answered with nothing — even a genuine 204 — is not answered (#653):
        // the typed client hands the seam's rule through unchanged instead of a null Value.
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(HttpStatusCode.NoContent, string.Empty);
        ITranslationSystemClient client = CreateClient(handler);

        ApiResult<string> result = await client.GetApiResultAsync<string>("anything");

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(StatusCodes.Status502BadGateway);
        result.ProblemDetails.Extensions.ShouldNotContainKey(ApiProblemCopy.FrontendAuthoredExtensionKey);
    }

    [Fact]
    public async Task GetApiResultAsync_WhenApiReturnsServiceUnavailable_ReturnsFailureWithProblemDetails()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.ServiceUnavailable,
            """{ "title": "Unhealthy", "status": 503 }""");
        ITranslationSystemClient client = CreateClient(handler);

        ApiResult<object> result = await client.GetApiResultAsync<object>("api/v1/anything");

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
