using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Translations;

[Collection("TranslationApi")]
public sealed class ListTranslationsTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;

    public ListTranslationsTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\" CASCADE;");

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create("48.0").Value, Now).Value;
        dbContext.GameVersions.Add(gameVersion);
        await dbContext.SaveChangesAsync();
        _versionId = gameVersion.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task List_ShouldReturnRowsSortedByFileIdThenGossipId()
    {
        // Arrange — seeded out of order, across two file ids.
        await SeedAsync(Row(1, "B", fileId: 200), Row(2, "A", fileId: 100), Row(1, "C", fileId: 100));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?page=1&pageSize=10");

        // Assert
        page.TotalCount.ShouldBe(3);
        page.Items.Select(item => (item.FileId, item.GossipId))
            .ShouldBe([(100, 1L), (100, 2L), (200, 1L)]);
    }

    [Fact]
    public async Task List_ShouldPaginate()
    {
        // Arrange — five rows, two per page.
        await SeedAsync(Row(1, "a"), Row(2, "b"), Row(3, "c"), Row(4, "d"), Row(5, "e"));

        // Act
        PaginationResponse<TranslationListItemResponse> firstPage = await ListAsync("?page=1&pageSize=2");
        PaginationResponse<TranslationListItemResponse> lastPage = await ListAsync("?page=3&pageSize=2");

        // Assert
        firstPage.TotalCount.ShouldBe(5);
        firstPage.Items.Count.ShouldBe(2);
        firstPage.HasNextPage.ShouldBeTrue();
        firstPage.HasPreviousPage.ShouldBeFalse();

        lastPage.Items.Count.ShouldBe(1);
        lastPage.Items.Single().GossipId.ShouldBe(5);
        lastPage.HasNextPage.ShouldBeFalse();
        lastPage.HasPreviousPage.ShouldBeTrue();
    }

    [Fact]
    public async Task List_WithPageBeyondRange_ShouldReturnEmptyWithAccurateTotal()
    {
        // Arrange
        await SeedAsync(Row(1, "a"), Row(2, "b"), Row(3, "c"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?page=99&pageSize=2");

        // Assert
        page.Items.ShouldBeEmpty();
        page.TotalCount.ShouldBe(3);
    }

    [Fact]
    public async Task List_WithSearch_ShouldMatchContentCaseInsensitively()
    {
        // Arrange
        await SeedAsync(Row(1, "Frodo Baggins"), Row(2, "Samwise Gamgee"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?search=FRODO");

        // Assert
        page.Items.Single().GossipId.ShouldBe(1);
    }

    [Fact]
    public async Task List_WithSearch_ShouldAlsoMatchTheTranslatedText()
    {
        // Arrange — only the drafted row carries Polish ("Polski tekst").
        await SeedAsync(Row(1, "Aragorn"), Row(2, "Boromir", TranslationStatus.Draft));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?search=tekst");

        // Assert
        page.Items.Single().GossipId.ShouldBe(2);
    }

    [Fact]
    public async Task List_WithLikeMetacharacterInSearch_ShouldMatchItLiterally()
    {
        // Arrange — '%' must be a literal, not a LIKE wildcard, or "100%" would also match "1000".
        await SeedAsync(Row(1, "Reward 100% bonus"), Row(2, "Reward 1000 bonus"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?search=100%25");

        // Assert
        page.Items.Single().GossipId.ShouldBe(1);
    }

    [Fact]
    public async Task List_WithStatusNeedsReview_ShouldReturnOnlyInvalidatedRows()
    {
        // Arrange — one of each status; NeedsReview is the "needs re-translation" view.
        await SeedAsync(
            Row(1, "untranslated"),
            Row(2, "drafted", TranslationStatus.Draft),
            Row(3, "invalidated", TranslationStatus.NeedsReview));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?status=NeedsReview");

        // Assert
        TranslationListItemResponse item = page.Items.ShouldHaveSingleItem();
        item.GossipId.ShouldBe(3);
        item.Status.ShouldBe(TranslationStatus.NeedsReview);
    }

    [Fact]
    public async Task List_ShouldHideSoftRemovedRowsByDefault()
    {
        // Arrange
        await SeedAsync(Row(1, "kept"), Row(2, "removed", removed: true));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?page=1&pageSize=10");

        // Assert
        page.TotalCount.ShouldBe(1);
        page.Items.Single().GossipId.ShouldBe(1);
    }

    [Fact]
    public async Task List_WithUnsupportedLanguage_ShouldReturn400()
    {
        // Act
        HttpResponseMessage response = await TranslatorClient().GetAsync("/api/v1/translations?lang=de");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_WithoutToken_ShouldReturn401()
    {
        // Act
        HttpResponseMessage response = await _factory.CreateClient().GetAsync("/api/v1/translations");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<PaginationResponse<TranslationListItemResponse>> ListAsync(string queryString)
    {
        HttpResponseMessage response = await TranslatorClient().GetAsync($"/api/v1/translations{queryString}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PaginationResponse<TranslationListItemResponse>>(JsonOptions))!;
    }

    private HttpClient TranslatorClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator));
        return client;
    }

    private async Task SeedAsync(params Translation[] rows)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        dbContext.Translations.AddRange(rows);
        await dbContext.SaveChangesAsync();
    }

    private Translation Row(
        int gossipId,
        string source,
        TranslationStatus status = TranslationStatus.Untranslated,
        bool removed = false,
        int fileId = FileId)
    {
        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(fileId, gossipId).Value,
            TranslationSource.Create(source, null, null).Value,
            _versionId,
            Now).Value;

        switch (status)
        {
            case TranslationStatus.Draft:
                row.ProvideTranslation("Polski tekst", IdentityId.Create(), Now);
                break;
            case TranslationStatus.NeedsReview:
                row.ProvideTranslation("Polski tekst", IdentityId.Create(), Now);
                row.ApplySourceChange(TranslationSource.Create($"{source} (reworded)", null, null).Value, _versionId, Now);
                break;
        }

        if (removed)
        {
            row.MarkRemoved(_versionId, Now);
        }

        return row;
    }
}
