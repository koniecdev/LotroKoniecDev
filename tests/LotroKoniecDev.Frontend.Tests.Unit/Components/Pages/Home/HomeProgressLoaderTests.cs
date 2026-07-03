using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.Frontend.Components.Pages.Home;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Progress;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Home;

public sealed class HomeProgressLoaderTests
{
    private const string BaseUrl = "https://localhost:5002/";

    // Mirrors the JSON options the Frontend's HTTP seam uses (HttpClientApiExtensions) so the stub
    // body deserializes through the exact same contract the loader relies on.
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task LoadAsync_WhenApiReturnsProgress_DeserializesEveryField()
    {
        PublicProgressResponse progress = new(Total: 1500, Translated: 900, Approved: 600, CurrentGameVersion: "48.1");
        HomeProgressLoader loader = CreateLoader(progress, out _);

        ApiResult<PublicProgressResponse> result = await loader.LoadAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value.Total.ShouldBe(1500);
        result.Value.Translated.ShouldBe(900);
        result.Value.Approved.ShouldBe(600);
        result.Value.CurrentGameVersion.ShouldBe("48.1");
    }

    [Fact]
    public async Task LoadAsync_WhenNoVersionProcessedYet_DeserializesTheNullVersion()
    {
        HomeProgressLoader loader = CreateLoader(
            new PublicProgressResponse(0, 0, 0, CurrentGameVersion: null),
            out _);

        ApiResult<PublicProgressResponse> result = await loader.LoadAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value.CurrentGameVersion.ShouldBeNull();
    }

    [Fact]
    public async Task LoadAsync_RequestsThePublicProgressEndpoint()
    {
        HomeProgressLoader loader = CreateLoader(
            new PublicProgressResponse(0, 0, 0, null),
            out StubHttpMessageHandler handler);

        await loader.LoadAsync();

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.ToString().ShouldBe($"{BaseUrl}api/v1/progress");
    }

    [Fact]
    public async Task LoadAsync_WhenApiFails_ReturnsFailureWithProblemDetails()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.InternalServerError,
            """{ "title": "Nie udało się wczytać postępu.", "status": 500 }""");
        HomeProgressLoader loader = new(CreateClient(handler));

        ApiResult<PublicProgressResponse> result = await loader.LoadAsync();

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails.ShouldNotBeNull();
        result.ProblemDetails!.Status.ShouldBe(500);
    }

    private static HomeProgressLoader CreateLoader(
        PublicProgressResponse progress,
        out StubHttpMessageHandler handler)
    {
        handler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(progress, ApiJsonOptions));
        return new HomeProgressLoader(CreateClient(handler));
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
