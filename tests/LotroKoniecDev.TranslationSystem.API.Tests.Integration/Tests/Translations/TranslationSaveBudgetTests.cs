using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.Tests.Shared;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Translations;

/// <summary>
/// The per-account save budget of ADR-0072 (#687) against real PostgreSQL: the count lives in the
/// database, so it survives a restart and holds across parallel requests.
/// </summary>
[Collection("TranslationApi")]
public sealed class TranslationSaveBudgetTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private const string Route = "/api/v1/translations";
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);

    // ILogger<T> writes a nested type with a dot, where Type.FullName has a plus.
    private static readonly string HandlerLogCategory = typeof(UpsertTranslation.Handler).FullName!.Replace('+', '.');

    private readonly TranslationSystemApiFactory _factory;
    private readonly CapturingLoggerFactory _loggerFactory = new();
    private GameVersionId _versionId;
    private TranslatorId _seederId;

    public async Task InitializeAsync()
    {
        // CASCADE through Translators also empties the budget table.
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"TranslationArtifacts\", translation.\"Translators\" CASCADE;");

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

    [Fact]
    public async Task Upsert_PastTheSaveLimit_ShouldReturn429WithItsCodeAndKeepTheRowUntranslated()
    {
        // Arrange
        await SeedUntranslatedAsync(1, 2, 3);
        await using WebApplicationFactory<Program> host = CreateHost(saveLimit: 2, overwriteLimit: 2);
        using HttpClient client = TranslatorClient(host, Guid.NewGuid());
        (await SaveAsync(client, 1)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SaveAsync(client, 2)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        HttpResponseMessage response = await SaveAsync(client, 3);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadErrorCodeAsync(response)).ShouldBe("Translations.SaveBudgetExhausted");
        (await ReadTranslatedTextAsync(3)).ShouldBeNull();
    }

    [Fact]
    public async Task Upsert_PastTheOverwriteLimit_ShouldRefuseTheOverwriteButStillSaveANewRow()
    {
        // Arrange
        await SeedDraftAsync(1, "Alfa");
        await SeedDraftAsync(2, "Beta");
        await SeedUntranslatedAsync(3);
        await using WebApplicationFactory<Program> host = CreateHost(saveLimit: 10, overwriteLimit: 1);
        using HttpClient client = TranslatorClient(host, Guid.NewGuid());
        (await SaveAsync(client, 1)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        HttpResponseMessage overwrite = await SaveAsync(client, 2);
        HttpResponseMessage newRow = await SaveAsync(client, 3);

        // Assert
        overwrite.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadErrorCodeAsync(overwrite)).ShouldBe("Translations.OverwriteBudgetExhausted");
        (await ReadTranslatedTextAsync(2)).ShouldBe("Beta");
        newRow.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Upsert_WhenBothBudgetsAreSpent_ShouldAnswerWithTheSaveRefusal()
    {
        // Arrange: the overwrite answer says untranslated rows still go through, false here.
        await SeedDraftAsync(1, "Alfa");
        await SeedDraftAsync(2, "Beta");
        await using WebApplicationFactory<Program> host = CreateHost(saveLimit: 1, overwriteLimit: 1);
        using HttpClient client = TranslatorClient(host, Guid.NewGuid());
        (await SaveAsync(client, 1)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        HttpResponseMessage response = await SaveAsync(client, 2);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadErrorCodeAsync(response)).ShouldBe("Translations.SaveBudgetExhausted");
    }

    [Fact]
    public async Task Upsert_TwoAccounts_ShouldEachHaveTheirOwnBudget()
    {
        // Arrange
        await SeedUntranslatedAsync(1, 2);
        await using WebApplicationFactory<Program> host = CreateHost(saveLimit: 1, overwriteLimit: 1);
        using HttpClient first = TranslatorClient(host, Guid.NewGuid());
        using HttpClient second = TranslatorClient(host, Guid.NewGuid());
        (await SaveAsync(first, 1)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SaveAsync(first, 2)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Act
        HttpResponseMessage response = await SaveAsync(second, 2);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Upsert_WhenTheEarlierSavesAreOlderThanTheWindow_ShouldNotCountThemAndShouldDeleteThem()
    {
        // Arrange: the budget lives in the database, so a restart keeps it; only time empties it.
        await SeedUntranslatedAsync(1, 2);
        await using WebApplicationFactory<Program> host = CreateHost(saveLimit: 1, overwriteLimit: 1);
        using HttpClient client = TranslatorClient(host, Guid.NewGuid());
        (await SaveAsync(client, 1)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ExecuteSqlAsync("UPDATE translation.\"TranslationSaves\" SET \"SavedAt\" = \"SavedAt\" - interval '25 hours';");

        // Act
        HttpResponseMessage response = await SaveAsync(client, 2);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountLedgerRowsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Upsert_WhenRefused_ShouldLeaveNoBudgetRowBehind()
    {
        // Arrange
        await SeedUntranslatedAsync(1, 2);
        await using WebApplicationFactory<Program> host = CreateHost(saveLimit: 1, overwriteLimit: 1);
        using HttpClient client = TranslatorClient(host, Guid.NewGuid());
        (await SaveAsync(client, 1)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        HttpResponseMessage response = await SaveAsync(client, 2);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await CountLedgerRowsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Upsert_ParallelSavesWithFewPermitsLeft_ShouldNeverPassMoreThanTheLimit()
    {
        // Arrange: a script runs saves in parallel, so a count before the insert would let every
        // parallel request through. One save first, so the account exists before the burst.
        const int limit = 4;
        const int parallelSaves = 12;
        await SeedUntranslatedAsync([.. Enumerable.Range(1, parallelSaves + 1)]);
        await using WebApplicationFactory<Program> host = CreateHost(saveLimit: limit, overwriteLimit: limit);
        using HttpClient client = TranslatorClient(host, Guid.NewGuid());
        (await SaveAsync(client, parallelSaves + 1)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(1, parallelSaves).Select(gossipId => SaveAsync(client, gossipId)));

        // Assert
        int passed = responses.Count(response => response.StatusCode is HttpStatusCode.OK);
        responses.ShouldAllBe(response =>
            response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.TooManyRequests);
        passed.ShouldBeLessThanOrEqualTo(limit - 1);
        (await CountLedgerRowsAsync()).ShouldBe(passed + 1);
        (await CountTranslatedRowsAsync()).ShouldBe(passed + 1);
    }

    [Fact]
    public async Task Upsert_BelowAndAtTheAlertThreshold_ShouldWarnOnlyAtItNamingTheAccount()
    {
        // Arrange
        await SeedUntranslatedAsync(1, 2);
        await using WebApplicationFactory<Program> host = CreateHost(
            saveLimit: 10, overwriteLimit: 10, saveAlertThreshold: 2);
        Guid subject = Guid.NewGuid();
        using HttpClient client = TranslatorClient(host, subject);
        (await SaveAsync(client, 1)).StatusCode.ShouldBe(HttpStatusCode.OK);
        int warningsBelowTheThreshold = BudgetEntries().Count;

        // Act
        HttpResponseMessage response = await SaveAsync(client, 2);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        warningsBelowTheThreshold.ShouldBe(0);
        CapturingLoggerFactory.LogEntry warning = BudgetEntries().ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldStartWith(TranslationSaveBudgetLogText.BudgetPrefix);
        warning.Message.ShouldContain((await TranslatorIdOfAsync(host, subject)).Value.ToString());
        warning.Message.ShouldContain("budget Saves, outcome AlertThreshold, 2 with this save");
    }

    [Fact]
    public async Task Upsert_WhenASaveReplacesPolish_ShouldWriteTheInformationLineTheTotalAlertCounts()
    {
        // Arrange
        await SeedDraftAsync(1, "Alfa");
        await SeedUntranslatedAsync(2);
        await using WebApplicationFactory<Program> host = CreateHost(saveLimit: 10, overwriteLimit: 10);
        using HttpClient client = TranslatorClient(host, Guid.NewGuid());

        // Act
        (await SaveAsync(client, 1)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SaveAsync(client, 2)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Assert: only the save on row 1 replaced Polish.
        CapturingLoggerFactory.LogEntry entry = OverwriteEntries().ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldStartWith(TranslationSaveBudgetLogText.OverwrittenPrefix);
        entry.Message.ShouldContain($"({FileId}, 1)");
    }

    [Fact]
    public async Task Upsert_AsAnAdmin_ShouldNeverBeRefusedNorSpendTheBudget()
    {
        // Arrange
        await SeedDraftAsync(1, "Alfa");
        await SeedDraftAsync(2, "Beta");
        await using WebApplicationFactory<Program> host = CreateHost(saveLimit: 1, overwriteLimit: 1);
        using HttpClient client = AdminClient(host, Guid.NewGuid());

        // Act
        HttpResponseMessage first = await SaveAsync(client, 1);
        HttpResponseMessage second = await SaveAsync(client, 2);

        // Assert
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountLedgerRowsAsync()).ShouldBe(0);
    }

    public TranslationSaveBudgetTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    private WebApplicationFactory<Program> CreateHost(int saveLimit, int overwriteLimit, int? saveAlertThreshold = null)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "TranslationSaveBudget:SaveLimit", saveLimit.ToString(CultureInfo.InvariantCulture) },
                    { "TranslationSaveBudget:SaveAlertThreshold", (saveAlertThreshold ?? saveLimit).ToString(CultureInfo.InvariantCulture) },
                    { "TranslationSaveBudget:OverwriteLimit", overwriteLimit.ToString(CultureInfo.InvariantCulture) },
                    { "TranslationSaveBudget:OverwriteAlertThreshold", overwriteLimit.ToString(CultureInfo.InvariantCulture) }
                });
            });

            builder.ConfigureTestServices(services => services.AddSingleton<ILoggerFactory>(_loggerFactory));
        });
    }

    private static HttpClient TranslatorClient(WebApplicationFactory<Program> host, Guid subject)
        => AuthorizedClient(host, AuthConstants.Roles.Translator, subject);

    private static HttpClient AdminClient(WebApplicationFactory<Program> host, Guid subject)
        => AuthorizedClient(host, AuthConstants.Roles.Admin, subject);

    private static HttpClient AuthorizedClient(WebApplicationFactory<Program> host, string role, Guid subject)
    {
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(role, AuthConstants.Scopes.Api, subject));
        return client;
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, int gossipId)
        => client.PutAsJsonAsync(Route, new UpsertTranslationRequest(FileId, gossipId, $"Polski {gossipId}"));

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.TryGetProperty("errorCode", out JsonElement code) ? code.GetString() : null;
    }

    private List<CapturingLoggerFactory.LogEntry> BudgetEntries()
        => _loggerFactory.Entries
            .Where(entry => entry.Category == HandlerLogCategory
                && entry.EventId.Id == EventIds.TranslationSaveBudgetWarning)
            .ToList();

    private List<CapturingLoggerFactory.LogEntry> OverwriteEntries()
        => _loggerFactory.Entries
            .Where(entry => entry.Category == HandlerLogCategory
                && entry.EventId.Id == EventIds.TranslationOverwritten)
            .ToList();

    private async Task SeedUntranslatedAsync(params int[] gossipIds)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        foreach (int gossipId in gossipIds)
        {
            dbContext.Translations.Add(NewRow(gossipId));
        }

        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Draft, never Approved: an approved row would start a background rebuild in a host this test
    /// disposes at its end.
    /// </summary>
    private async Task SeedDraftAsync(int gossipId, string polish)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translation row = NewRow(gossipId);
        row.ProvideTranslation(polish, _seederId, Now);
        dbContext.Translations.Add(row);
        await dbContext.SaveChangesAsync();
    }

    private Translation NewRow(int gossipId)
        => Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create($"English {gossipId}", null, null).Value,
            _versionId,
            Now).Value;

    private async Task<string?> ReadTranslatedTextAsync(int gossipId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translation row = await dbContext.Translations
            .AsNoTracking()
            .SingleAsync(translation => translation.FragmentKey.FileId == FileId && translation.FragmentKey.GossipId == gossipId);
        return row.TranslatedText;
    }

    private async Task<int> CountLedgerRowsAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        return await dbContext.TranslationSaves.CountAsync();
    }

    private async Task<int> CountTranslatedRowsAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        return await dbContext.Translations.CountAsync(translation => translation.TranslatedText != null);
    }

    private async Task ExecuteSqlAsync(string sql)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task<TranslatorId> TranslatorIdOfAsync(WebApplicationFactory<Program> host, Guid subject)
    {
        using IServiceScope scope = host.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translator translator = await dbContext.Translators
            .AsNoTracking()
            .SingleAsync(candidate => candidate.IdentityId == IdentityId.FromValue(subject));
        return translator.Id;
    }
}
