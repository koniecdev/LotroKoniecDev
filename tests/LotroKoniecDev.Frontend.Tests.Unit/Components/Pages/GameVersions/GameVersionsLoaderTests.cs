using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.Frontend.Components.Pages.GameVersions;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.GameVersions;

public sealed class GameVersionsLoaderTests
{
    private const string BaseUrl = "https://localhost:5002/";

    /// <summary>The per-row / collection hrefs the API advertises — never composed by the loader (#610).</summary>
    private const string AdvertisedRegisterHref = "/advertised/register-game-version";

    private const string AdvertisedDeleteHref = "/advertised/delete-game-version/42";

    private static readonly Guid GameVersionGuid = Guid.Parse("0192a000-0000-7000-8000-0000000000aa");
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
        GameVersionsLoader loader = CreateLoader(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new CollectionResponse<GameVersionResponse> { Items = [VersionFixture()] }, ApiJsonOptions),
            out StubHttpMessageHandler handler);

        await loader.ListGameVersionsAsync();

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.ToString().ShouldBe(ResolvedGameVersionsUri);
    }

    [Fact]
    public async Task ListGameVersionsAsync_PreservesTheCollectionRegisterRelSoTheFormCanBeGated()
    {
        // The collection's admin-only `register` rel must survive deserialization — the page gates the
        // register form on its presence (#158) rather than recomputing the role locally.
        CollectionResponse<GameVersionResponse> collection = new()
        {
            Items = [VersionFixture()],
            Links = [new LinkDto("https://tms.example/api/v1/game-versions", Rels.Register, "POST")]
        };
        GameVersionsLoader loader = CreateLoader(HttpStatusCode.OK, JsonSerializer.Serialize(collection, ApiJsonOptions), out _);

        ApiResult<CollectionResponse<GameVersionResponse>> result = await loader.ListGameVersionsAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value.Links.ShouldContain(link => link.Rel == Rels.Register);
        result.Value.Items.ShouldHaveSingleItem().Version.ShouldBe("48");
    }

    [Fact]
    public async Task ListGameVersionsAsync_WhenApiFails_ReturnsFailureWithProblemDetails()
    {
        GameVersionsLoader loader = CreateLoader(
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
        // `game-versions` is RequireTranslatorRole while /game-versions is only [Authorize], so an
        // authenticated non-translator reaches this on a normal navigation — it must be a clear failure,
        // never a call to a guessed path (#610).
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "{}");
        GameVersionsLoader loader = new(StubDiscoveryCache.AdvertisingGet(Rels.Progress), CreateClient(handler));

        ApiResult<CollectionResponse<GameVersionResponse>> result = await loader.ListGameVersionsAsync();

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(403);
        handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task ListGameVersionsAsync_WhenDiscoveryIsUnavailable_PassesThatProblemThrough()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "{}");
        GameVersionsLoader loader = new(StubDiscoveryCache.Unavailable(), CreateClient(handler));

        ApiResult<CollectionResponse<GameVersionResponse>> result = await loader.ListGameVersionsAsync();

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(503);
        handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task RegisterGameVersionAsync_WhenGivenTheCollectionsRegisterHref_PostsTheVersionToIt()
    {
        GameVersionsLoader loader = CreateLoader(
            HttpStatusCode.Created,
            JsonSerializer.Serialize(VersionFixture(), ApiJsonOptions),
            out StubHttpMessageHandler handler);

        ApiResult<GameVersionResponse> result = await loader.RegisterGameVersionAsync(AdvertisedRegisterHref, "48.0");

        result.IsSuccess.ShouldBeTrue();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().ShouldBe($"{BaseUrl}{AdvertisedRegisterHref.TrimStart('/')}");
        handler.LastRequestBody.ShouldNotBeNull();
        handler.LastRequestBody!.ShouldContain("48.0");
    }

    [Fact]
    public async Task RegisterGameVersionAsync_WhenDuplicate_ReturnsFailureWithProblemDetails()
    {
        GameVersionsLoader loader = CreateLoader(
            HttpStatusCode.UnprocessableEntity,
            """{ "title": "Data Conflict", "status": 422 }""",
            out _);

        ApiResult<GameVersionResponse> result = await loader.RegisterGameVersionAsync(AdvertisedRegisterHref, "48.0");

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(422);
    }

    [Fact]
    public async Task DeleteGameVersionAsync_WhenGivenTheRowsDeleteHref_SendsDeleteToIt()
    {
        GameVersionsLoader loader = CreateLoader(HttpStatusCode.NoContent, string.Empty, out StubHttpMessageHandler handler);

        ApiResult result = await loader.DeleteGameVersionAsync(AdvertisedDeleteHref);

        result.IsSuccess.ShouldBeTrue();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Delete);
        handler.LastRequest.RequestUri!.ToString().ShouldBe($"{BaseUrl}{AdvertisedDeleteHref.TrimStart('/')}");
    }

    [Fact]
    public async Task DeleteGameVersionAsync_WhenRefused_ReturnsFailureWithProblemDetails()
    {
        GameVersionsLoader loader = CreateLoader(
            HttpStatusCode.UnprocessableEntity,
            """{ "title": "Data Conflict", "status": 422 }""",
            out _);

        ApiResult result = await loader.DeleteGameVersionAsync(AdvertisedDeleteHref);

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(422);
    }

    private static GameVersionResponse VersionFixture() =>
        new(GameVersionId.Create(GameVersionGuid), "48", DateTimeOffset.UnixEpoch, GameVersionStatus.Unprocessed);

    private static GameVersionsLoader CreateLoader(HttpStatusCode statusCode, string body, out StubHttpMessageHandler handler)
    {
        handler = StubHttpMessageHandler.RespondWith(statusCode, body);
        return new GameVersionsLoader(StubDiscoveryCache.AdvertisingGet(Rels.GameVersions), CreateClient(handler));
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
