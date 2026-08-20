using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.Frontend.Components.Pages.ImportExport;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.ImportExport;

public sealed class ImportExportLoaderTests
{
    private const string BaseUrl = "https://localhost:5002/";

    /// <summary>
    /// The per-version <c>import</c> href the API sends. It looks nothing like the real route on purpose,
    /// so a passing assertion proves the loader followed the link it was given (#610).
    /// </summary>
    private const string AdvertisedImportHref = "/advertised/import-into/42";

    private static readonly Guid GameVersionGuid = Guid.Parse("0192a000-0000-7000-8000-000000000099");
    private static readonly string ResolvedGameVersionsUri =
        BaseUrl.TrimEnd('/') + StubDiscoveryCache.HrefFor(Rels.GameVersions);

    // Mirrors the JSON options the Frontend's HTTP seam uses (HttpClientApiExtensions) so the stub
    // body deserializes through the exact same contract the loader relies on.
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task ListGameVersionsAsync_WhenTheGameVersionsRelIsAdvertised_GetsThatHref()
    {
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(
                new CollectionResponse<GameVersionResponse> { Items = [VersionFixture()] }, ApiJsonOptions),
            out StubHttpMessageHandler handler);

        await loader.ListGameVersionsAsync();

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.ToString().ShouldBe(ResolvedGameVersionsUri);
    }

    [Fact]
    public async Task ListGameVersionsAsync_WhenApiReturnsVersions_DeserializesEachOne()
    {
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(
                new CollectionResponse<GameVersionResponse> { Items = [VersionFixture()] }, ApiJsonOptions),
            out _);

        ApiResult<CollectionResponse<GameVersionResponse>> result = await loader.ListGameVersionsAsync();

        result.IsSuccess.ShouldBeTrue();
        GameVersionResponse version = result.Value.Items.ShouldHaveSingleItem();
        version.Id.Value.ShouldBe(GameVersionGuid);
        version.Version.ShouldBe("48.0");
        version.Status.ShouldBe(GameVersionStatus.Unprocessed);
    }

    [Fact]
    public async Task ListGameVersionsAsync_PreservesTheCollectionLinksSoTheRegisterRelCanGateImport()
    {
        // The collection's admin-only `register` rel has to survive deserialization, because the
        // import/export page shows the import panel only when it is there (#158) instead of checking the
        // role itself.
        CollectionResponse<GameVersionResponse> collection = new()
        {
            Items = [VersionFixture()],
            Links = [new LinkDto("https://tms.example/api/v1/game-versions", Rels.Register, "POST")]
        };
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(collection, ApiJsonOptions),
            out _);

        ApiResult<CollectionResponse<GameVersionResponse>> result = await loader.ListGameVersionsAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value.Links.ShouldContain(link => link.Rel == Rels.Register);
    }

    [Fact]
    public async Task ListGameVersionsAsync_WhenApiFails_ReturnsFailureWithProblemDetails()
    {
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.InternalServerError,
            """{ "title": "Nie udało się wczytać wersji.", "status": 500 }""",
            out _);

        ApiResult<CollectionResponse<GameVersionResponse>> result = await loader.ListGameVersionsAsync();

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(500);
    }

    [Fact]
    public async Task ListGameVersionsAsync_WhenTheGameVersionsRelIsNotAdvertised_FailsWithoutCallingTheApi()
    {
        // `game-versions` is RequireTranslatorRole while /import-export is only [Authorize], so an
        // authenticated non-translator lands here on a normal navigation (#610).
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "{}");
        ImportExportLoader loader = new(StubDiscoveryCache.AdvertisingGet(Rels.Progress), CreateClient(handler));

        ApiResult<CollectionResponse<GameVersionResponse>> result = await loader.ListGameVersionsAsync();

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(403);
        handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task ListGameVersionsAsync_WhenDiscoveryIsUnavailable_PassesThatProblemThrough()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "{}");
        ImportExportLoader loader = new(StubDiscoveryCache.Unavailable(), CreateClient(handler));

        ApiResult<CollectionResponse<GameVersionResponse>> result = await loader.ListGameVersionsAsync();

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(503);
        handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task DownloadTranslationFileAsync_WhenTheTranslationFileRelIsNotAdvertised_FailsWithoutCallingTheApi()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "body");
        ImportExportLoader loader = new(StubDiscoveryCache.AdvertisingGet(Rels.Progress), CreateClient(handler));

        ApiResult<string> result = await loader.DownloadTranslationFileAsync();

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(403);
        handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task DownloadTranslationFileAsync_WhenDiscoveryIsUnavailable_PassesThatProblemThrough()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "body");
        ImportExportLoader loader = new(StubDiscoveryCache.Unavailable(), CreateClient(handler));

        ApiResult<string> result = await loader.DownloadTranslationFileAsync();

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(503);
        handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task ImportAsync_WhenGivenTheVersionsImportHref_PostsMultipartToIt()
    {
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(SummaryFixture(), ApiJsonOptions),
            out StubHttpMessageHandler handler);
        using MemoryStream file = new(Encoding.UTF8.GetBytes("620756992||1001||Witaj||NULL||NULL||1"));

        await loader.ImportAsync(AdvertisedImportHref, file, "exported.txt", allowMassRemoval: false);

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString()
            .ShouldBe($"{BaseUrl}{AdvertisedImportHref.TrimStart('/')}?allowMassRemoval=false");
    }

    [Fact]
    public async Task ImportAsync_SendsTheUploadedFileAsMultipartFormData()
    {
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(SummaryFixture(), ApiJsonOptions),
            out StubHttpMessageHandler handler);
        using MemoryStream file = new(Encoding.UTF8.GetBytes("620756992||1001||Witaj||NULL||NULL||1"));

        await loader.ImportAsync(AdvertisedImportHref, file, "exported.txt", allowMassRemoval: false);

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

        await loader.ImportAsync(AdvertisedImportHref, file, "exported.txt", allowMassRemoval: true);

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
            await loader.ImportAsync(AdvertisedImportHref, file, "exported.txt", allowMassRemoval: false);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Added.ShouldBe(3);
        result.Value.SourceChanged.ShouldBe(2);
        result.Value.Invalidated.ShouldBe(1);
        result.Value.Removed.ShouldBe(4);
        result.Value.Unchanged.ShouldBe(10);
        result.Value.Echoed.ShouldBe(7);
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
            await loader.ImportAsync(AdvertisedImportHref, file, "exported.txt", allowMassRemoval: false);

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(422);
    }

    [Fact]
    public async Task DownloadTranslationFileAsync_WhenTheTranslationFileRelIsAdvertised_GetsThatHref()
    {
        ImportExportLoader loader = CreateLoader(HttpStatusCode.OK, "file body", out StubHttpMessageHandler handler);

        await loader.DownloadTranslationFileAsync();

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.ToString()
            .ShouldBe(BaseUrl.TrimEnd('/') + StubDiscoveryCache.HrefFor(Rels.TranslationFile));
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
        new(GameVersionId.Create(GameVersionGuid), "48.0", DateTimeOffset.UnixEpoch, GameVersionStatus.Unprocessed);

    private static ImportSummary SummaryFixture() =>
        new(Added: 3, SourceChanged: 2, Invalidated: 1, Removed: 4, Unchanged: 10, Echoed: 7, Warnings: ["1 wiersz przywrócony."]);

    private static ImportExportLoader CreateLoader(
        HttpStatusCode statusCode,
        string body,
        out StubHttpMessageHandler handler)
    {
        handler = StubHttpMessageHandler.RespondWith(statusCode, body);
        return new ImportExportLoader(
            StubDiscoveryCache.AdvertisingGet(Rels.GameVersions, Rels.TranslationFile),
            CreateClient(handler));
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
