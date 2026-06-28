using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.Frontend.Components.Pages.GameVersions;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
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
    private static readonly Guid GameVersionGuid = Guid.Parse("0192a000-0000-7000-8000-0000000000aa");

    // Mirrors the JSON options the Frontend's HTTP seam uses (HttpClientApiExtensions) so the stub
    // body deserializes through the exact same contract the loader relies on.
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task ListGameVersionsAsync_RequestsTheGameVersionsCollectionEndpoint()
    {
        GameVersionsLoader loader = CreateLoader(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new CollectionResponse<GameVersionResponse> { Items = [VersionFixture()] }, ApiJsonOptions),
            out StubHttpMessageHandler handler);

        await loader.ListGameVersionsAsync();

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.ToString().ShouldBe($"{BaseUrl}api/v1/game-versions");
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
    public async Task RegisterGameVersionAsync_PostsTheVersionToTheCollectionEndpoint()
    {
        GameVersionsLoader loader = CreateLoader(
            HttpStatusCode.Created,
            JsonSerializer.Serialize(VersionFixture(), ApiJsonOptions),
            out StubHttpMessageHandler handler);

        ApiResult<GameVersionResponse> result = await loader.RegisterGameVersionAsync("48.0");

        result.IsSuccess.ShouldBeTrue();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().ShouldBe($"{BaseUrl}api/v1/game-versions");
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

        ApiResult<GameVersionResponse> result = await loader.RegisterGameVersionAsync("48.0");

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(422);
    }

    [Fact]
    public async Task DeleteGameVersionAsync_SendsDeleteToTheVersionScopedEndpoint()
    {
        GameVersionsLoader loader = CreateLoader(HttpStatusCode.NoContent, string.Empty, out StubHttpMessageHandler handler);

        ApiResult result = await loader.DeleteGameVersionAsync(GameVersionId.Create(GameVersionGuid));

        result.IsSuccess.ShouldBeTrue();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Delete);
        handler.LastRequest.RequestUri!.ToString().ShouldBe($"{BaseUrl}api/v1/game-versions/{GameVersionGuid}");
    }

    [Fact]
    public async Task DeleteGameVersionAsync_WhenRefused_ReturnsFailureWithProblemDetails()
    {
        GameVersionsLoader loader = CreateLoader(
            HttpStatusCode.UnprocessableEntity,
            """{ "title": "Data Conflict", "status": 422 }""",
            out _);

        ApiResult result = await loader.DeleteGameVersionAsync(GameVersionId.Create(GameVersionGuid));

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(422);
    }

    private static GameVersionResponse VersionFixture() =>
        new(GameVersionId.Create(GameVersionGuid), "48", DateTimeOffset.UnixEpoch, GameVersionStatus.Unprocessed);

    private static GameVersionsLoader CreateLoader(HttpStatusCode statusCode, string body, out StubHttpMessageHandler handler)
    {
        handler = StubHttpMessageHandler.RespondWith(statusCode, body);
        return new GameVersionsLoader(CreateClient(handler));
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
