using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.Frontend.Components.Pages.ImportExport;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.ImportExport;

public sealed class ImportExportLoaderTests
{
    private const string BaseUrl = "https://localhost:5004/";
    private static readonly Guid GameVersionGuid = Guid.Parse("0192a000-0000-7000-8000-000000000099");

    // Mirrors the JSON options the Frontend's HTTP seam uses (HttpClientApiExtensions) so the stub
    // body deserializes through the exact same contract the loader relies on.
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task ListGameVersionsAsync_RequestsTheGameVersionsCollectionEndpoint()
    {
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new[] { VersionFixture() }, ApiJsonOptions),
            out StubHttpMessageHandler handler);

        await loader.ListGameVersionsAsync();

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.ToString().ShouldBe($"{BaseUrl}api/v1/game-versions");
    }

    [Fact]
    public async Task ListGameVersionsAsync_WhenApiReturnsVersions_DeserializesEachOne()
    {
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new[] { VersionFixture() }, ApiJsonOptions),
            out _);

        ApiResult<IReadOnlyList<GameVersionResponse>> result = await loader.ListGameVersionsAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(1);
        result.Value[0].Id.Value.ShouldBe(GameVersionGuid);
        result.Value[0].Version.ShouldBe("48.0");
        result.Value[0].Status.ShouldBe(GameVersionStatus.Unprocessed);
    }

    [Fact]
    public async Task ListGameVersionsAsync_WhenApiFails_ReturnsFailureWithProblemDetails()
    {
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.InternalServerError,
            """{ "title": "Nie udało się wczytać wersji.", "status": 500 }""",
            out _);

        ApiResult<IReadOnlyList<GameVersionResponse>> result = await loader.ListGameVersionsAsync();

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(500);
    }

    [Fact]
    public async Task ImportAsync_PostsMultipartToTheVersionScopedImportEndpoint()
    {
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(SummaryFixture(), ApiJsonOptions),
            out StubHttpMessageHandler handler);
        using MemoryStream file = new(Encoding.UTF8.GetBytes("620756992||1001||Witaj||NULL||NULL||1"));

        await loader.ImportAsync(new GameVersionId(GameVersionGuid), file, "exported.txt", allowMassRemoval: false);

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString()
            .ShouldBe($"{BaseUrl}api/v1/game-versions/{GameVersionGuid}/import?allowMassRemoval=false");
    }

    [Fact]
    public async Task ImportAsync_SendsTheUploadedFileAsMultipartFormData()
    {
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(SummaryFixture(), ApiJsonOptions),
            out StubHttpMessageHandler handler);
        using MemoryStream file = new(Encoding.UTF8.GetBytes("620756992||1001||Witaj||NULL||NULL||1"));

        await loader.ImportAsync(new GameVersionId(GameVersionGuid), file, "exported.txt", allowMassRemoval: false);

        handler.LastRequestBody.ShouldNotBeNull();
        handler.LastRequestBody!.ShouldContain("name=file");
        handler.LastRequestBody.ShouldContain("exported.txt");
        handler.LastRequestBody.ShouldContain("620756992||1001||Witaj");
        // The file part declares text/plain (the ||-format export), mirroring the reference upload.
        handler.LastRequestBody.ShouldContain("Content-Type: text/plain");
    }

    [Fact]
    public async Task ImportAsync_WhenMassRemovalAllowed_PassesTheOverrideFlagInTheQueryString()
    {
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(SummaryFixture(), ApiJsonOptions),
            out StubHttpMessageHandler handler);
        using MemoryStream file = new(Encoding.UTF8.GetBytes("x"));

        await loader.ImportAsync(new GameVersionId(GameVersionGuid), file, "exported.txt", allowMassRemoval: true);

        handler.LastRequest!.RequestUri!.Query.ShouldBe("?allowMassRemoval=true");
    }

    [Fact]
    public async Task ImportAsync_OnSuccess_DeserializesEveryCounterOfTheSummary()
    {
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(SummaryFixture(), ApiJsonOptions),
            out _);
        using MemoryStream file = new(Encoding.UTF8.GetBytes("x"));

        ApiResult<ImportSummary> result =
            await loader.ImportAsync(new GameVersionId(GameVersionGuid), file, "exported.txt", allowMassRemoval: false);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Added.ShouldBe(3);
        result.Value.SourceChanged.ShouldBe(2);
        result.Value.Invalidated.ShouldBe(1);
        result.Value.Removed.ShouldBe(4);
        result.Value.Unchanged.ShouldBe(10);
        result.Value.Warnings.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ImportAsync_WhenApiRejectsMassRemoval_ReturnsFailureWithProblemDetails()
    {
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.UnprocessableEntity,
            """{ "title": "Zbyt wiele usunięć", "status": 422 }""",
            out _);
        using MemoryStream file = new(Encoding.UTF8.GetBytes("x"));

        ApiResult<ImportSummary> result =
            await loader.ImportAsync(new GameVersionId(GameVersionGuid), file, "exported.txt", allowMassRemoval: false);

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(422);
    }

    [Fact]
    public async Task DownloadTranslationFileAsync_RequestsThePolishTranslationFileEndpoint()
    {
        ImportExportLoader loader = CreateLoader(HttpStatusCode.OK, "file body", out StubHttpMessageHandler handler);

        await loader.DownloadTranslationFileAsync();

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.ToString().ShouldBe($"{BaseUrl}api/v1/translation-files/pl");
    }

    [Fact]
    public async Task DownloadTranslationFileAsync_OnSuccess_ReturnsTheRawBodyVerbatim()
    {
        const string body = "# polish.txt\n620756992||1001||Witaj w Śródziemiu!||NULL||NULL||1";
        ImportExportLoader loader = CreateLoader(HttpStatusCode.OK, body, out _);

        ApiResult<string> result = await loader.DownloadTranslationFileAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(body);
    }

    [Fact]
    public async Task DownloadTranslationFileAsync_WhenNoArtifactExists_ReturnsFailureWithProblemDetails()
    {
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.NotFound,
            """{ "title": "Brak pliku tłumaczenia", "status": 404 }""",
            out _);

        ApiResult<string> result = await loader.DownloadTranslationFileAsync();

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(404);
    }

    private static GameVersionResponse VersionFixture() =>
        new(new GameVersionId(GameVersionGuid), "48.0", DateTimeOffset.UnixEpoch, GameVersionStatus.Unprocessed);

    private static ImportSummary SummaryFixture() =>
        new(Added: 3, SourceChanged: 2, Invalidated: 1, Removed: 4, Unchanged: 10, Warnings: ["1 wiersz przywrócony."]);

    private static ImportExportLoader CreateLoader(
        HttpStatusCode statusCode,
        string body,
        out StubHttpMessageHandler handler)
    {
        handler = StubHttpMessageHandler.RespondWith(statusCode, body);
        return new ImportExportLoader(CreateClient(handler));
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
