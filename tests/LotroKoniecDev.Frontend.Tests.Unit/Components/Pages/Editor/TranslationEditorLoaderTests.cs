using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.Frontend.Components.Pages.Editor;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Editor;

public sealed class TranslationEditorLoaderTests
{
    private const string BaseUrl = "https://localhost:5002/";

    // These server hrefs look unusual on purpose, on a different host and path than anything this app
    // would build, so the assertions prove the loader follows the link the server sent and not a route
    // written into the code (#158).
    private const string UpsertHref = "https://tms.example/hateoas/translations";
    private const string ApproveHref = "https://tms.example/hateoas/translations/abc/approve";

    private static readonly Guid TranslationGuid = Guid.Parse("0192a000-0000-7000-8000-000000000042");
    private static readonly string ResolvedTranslationsUri =
        BaseUrl.TrimEnd('/') + StubDiscoveryCache.HrefFor(Rels.Translations);

    // The same JSON options the Frontend's HTTP layer uses (HttpClientApiExtensions), so the stub
    // body deserializes through the exact same contract the loader relies on.
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task LoadAsync_WhenApiReturnsTheRow_DeserializesEveryFieldTheEditorRenders()
    {
        TranslationDetailResponse detail = DetailFixture();
        TranslationEditorLoader loader = CreateLoader(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(detail, ApiJsonOptions),
            out _);

        ApiResult<TranslationDetailResponse> result =
            await loader.LoadAsync(TranslationId.Create(TranslationGuid));

        result.IsSuccess.ShouldBeTrue();
        TranslationDetailResponse loaded = result.Value;
        loaded.Id.Value.ShouldBe(TranslationGuid);
        loaded.FileId.ShouldBe(620756992);
        loaded.GossipId.ShouldBe(1002);
        loaded.SourceText.ShouldBe("You have <--DO_NOT_TOUCH!--> gold");
        loaded.TranslatedText.ShouldBe("Masz <--DO_NOT_TOUCH!--> złota");
        loaded.PreviousSourceText.ShouldBe("You had <--DO_NOT_TOUCH!--> gold");
        loaded.Status.ShouldBe(TranslationStatus.NeedsReview);
        loaded.Submitter!.DisplayName.ShouldBe("Frodo");
    }

    [Fact]
    public async Task LoadAsync_WhenTheTranslationsRelIsAdvertised_AppendsTheIdToThatHref()
    {
        TranslationEditorLoader loader = CreateLoader(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(DetailFixture(), ApiJsonOptions),
            out StubHttpMessageHandler handler);

        await loader.LoadAsync(TranslationId.Create(TranslationGuid));

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.ToString()
            .ShouldBe($"{ResolvedTranslationsUri}/{TranslationGuid}");
    }

    [Fact]
    public async Task LoadAsync_WhenRowIsMissing_ReturnsFailureWithProblemDetails()
    {
        TranslationEditorLoader loader = CreateLoader(
            HttpStatusCode.NotFound,
            """{ "title": "Nie znaleziono tłumaczenia", "status": 404 }""",
            out _);

        ApiResult<TranslationDetailResponse> result =
            await loader.LoadAsync(TranslationId.Create(TranslationGuid));

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(404);
    }

    [Fact]
    public async Task SaveAsync_PutsTheRequestBodyToTheProvidedUpsertHref()
    {
        TranslationEditorLoader loader = CreateLoader(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(DetailFixture(), ApiJsonOptions),
            out StubHttpMessageHandler handler);
        UpsertTranslationRequest request = new(620756992, 1002, "Masz <--DO_NOT_TOUCH!--> złota");

        await loader.SaveAsync(UpsertHref, request);

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Put);
        // The request goes to the server's upsert href exactly, not to an /api/v1/translations path
        // built here.
        handler.LastRequest.RequestUri!.ToString().ShouldBe(UpsertHref);
        handler.LastRequestBody.ShouldNotBeNull();
        // Deserialize the sent JSON rather than substring-match it: System.Text.Json escapes '<', '>'
        // and non-ASCII, so the placeholder/diacritics only survive a round-trip, not a raw contains.
        UpsertTranslationRequest sent =
            JsonSerializer.Deserialize<UpsertTranslationRequest>(handler.LastRequestBody!, ApiJsonOptions)!;
        sent.FileId.ShouldBe(620756992);
        sent.GossipId.ShouldBe(1002);
        sent.TranslatedText.ShouldBe("Masz <--DO_NOT_TOUCH!--> złota");
    }

    [Fact]
    public async Task SaveAsync_OnSuccess_DeserializesTheUpdatedRow()
    {
        TranslationEditorLoader loader = CreateLoader(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(DetailFixture(), ApiJsonOptions),
            out _);

        ApiResult<TranslationDetailResponse> result =
            await loader.SaveAsync(UpsertHref, new UpsertTranslationRequest(620756992, 1002, "x"));

        result.IsSuccess.ShouldBeTrue();
        result.Value.Id.Value.ShouldBe(TranslationGuid);
    }

    [Fact]
    public async Task SaveAsync_WhenApiRejects_ReturnsFailureWithProblemDetails()
    {
        TranslationEditorLoader loader = CreateLoader(
            HttpStatusCode.UnprocessableEntity,
            """{ "title": "Tłumaczenie nie może być puste", "status": 422 }""",
            out _);

        ApiResult<TranslationDetailResponse> result =
            await loader.SaveAsync(UpsertHref, new UpsertTranslationRequest(620756992, 1002, ""));

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(422);
    }

    [Fact]
    public async Task ApproveAsync_PostsToTheProvidedApproveHref()
    {
        TranslationEditorLoader loader = CreateLoader(HttpStatusCode.NoContent, string.Empty, out StubHttpMessageHandler handler);

        ApiResult result = await loader.ApproveAsync(ApproveHref);

        result.IsSuccess.ShouldBeTrue();
        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        // The request goes to the server's approve href exactly, not to an {id}/approve path built
        // here.
        handler.LastRequest.RequestUri!.ToString().ShouldBe(ApproveHref);
    }

    [Fact]
    public async Task ApproveAsync_WhenForbidden_ReturnsFailureWithProblemDetails()
    {
        TranslationEditorLoader loader = CreateLoader(
            HttpStatusCode.Forbidden,
            """{ "title": "Brak uprawnień", "status": 403 }""",
            out _);

        ApiResult result = await loader.ApproveAsync(ApproveHref);

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(403);
    }

    private static TranslationDetailResponse DetailFixture() =>
        new(
            TranslationId.Create(TranslationGuid),
            FileId: 620756992,
            GossipId: 1002,
            SourceText: "You have <--DO_NOT_TOUCH!--> gold",
            ArgsOrder: "1",
            ArgsId: "1",
            TranslatedText: "Masz <--DO_NOT_TOUCH!--> złota",
            PreviousSourceText: "You had <--DO_NOT_TOUCH!--> gold",
            Submitter: new TranslatorSummaryResponse(TranslatorId.Create(), "Frodo"),
            Approver: null,
            Status: TranslationStatus.NeedsReview,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task LoadAsync_WhenTheTranslationsRelIsNotAdvertised_FailsWithoutCallingTheApi()
    {
        // `translations` is open to anyone today, but the editor route needs a login, and the loader must
        // not assume the rel is there. A missing rel means we do not call, not that we build a path
        // (#610).
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "{}");
        TranslationEditorLoader loader = new(StubDiscoveryCache.AdvertisingGet(Rels.Progress), CreateClient(handler));

        ApiResult<TranslationDetailResponse> result = await loader.LoadAsync(TranslationId.Create(TranslationGuid));

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(403);
        handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task LoadAsync_WhenDiscoveryIsUnavailable_PassesThatProblemThrough()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "{}");
        TranslationEditorLoader loader = new(StubDiscoveryCache.Unavailable(), CreateClient(handler));

        ApiResult<TranslationDetailResponse> result = await loader.LoadAsync(TranslationId.Create(TranslationGuid));

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(503);
        handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveCollectionUpsertHrefAsync_WhenTheUpsertRelIsAdvertised_ReturnsThatHref()
    {
        // The recovery path, a resubmit whose row could not be loaded again, has no row to read the
        // upsert rel from, so it reads the same rel from the service document and never uses a path
        // written into the code.
        TranslationEditorLoader loader = CreateLoader(HttpStatusCode.OK, "{}", out _);

        ApiResult<string> result = await loader.ResolveCollectionUpsertHrefAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(StubDiscoveryCache.HrefFor(Rels.Upsert));
    }

    [Fact]
    public async Task ResolveCollectionUpsertHrefAsync_WhenTheUpsertRelIsNotAdvertised_Fails()
    {
        // The API only sends `upsert` to a translator. A read-only caller who reaches the recovery path
        // gets a failure the page can show, not a PUT to a guessed URL.
        TranslationEditorLoader loader = new(
            StubDiscoveryCache.AdvertisingGet(Rels.Translations),
            CreateClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "{}")));

        ApiResult<string> result = await loader.ResolveCollectionUpsertHrefAsync();

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(403);
    }

    private static TranslationEditorLoader CreateLoader(
        HttpStatusCode statusCode,
        string jsonBody,
        out StubHttpMessageHandler handler)
    {
        handler = StubHttpMessageHandler.RespondWith(statusCode, jsonBody);
        return new TranslationEditorLoader(
            StubDiscoveryCache.AdvertisingGet(Rels.Translations, Rels.Upsert),
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
