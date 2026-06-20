using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.Frontend.Components.Pages.Dashboard;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Dashboard;

public sealed class DashboardStatsLoaderTests
{
    private const string BaseUrl = "https://localhost:5002/";

    // Mirrors the JSON options the Frontend's HTTP seam uses (HttpClientApiExtensions) so the stub
    // body deserializes through the exact same contract the loader relies on.
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task LoadAsync_WhenApiReturnsStats_DeserializesEveryCounter()
    {
        TranslationStatsResponse stats = new(Total: 1500, Translated: 900, Approved: 600, Remaining: 900);
        DashboardStatsLoader loader = CreateLoader(stats, out _);

        ApiResult<TranslationStatsResponse> result = await loader.LoadAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value.Total.ShouldBe(1500);
        result.Value.Translated.ShouldBe(900);
        result.Value.Approved.ShouldBe(600);
        result.Value.Remaining.ShouldBe(900);
    }

    [Fact]
    public async Task LoadAsync_RequestsTheStatsEndpoint()
    {
        DashboardStatsLoader loader = CreateLoader(
            new TranslationStatsResponse(0, 0, 0, 0),
            out StubHttpMessageHandler handler);

        await loader.LoadAsync();

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.ToString().ShouldBe($"{BaseUrl}api/v1/translations/stats");
    }

    [Fact]
    public async Task LoadAsync_WhenApiFails_ReturnsFailureWithProblemDetails()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.InternalServerError,
            """{ "title": "Nie udało się wczytać statystyk.", "status": 500 }""");
        DashboardStatsLoader loader = new(CreateClient(handler));

        ApiResult<TranslationStatsResponse> result = await loader.LoadAsync();

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails.ShouldNotBeNull();
        result.ProblemDetails!.Status.ShouldBe(500);
    }

    private static DashboardStatsLoader CreateLoader(
        TranslationStatsResponse stats,
        out StubHttpMessageHandler handler)
    {
        handler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(stats, ApiJsonOptions));
        return new DashboardStatsLoader(CreateClient(handler));
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
