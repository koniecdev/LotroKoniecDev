using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.Frontend.Components.Pages.Translations;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Translations;

public sealed class TranslationListLoaderTests
{
    private const string BaseUrl = "https://localhost:5002/";

    private static readonly string ResolvedTranslationsUri =
        BaseUrl.TrimEnd('/') + StubDiscoveryCache.HrefFor(Rels.Translations);

    // The same JSON options the Frontend's HTTP layer uses (HttpClientApiExtensions), so the stub
    // body deserializes through the exact same contract the loader relies on.
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task LoadAsync_WhenApiReturnsAPage_DeserializesEveryColumnTheTableRenders()
    {
        TranslationListItemResponse item = new(
            TranslationId.Create(Guid.Parse("0192a000-0000-7000-8000-000000000001")),
            FileId: 620756992,
            GossipId: 1001,
            SourceText: "Welcome to Middle-earth!",
            TranslatedText: "Witaj w Śródziemiu!",
            Status: TranslationStatus.Approved,
            Submitter: new TranslatorSummaryResponse(TranslatorId.Create(), "Frodo"),
            UpdatedAt: DateTimeOffset.UtcNow);
        TranslationListLoader loader = CreateLoader(PageOf([item], page: 1, pageSize: 50, totalCount: 1), out _);

        ApiResult<PaginationResponse<TranslationListItemResponse>> result =
            await loader.LoadAsync(TranslationListQuery.From(null, null, 1));

        result.IsSuccess.ShouldBeTrue();
        TranslationListItemResponse loaded = result.Value.Items.ShouldHaveSingleItem();
        loaded.FileId.ShouldBe(620756992);
        loaded.GossipId.ShouldBe(1001);
        loaded.SourceText.ShouldBe("Welcome to Middle-earth!");
        loaded.TranslatedText.ShouldBe("Witaj w Śródziemiu!");
        loaded.Status.ShouldBe(TranslationStatus.Approved);
        loaded.Id.Value.ShouldBe(Guid.Parse("0192a000-0000-7000-8000-000000000001"));
    }

    [Fact]
    public async Task LoadAsync_WithNoFilters_QueriesTheAdvertisedTranslationsHrefWithLangAndDefaultPaging()
    {
        TranslationListLoader loader = CreateLoader(EmptyPage(), out StubHttpMessageHandler handler);

        await loader.LoadAsync(TranslationListQuery.From(null, null, 1));

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        string requestUri = handler.LastRequest.RequestUri!.ToString();
        requestUri.ShouldStartWith($"{ResolvedTranslationsUri}?");
        requestUri.ShouldContain("lang=pl");
        requestUri.ShouldContain("page=1");
        requestUri.ShouldContain($"pageSize={TranslationListQuery.DefaultPageSize}");
    }

    [Fact]
    public async Task LoadAsync_WithSearchTerm_ForwardsTheSearchQueryParameter()
    {
        TranslationListLoader loader = CreateLoader(EmptyPage(), out StubHttpMessageHandler handler);

        await loader.LoadAsync(TranslationListQuery.From(search: "Gandalf", status: null, page: 1));

        handler.LastRequest!.RequestUri!.ToString().ShouldContain("search=Gandalf");
    }

    [Fact]
    public async Task LoadAsync_WithStatusFilter_ForwardsTheStatusQueryParameter()
    {
        TranslationListLoader loader = CreateLoader(EmptyPage(), out StubHttpMessageHandler handler);

        await loader.LoadAsync(TranslationListQuery.From(search: null, status: "NeedsReview", page: 1));

        handler.LastRequest!.RequestUri!.ToString().ShouldContain("status=NeedsReview");
    }

    [Fact]
    public async Task LoadAsync_OnAFurtherPage_ForwardsThePageNumber()
    {
        TranslationListLoader loader = CreateLoader(EmptyPage(), out StubHttpMessageHandler handler);

        await loader.LoadAsync(TranslationListQuery.From(search: null, status: null, page: 4));

        handler.LastRequest!.RequestUri!.ToString().ShouldContain("page=4");
    }

    [Fact]
    public async Task LoadAsync_PastTheLastPage_SucceedsWithNoItemsSoThePageShowsTheEmptyState()
    {
        // The API echoes the requested page with an empty Items collection when the caller overshoots;
        // the page must degrade to "Brak wyników" rather than treat it as a failure.
        TranslationListLoader loader = CreateLoader(
            PageOf([], page: 99, pageSize: 50, totalCount: 120),
            out StubHttpMessageHandler handler);

        ApiResult<PaginationResponse<TranslationListItemResponse>> result =
            await loader.LoadAsync(TranslationListQuery.From(search: null, status: null, page: 99));

        result.IsSuccess.ShouldBeTrue();
        result.Value.Items.ShouldBeEmpty();
        result.Value.Page.ShouldBe(99);
        handler.LastRequest!.RequestUri!.ToString().ShouldContain("page=99");
    }

    [Fact]
    public async Task LoadAsync_WhenApiFails_ReturnsFailureWithProblemDetails()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.BadRequest,
            """{ "title": "Nieobsługiwany język", "status": 400 }""");
        TranslationListLoader loader = new(StubDiscoveryCache.AdvertisingGet(Rels.Translations), CreateClient(handler));

        ApiResult<PaginationResponse<TranslationListItemResponse>> result =
            await loader.LoadAsync(TranslationListQuery.From(null, null, 1));

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails.ShouldNotBeNull();
        result.ProblemDetails!.Status.ShouldBe(400);
    }

    [Fact]
    public async Task LoadAsync_WhenTheTranslationsRelIsNotAdvertised_FailsWithoutCallingTheApi()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "{}");
        TranslationListLoader loader = new(StubDiscoveryCache.AdvertisingGet(Rels.Progress), CreateClient(handler));

        ApiResult<PaginationResponse<TranslationListItemResponse>> result =
            await loader.LoadAsync(TranslationListQuery.From(null, null, 1));

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(403);
        handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task LoadAsync_WhenDiscoveryIsUnavailable_PassesThatProblemThrough()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "{}");
        TranslationListLoader loader = new(StubDiscoveryCache.Unavailable(), CreateClient(handler));

        ApiResult<PaginationResponse<TranslationListItemResponse>> result =
            await loader.LoadAsync(TranslationListQuery.From(null, null, 1));

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(503);
        handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task BulkApproveAsync_PostsTheSelectedIdsToTheHref_AndReturnsTheSummary()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new BulkApproveTranslationsResponse(2, 2, 0), ApiJsonOptions));
        TranslationListLoader loader = new(StubDiscoveryCache.AdvertisingGet(Rels.Translations), CreateClient(handler));

        ApiResult<BulkApproveTranslationsResponse> result =
            await loader.BulkApproveAsync("/api/v1/translations/approve", [first, second]);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Approved.ShouldBe(2);
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().ShouldEndWith("/api/v1/translations/approve");
        handler.LastRequestBody.ShouldNotBeNull();
        handler.LastRequestBody!.ShouldContain(first.ToString());
        handler.LastRequestBody.ShouldContain(second.ToString());
    }

    [Fact]
    public async Task BulkApproveAsync_WhenApiRejectsTheBatch_ReturnsFailureWithProblemDetails()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.BadRequest,
            """{ "title": "Za dużo pozycji", "status": 400 }""");
        TranslationListLoader loader = new(StubDiscoveryCache.AdvertisingGet(Rels.Translations), CreateClient(handler));

        ApiResult<BulkApproveTranslationsResponse> result =
            await loader.BulkApproveAsync("/api/v1/translations/approve", [Guid.NewGuid()]);

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails.ShouldNotBeNull();
        result.ProblemDetails!.Status.ShouldBe(400);
    }

    private static PaginationResponse<TranslationListItemResponse> EmptyPage() =>
        PageOf([], page: 1, pageSize: 50, totalCount: 0);

    private static PaginationResponse<TranslationListItemResponse> PageOf(
        IReadOnlyCollection<TranslationListItemResponse> items,
        int page,
        int pageSize,
        int totalCount) =>
        new()
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };

    private static TranslationListLoader CreateLoader(
        PaginationResponse<TranslationListItemResponse> page,
        out StubHttpMessageHandler handler)
    {
        handler = StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(page, ApiJsonOptions));
        return new TranslationListLoader(StubDiscoveryCache.AdvertisingGet(Rels.Translations), CreateClient(handler));
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
