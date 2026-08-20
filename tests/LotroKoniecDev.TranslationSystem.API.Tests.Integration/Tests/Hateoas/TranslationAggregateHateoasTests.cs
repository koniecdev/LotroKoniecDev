using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Hateoas;

/// <summary>
/// Checks that a translation's links depend both on the caller's role, so only reviewers see
/// <c>approve</c>, and on the row's state, so actions that lead nowhere are never offered. Those are
/// approving a row that is untranslated or already approved, and editing a removed row.
/// It also checks that the pagination links keep the filters that are applied. A plain
/// <c>application/json</c> request must carry no links at all.
/// </summary>
[Collection("TranslationApi")]
public sealed class TranslationAggregateHateoasTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;
    private TranslatorId _seederId;

    public TranslationAggregateHateoasTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"Translators\" CASCADE;");

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create("48.0").Value, Now).Value;
        dbContext.GameVersions.Add(gameVersion);

        Translator seeder = Translator.Create(
            IdentityId.Create(), DisplayName.Create("Seed Author").Value, email: null, Now).Value;
        dbContext.Translators.Add(seeder);

        await dbContext.SaveChangesAsync();
        _versionId = gameVersion.Id;
        _seederId = seeder.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(TranslationStatus.Draft)]
    [InlineData(TranslationStatus.NeedsReview)]
    public async Task GetTranslation_AsAdmin_WithPendingPolish_ReturnsSelfUpsertApprove(TranslationStatus status)
    {
        // Arrange: both Draft and NeedsReview carry Polish awaiting review, so a reviewer can approve.
        Translation row = await SeedAsync(Row(1, "Aragorn", status));

        // Act
        TranslationDetailResponse response = await GetHateoasAsync<TranslationDetailResponse>(
            AdminClient(), $"/api/v1/translations/{row.Id.Value}");

        // Assert
        response.Links.Count.ShouldBe(3);
        response.Links.ShouldContain(l => l.Rel == Rels.Self && l.Method == "GET");
        response.Links.ShouldContain(l => l.Rel == Rels.Upsert && l.Method == "PUT");
        response.Links.ShouldContain(l => l.Rel == Rels.Approve && l.Method == "POST");

        LinkDto selfLink = response.Links.First(l => l.Rel == Rels.Self);
        selfLink.Href.ShouldContain($"/api/v1/translations/{row.Id.Value}");
        Uri.TryCreate(selfLink.Href, UriKind.Absolute, out _).ShouldBeTrue();

        LinkDto approveLink = response.Links.First(l => l.Rel == Rels.Approve);
        approveLink.Href.ShouldContain($"/api/v1/translations/{row.Id.Value}/approve");
    }

    [Fact]
    public async Task GetTranslation_AsTranslator_DraftRow_ReturnsSelfAndUpsert_ButNotApprove()
    {
        // Arrange: approve is reviewer-only, so a translator never sees it.
        Translation row = await SeedAsync(Row(1, "Aragorn", TranslationStatus.Draft));

        // Act
        TranslationDetailResponse response = await GetHateoasAsync<TranslationDetailResponse>(
            TranslatorClient(), $"/api/v1/translations/{row.Id.Value}");

        // Assert
        response.Links.Count.ShouldBe(2);
        response.Links.ShouldContain(l => l.Rel == Rels.Self && l.Method == "GET");
        response.Links.ShouldContain(l => l.Rel == Rels.Upsert && l.Method == "PUT");
        response.Links.ShouldNotContain(l => l.Rel == Rels.Approve);
    }

    [Theory]
    [InlineData(TranslationStatus.Untranslated)]
    [InlineData(TranslationStatus.Approved)]
    public async Task GetTranslation_AsAdmin_WithoutPendingPolish_DoesNotAdvertiseApprove(TranslationStatus status)
    {
        // Arrange: nothing to approve on an untranslated row; re-approving an approved row is a dead end.
        Translation row = await SeedAsync(Row(1, "Aragorn", status));

        // Act
        TranslationDetailResponse response = await GetHateoasAsync<TranslationDetailResponse>(
            AdminClient(), $"/api/v1/translations/{row.Id.Value}");

        // Assert
        response.Links.ShouldContain(l => l.Rel == Rels.Self);
        response.Links.ShouldContain(l => l.Rel == Rels.Upsert);
        response.Links.ShouldNotContain(l => l.Rel == Rels.Approve);
    }

    [Fact]
    public async Task GetTranslation_AsAdmin_RemovedRow_ReturnsSelfOnly()
    {
        // Arrange: a soft-removed row is cut from translation work: no edit/approve transitions.
        Translation row = await SeedAsync(Row(1, "Aragorn", TranslationStatus.Draft, removed: true));

        // Act
        TranslationDetailResponse response = await GetHateoasAsync<TranslationDetailResponse>(
            AdminClient(), $"/api/v1/translations/{row.Id.Value}");

        // Assert
        response.Links.Count.ShouldBe(1);
        response.Links.ShouldContain(l => l.Rel == Rels.Self && l.Method == "GET");
        response.Links.ShouldNotContain(l => l.Rel == Rels.Upsert);
        response.Links.ShouldNotContain(l => l.Rel == Rels.Approve);
    }

    [Fact]
    public async Task GetTranslation_PlainJson_OmitsLinks()
    {
        // Arrange
        Translation row = await SeedAsync(Row(1, "Aragorn", TranslationStatus.Draft));
        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/translations/{row.Id.Value}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.Json));

        // Act
        HttpResponseMessage httpResponse = await AdminClient().SendAsync(request);
        TranslationDetailResponse response =
            (await httpResponse.Content.ReadFromJsonAsync<TranslationDetailResponse>(JsonOptions))!;

        // Assert
        httpResponse.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.Json);
        response.Links.Count.ShouldBe(0, "plain JSON responses must not carry hypermedia links");
    }

    [Fact]
    public async Task ListTranslations_AsAdmin_ReturnsPerItemAndPaginationLinks()
    {
        // Arrange
        Translation row = await SeedAsync(Row(1, "Aragorn", TranslationStatus.Draft));

        // Act
        PaginationResponse<TranslationListItemResponse> response =
            await GetHateoasAsync<PaginationResponse<TranslationListItemResponse>>(
                AdminClient(), "/api/v1/translations?page=1&pageSize=50");

        // Assert: per-item links (Draft + admin → self, upsert, approve)
        TranslationListItemResponse item = response.Items.First(i => i.Id == row.Id);
        item.Links.Count.ShouldBe(3);
        item.Links.ShouldContain(l => l.Rel == Rels.Self && l.Method == "GET");
        item.Links.ShouldContain(l => l.Rel == Rels.Upsert && l.Method == "PUT");
        item.Links.ShouldContain(l => l.Rel == Rels.Approve && l.Method == "POST");

        // Assert: pagination links on the envelope. One row fits on one page, so every boundary rel
        // would point at the page we are already on and none is advertised (#545).
        response.Links.ShouldContain(l => l.Rel == Rels.Self && l.Method == "GET");
        response.Links.ShouldNotContain(l => l.Rel == Rels.FirstPage);
        response.Links.ShouldNotContain(l => l.Rel == Rels.LastPage);

        // Assert: the admin-only bulk-approve collection affordance (#322) drives the FE checkbox toolbar.
        response.Links.ShouldContain(l => l.Rel == Rels.BulkApprove && l.Method == "POST");
    }

    [Fact]
    public async Task ListTranslations_Anonymously_ItemsCarryNoLinks_ButPagerLinksRemain()
    {
        // Arrange: the public read-only list (#309): items must not advertise transitions an
        // anonymous visitor cannot take (self targets the translator-only detail GET), while the
        // envelope keeps its pagination navigation, which is plain browsing. Two rows one per page,
        // so the pager links this test is about actually exist.
        Translation row = await SeedAsync(Row(1, "Aragorn", TranslationStatus.Draft), Row(2, "Boromir"));

        // Act
        PaginationResponse<TranslationListItemResponse> response =
            await GetHateoasAsync<PaginationResponse<TranslationListItemResponse>>(
                _factory.CreateClient(), "/api/v1/translations?page=1&pageSize=1");

        // Assert
        response.Items.First(i => i.Id == row.Id).Links.ShouldBeEmpty();
        response.Links.ShouldContain(l => l.Rel == Rels.Self && l.Method == "GET");
        response.Links.ShouldContain(l => l.Rel == Rels.NextPage);
        response.Links.ShouldContain(l => l.Rel == Rels.LastPage);
        // On page 1 the backward jumps lead nowhere, so the pager renders them disabled (#545).
        response.Links.ShouldNotContain(l => l.Rel == Rels.FirstPage);
        response.Links.ShouldNotContain(l => l.Rel == Rels.PreviousPage);
        // The bulk-approve affordance is reviewer-only, so the anonymous envelope must not carry it.
        response.Links.ShouldNotContain(l => l.Rel == Rels.BulkApprove);
    }

    [Fact]
    public async Task ListTranslations_AsTranslator_ItemsDoNotAdvertiseApprove()
    {
        // Arrange
        Translation row = await SeedAsync(Row(1, "Aragorn", TranslationStatus.Draft));

        // Act
        PaginationResponse<TranslationListItemResponse> response =
            await GetHateoasAsync<PaginationResponse<TranslationListItemResponse>>(
                TranslatorClient(), "/api/v1/translations");

        // Assert
        TranslationListItemResponse item = response.Items.First(i => i.Id == row.Id);
        item.Links.ShouldContain(l => l.Rel == Rels.Self);
        item.Links.ShouldContain(l => l.Rel == Rels.Upsert);
        item.Links.ShouldNotContain(l => l.Rel == Rels.Approve);
        // Bulk approve is reviewer-only: a translator's envelope must not advertise it either.
        response.Links.ShouldNotContain(l => l.Rel == Rels.BulkApprove);
    }

    [Fact]
    public async Task ListTranslations_OnMiddlePage_ReturnsPreviousAndNextLinks()
    {
        // Arrange: three rows, one per page, so page 2 is a middle page.
        await SeedAsync(Row(1, "a"), Row(2, "b"), Row(3, "c"));

        // Act
        PaginationResponse<TranslationListItemResponse> response =
            await GetHateoasAsync<PaginationResponse<TranslationListItemResponse>>(
                AdminClient(), "/api/v1/translations?page=2&pageSize=1");

        // Assert
        response.Page.ShouldBe(2);
        response.Links.ShouldContain(l => l.Rel == Rels.Self);
        response.Links.ShouldContain(l => l.Rel == Rels.FirstPage);
        response.Links.ShouldContain(l => l.Rel == Rels.LastPage);
        response.Links.ShouldContain(l => l.Rel == Rels.PreviousPage);
        response.Links.ShouldContain(l => l.Rel == Rels.NextPage);
    }

    [Fact]
    public async Task ListTranslations_OnTheLastPage_OmitsTheForwardJumps()
    {
        // Arrange: three rows, one per page, so page 3 is the last page.
        await SeedAsync(Row(1, "a"), Row(2, "b"), Row(3, "c"));

        // Act
        PaginationResponse<TranslationListItemResponse> response =
            await GetHateoasAsync<PaginationResponse<TranslationListItemResponse>>(
                AdminClient(), "/api/v1/translations?page=3&pageSize=1");

        // Assert: both forward controls lead nowhere here and the pager renders them disabled (#545).
        response.Page.ShouldBe(3);
        response.Links.ShouldNotContain(l => l.Rel == Rels.LastPage);
        response.Links.ShouldNotContain(l => l.Rel == Rels.NextPage);
        response.Links.ShouldContain(l => l.Rel == Rels.FirstPage);
        response.Links.ShouldContain(l => l.Rel == Rels.PreviousPage);
    }

    [Fact]
    public async Task ListTranslations_PastTheLastPage_StillOffersBothAbsoluteJumpsBack()
    {
        // Arrange: page is clamped at the lower bound only, so an over-range page is reachable and
        // must not become a dead end: next is genuinely gone, but first/last are absolute jumps.
        await SeedAsync(Row(1, "a"), Row(2, "b"), Row(3, "c"));

        // Act
        PaginationResponse<TranslationListItemResponse> response =
            await GetHateoasAsync<PaginationResponse<TranslationListItemResponse>>(
                AdminClient(), "/api/v1/translations?page=99&pageSize=1");

        // Assert
        response.Items.ShouldBeEmpty();
        response.Links.ShouldContain(l => l.Rel == Rels.FirstPage);
        response.Links.ShouldContain(l => l.Rel == Rels.LastPage);
        response.Links.ShouldNotContain(l => l.Rel == Rels.NextPage);
    }

    [Fact]
    public async Task ListTranslations_PaginationLinks_PreserveTheSearchFilter()
    {
        // Arrange
        await SeedAsync(Row(1, "Frodo Baggins"), Row(2, "Samwise Gamgee"));

        // Act
        PaginationResponse<TranslationListItemResponse> response =
            await GetHateoasAsync<PaginationResponse<TranslationListItemResponse>>(
                AdminClient(), "/api/v1/translations?search=Frodo");

        // Assert: every page link carries the active filter forward.
        LinkDto selfLink = response.Links.First(l => l.Rel == Rels.Self);
        selfLink.Href.ShouldContain("search=Frodo", Case.Insensitive);
    }

    [Fact]
    public async Task ListTranslations_PaginationLinks_PreserveTheFilterOnEveryPageLink()
    {
        // Arrange: three matching rows, one per page, so page 2 carries the full nav set
        // (first/last/previous/next). The filter must ride along on every one of them, not just self.
        await SeedAsync(
            Row(1, "Gandalf the Grey"),
            Row(2, "Gandalf the White"),
            Row(3, "Gandalf Stormcrow"));

        // Act
        PaginationResponse<TranslationListItemResponse> response =
            await GetHateoasAsync<PaginationResponse<TranslationListItemResponse>>(
                AdminClient(), "/api/v1/translations?search=Gandalf&page=2&pageSize=1");

        // Assert: the filter rides every page link, not only self.
        response.Page.ShouldBe(2);
        response.Links.First(l => l.Rel == Rels.Self).Href.ShouldContain("search=Gandalf", Case.Insensitive);
        response.Links.First(l => l.Rel == Rels.FirstPage).Href.ShouldContain("search=Gandalf", Case.Insensitive);
        response.Links.First(l => l.Rel == Rels.LastPage).Href.ShouldContain("search=Gandalf", Case.Insensitive);
        response.Links.First(l => l.Rel == Rels.PreviousPage).Href.ShouldContain("search=Gandalf", Case.Insensitive);
        response.Links.First(l => l.Rel == Rels.NextPage).Href.ShouldContain("search=Gandalf", Case.Insensitive);
    }

    [Fact]
    public async Task ListTranslations_PaginationLinks_PreserveTheActiveSort()
    {
        // Arrange: three rows, one per page, so page 2 carries the full nav set; the active sort
        // must ride along on every page link, exactly as the filters do.
        await SeedAsync(Row(1, "a"), Row(2, "b"), Row(3, "c"));

        // Act
        PaginationResponse<TranslationListItemResponse> response =
            await GetHateoasAsync<PaginationResponse<TranslationListItemResponse>>(
                AdminClient(), "/api/v1/translations?sort=gossipId:desc&page=2&pageSize=1");

        // Assert: the active sort rides every page link (the operand colon may be URL-encoded).
        response.Page.ShouldBe(2);
        response.Links.First(l => l.Rel == Rels.Self).Href.ShouldContain("sort=gossipId", Case.Insensitive);
        response.Links.First(l => l.Rel == Rels.FirstPage).Href.ShouldContain("sort=gossipId", Case.Insensitive);
        response.Links.First(l => l.Rel == Rels.LastPage).Href.ShouldContain("sort=gossipId", Case.Insensitive);
        response.Links.First(l => l.Rel == Rels.PreviousPage).Href.ShouldContain("sort=gossipId", Case.Insensitive);
        response.Links.First(l => l.Rel == Rels.NextPage).Href.ShouldContain("sort=gossipId", Case.Insensitive);
    }

    [Fact]
    public async Task ListTranslations_PlainJson_OmitsItemAndPaginationLinks()
    {
        // Arrange
        Translation row = await SeedAsync(Row(1, "Aragorn", TranslationStatus.Draft));
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/translations");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.Json));

        // Act
        HttpResponseMessage httpResponse = await AdminClient().SendAsync(request);
        PaginationResponse<TranslationListItemResponse> response =
            (await httpResponse.Content.ReadFromJsonAsync<PaginationResponse<TranslationListItemResponse>>(JsonOptions))!;

        // Assert
        response.Links.Count.ShouldBe(0);
        response.Items.First(i => i.Id == row.Id).Links.Count.ShouldBe(0);
    }

    private async Task<T> GetHateoasAsync<T>(HttpClient client, string url) where T : class
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.HateoasJson));

        HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.HateoasJson);

        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    private HttpClient AdminClient() => ClientForRole(AuthConstants.Roles.Admin);

    private HttpClient TranslatorClient() => ClientForRole(AuthConstants.Roles.Translator);

    private HttpClient ClientForRole(string role)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(role));
        return client;
    }

    private async Task<Translation> SeedAsync(params Translation[] rows)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        dbContext.Translations.AddRange(rows);
        await dbContext.SaveChangesAsync();
        return rows[0];
    }

    private Translation Row(
        int gossipId,
        string source,
        TranslationStatus status = TranslationStatus.Untranslated,
        bool removed = false)
    {
        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create(source, null, null).Value,
            _versionId,
            Now).Value;

        switch (status)
        {
            case TranslationStatus.Draft:
                row.ProvideTranslation("Polski tekst", _seederId, Now);
                break;
            case TranslationStatus.NeedsReview:
                row.ProvideTranslation("Polski tekst", _seederId, Now);
                row.ApplySourceChange(TranslationSource.Create($"{source} (reworded)", null, null).Value, _versionId, Now);
                break;
            case TranslationStatus.Approved:
                row.ProvideTranslation("Polski tekst", _seederId, Now);
                row.Approve(_seederId, Now);
                break;
        }

        if (removed)
        {
            row.MarkRemoved(_versionId, Now);
        }

        return row;
    }
}
