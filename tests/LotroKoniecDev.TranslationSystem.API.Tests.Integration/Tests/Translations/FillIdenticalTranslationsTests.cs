using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.Tests.Shared;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
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
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit.Abstractions;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Translations;

/// <summary>
/// The copy of one text into every empty row with the same English (#1587, spec 0019) against real
/// PostgreSQL: the row query, the one transaction, the permits and their hand-back.
/// </summary>
[Collection("TranslationApi")]
public sealed class FillIdenticalTranslationsTests : IAsyncLifetime
{
    private const int FileId = 620756997;
    private const string SourceText = "Wooden Spear[E]";
    private const string Polish = "Drewniana włócznia";
    private const string FileRoute = "/api/v1/translation-files/pl";
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // ILogger<T> writes a nested type with a dot, where Type.FullName has a plus.
    private static readonly string HandlerLogCategory =
        typeof(FillIdenticalTranslations.Handler).FullName!.Replace('+', '.');

    private readonly TranslationSystemApiFactory _factory;
    private readonly ITestOutputHelper _output;
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
    public async Task Fill_OnARowWithEmptyTwins_ShouldSaveTheRowAndEveryTwinAsADraftOfTheCaller()
    {
        // Arrange
        Translation row = Row(1);
        await SeedAsync(row, Row(2), Row(3), Row(4, source: "Dwarf[D]"));
        Guid subject = Guid.NewGuid();
        using HttpClient client = Client(_factory, AuthConstants.Roles.Translator, subject);

        // Act
        HttpResponseMessage response = await FillAsync(client, row.Id, Polish);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        FillIdenticalTranslationsResponse body = (await response.Content.ReadFromJsonAsync<FillIdenticalTranslationsResponse>(JsonOptions))!;
        body.FilledCount.ShouldBe(2);
        body.TranslationSaved.ShouldBeTrue();
        body.Translation.TranslatedText.ShouldBe(Polish);
        body.Translation.Status.ShouldBe(TranslationStatus.Draft);

        TranslatorId caller = await TranslatorIdOfAsync(_factory, subject);
        IReadOnlyList<Translation> stored = await ReadRowsAsync();
        stored.Where(translation => translation.FragmentKey.GossipId is 1 or 2 or 3)
            .ShouldAllBe(translation => translation.TranslatedText == Polish
                && translation.Status == TranslationStatus.Draft
                && translation.SubmittedById == caller);
        stored.Single(translation => translation.FragmentKey.GossipId == 4).TranslatedText.ShouldBeNull();
    }

    [Fact]
    public async Task Fill_ShouldChangeOnlyTheEmptyLiveRowsWithTheSameTextAndArgs()
    {
        // Arrange: one real twin, and one row of every kind that must stay as it is.
        Translation row = Row(1);
        await SeedAsync(
            row,
            Row(2),
            Row(3, polish: "Inna włócznia"),
            Row(4, polish: "Zatwierdzona włócznia", approve: true),
            NeedsReviewRow(5, "Stara włócznia"),
            Row(6, removed: true),
            Row(7, argsOrder: "1", argsId: "1"),
            Row(8, source: "Wooden Spear[n]"),
            Row(9, source: "wooden spear[E]"));
        IReadOnlyList<Translation> before = await ReadRowsAsync();

        // Act
        HttpResponseMessage response = await FillAsync(TranslatorClient(), row.Id, Polish);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<FillIdenticalTranslationsResponse>(JsonOptions))!.FilledCount.ShouldBe(1);
        IReadOnlyList<Translation> after = await ReadRowsAsync();
        after.Where(translation => translation.FragmentKey.GossipId is 1 or 2)
            .ShouldAllBe(translation => translation.TranslatedText == Polish);
        after.Where(translation => translation.FragmentKey.GossipId > 2)
            .Select(translation => (translation.FragmentKey.GossipId, translation.TranslatedText, translation.Status, translation.UpdatedAt))
            .ShouldBe(before.Where(translation => translation.FragmentKey.GossipId > 2)
                .Select(translation => (translation.FragmentKey.GossipId, translation.TranslatedText, translation.Status, translation.UpdatedAt)));
    }

    [Fact]
    public async Task Fill_WithTheTextAnApprovedRowAlreadyHas_ShouldKeepItApprovedTakeNoOverwriteAndFillTheEmptyRows()
    {
        // Arrange: the normal use, from a row that is already translated. The trailing line break is what
        // the save removes, so the text is the same.
        Translation row = Row(1, polish: Polish, approve: true);
        await SeedAsync(row, Row(2), Row(3));

        // Act
        HttpResponseMessage response = await FillAsync(TranslatorClient(), row.Id, Polish + "\n");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        FillIdenticalTranslationsResponse body = (await response.Content.ReadFromJsonAsync<FillIdenticalTranslationsResponse>(JsonOptions))!;
        body.TranslationSaved.ShouldBeFalse();
        body.FilledCount.ShouldBe(2);
        body.Translation.Status.ShouldBe(TranslationStatus.Approved);
        (await ReadRowsAsync()).Where(translation => translation.FragmentKey.GossipId is 2 or 3)
            .ShouldAllBe(translation => translation.TranslatedText == Polish && translation.Status == TranslationStatus.Draft);
        (await CountPermitsAsync()).ShouldBe((2, 0));
    }

    [Fact]
    public async Task Fill_FromAnApprovedRow_ShouldNotChangeTheGameFile()
    {
        // Arrange
        Translation row = Row(1, polish: Polish, approve: true);
        await SeedAsync(row, Row(2), Row(3));
        await RebuildArtifactAsync();
        HttpResponseMessage before = await _factory.CreateClient().GetAsync(FileRoute);
        string fileBefore = await before.Content.ReadAsStringAsync();

        // Act
        HttpResponseMessage response = await FillAsync(TranslatorClient(), row.Id, Polish);
        await WaitForRebuildsAsync();
        HttpResponseMessage after = await _factory.CreateClient().GetAsync(FileRoute);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        after.Headers.ETag.ShouldBe(before.Headers.ETag);
        (await after.Content.ReadAsStringAsync()).ShouldBe(fileBefore);
    }

    [Theory]
    [InlineData("Nowa włócznia", 3, 1)]
    [InlineData("Stara włócznia", 2, 0)]
    public async Task Fill_AsAPlainTranslator_ShouldSpendOnePermitPerSavedRow(
        string typedText,
        int expectedPermits,
        int expectedOverwrites)
    {
        // Arrange: the row has Polish, so only its own save can be an overwrite.
        Translation row = Row(1, polish: "Stara włócznia");
        await SeedAsync(row, Row(2), Row(3));

        // Act
        HttpResponseMessage response = await FillAsync(TranslatorClient(), row.Id, typedText);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountPermitsAsync()).ShouldBe((expectedPermits, expectedOverwrites));
    }

    [Fact]
    public async Task Fill_WhenTheCopyDoesNotFitTheSaveLimit_ShouldRefuseWithTheSaveCodeChangeNoRowAndLeaveNoPermit()
    {
        // Arrange: three saves needed, two allowed.
        Translation row = Row(1);
        await SeedAsync(row, Row(2), Row(3));
        await using WebApplicationFactory<Program> host = CreateHost(saveLimit: 2);
        using HttpClient client = Client(host, AuthConstants.Roles.Translator, Guid.NewGuid());

        // Act
        HttpResponseMessage response = await FillAsync(client, row.Id, Polish);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadErrorCodeAsync(response)).ShouldBe("Translations.SaveBudgetExhausted");
        (await ReadRowsAsync()).ShouldAllBe(translation => translation.TranslatedText == null);
        (await CountPermitsAsync()).ShouldBe((0, 0));
    }

    [Theory]
    [InlineData(AuthConstants.Roles.Admin)]
    [InlineData(AuthConstants.Roles.Reviewer)]
    public async Task Fill_AsAnAdminOrAReviewer_ShouldNeitherBeRefusedNorSpendTheBudget(string role)
    {
        // Arrange: a limit of one save would refuse a plain translator.
        Translation row = Row(1);
        await SeedAsync(row, Row(2), Row(3));
        await using WebApplicationFactory<Program> host = CreateHost(saveLimit: 1);
        using HttpClient client = role == AuthConstants.Roles.Reviewer
            ? ReviewerClient(host, Guid.NewGuid())
            : Client(host, role, Guid.NewGuid());

        // Act
        HttpResponseMessage response = await FillAsync(client, row.Id, Polish);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadRowsAsync()).ShouldAllBe(translation => translation.TranslatedText == Polish);
        (await CountPermitsAsync()).ShouldBe((0, 0));
    }

    [Fact]
    public async Task Fill_WhenNoEmptyRowIsLeft_ShouldSaveTheRowAloneAndReportNoFilledRow()
    {
        // Arrange
        Translation row = Row(1);
        await SeedAsync(row, Row(2, polish: "Inna włócznia"));

        // Act
        HttpResponseMessage response = await FillAsync(TranslatorClient(), row.Id, Polish);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        FillIdenticalTranslationsResponse body = (await response.Content.ReadFromJsonAsync<FillIdenticalTranslationsResponse>(JsonOptions))!;
        body.FilledCount.ShouldBe(0);
        body.TranslationSaved.ShouldBeTrue();
        (await ReadRowsAsync()).Select(translation => translation.TranslatedText).ShouldBe([Polish, "Inna włócznia"]);
    }

    [Fact]
    public async Task Fill_WithAnUnchangedTextAndNoEmptyRow_ShouldWriteNothingAndSpendNothing()
    {
        // Arrange
        Translation row = Row(1, polish: Polish);
        await SeedAsync(row, Row(2, polish: Polish));

        // Act
        HttpResponseMessage response = await FillAsync(TranslatorClient(), row.Id, Polish);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        FillIdenticalTranslationsResponse body = (await response.Content.ReadFromJsonAsync<FillIdenticalTranslationsResponse>(JsonOptions))!;
        body.FilledCount.ShouldBe(0);
        body.TranslationSaved.ShouldBeFalse();
        (await ReadRowsAsync()).ShouldAllBe(translation => translation.UpdatedAt == Now);
        (await CountPermitsAsync()).ShouldBe((0, 0));
    }

    [Fact]
    public async Task Fill_WhenATwinWasFilledAfterThePanelWasRead_ShouldLeaveItAndCountOnlyTheRealEmptyRows()
    {
        // Arrange: the panel counts three empty rows, then someone saves one of them.
        Translation row = Row(1);
        await SeedAsync(row, Row(2), Row(3), Row(4));
        using HttpClient client = TranslatorClient();
        SameSourceTextTranslationsResponse panel = (await client.GetFromJsonAsync<SameSourceTextTranslationsResponse>(
            $"/api/v1/translations/{row.Id.Value}/same-source-text", JsonOptions))!;
        HttpResponseMessage otherSave = await client.PutAsJsonAsync(
            "/api/v1/translations", new UpsertTranslationRequest(FileId, 3, "Cudza włócznia"));

        // Act
        HttpResponseMessage response = await FillAsync(client, row.Id, Polish);

        // Assert
        panel.EmptyIdenticalCount.ShouldBe(3);
        otherSave.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<FillIdenticalTranslationsResponse>(JsonOptions))!.FilledCount.ShouldBe(2);
        (await ReadRowsAsync()).Select(translation => translation.TranslatedText)
            .ShouldBe([Polish, Polish, "Cudza włócznia", Polish]);
    }

    [Fact]
    public async Task Fill_WhenAParallelSaveChangesATwinFirst_ShouldAnswer409ChangeNoRowAndLeaveNoPermit()
    {
        // Arrange: another connection saves twin 3 after the fill loaded it and before its write.
        Translation row = Row(1);
        await SeedAsync(row, Row(2), Row(3), Row(4));
        SaveTwinBeforeTheFillWrites interceptor = new(FileId, gossipId: 3, "Cudza włócznia");
        await using WebApplicationFactory<Program> host = CreateHost(saveLimit: 1_000, interceptor: interceptor);
        using HttpClient client = Client(host, AuthConstants.Roles.Translator, Guid.NewGuid());

        // Act
        HttpResponseMessage response = await FillAsync(client, row.Id, Polish);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadRowsAsync()).Select(translation => translation.TranslatedText)
            .ShouldBe([null, null, "Cudza włócznia", null]);
        (await CountPermitsAsync()).ShouldBe((0, 0));
    }

    [Fact]
    public async Task Fill_WhenTheCommitLandsButItsAnswerIsLost_ShouldCountTheFillAsDoneAndKeepItsPermits()
    {
        // Arrange
        Translation row = Row(1);
        await SeedAsync(row, Row(2), Row(3));
        LoseTheFirstCommit interceptor = new(afterTheCommit: true);
        await using WebApplicationFactory<Program> host = CreateHost(saveLimit: 1_000, interceptor: interceptor);
        using HttpClient client = Client(host, AuthConstants.Roles.Translator, Guid.NewGuid());

        // Act
        HttpResponseMessage response = await FillAsync(client, row.Id, Polish);

        // Assert
        interceptor.Commits.ShouldBe(1);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<FillIdenticalTranslationsResponse>(JsonOptions))!.FilledCount.ShouldBe(2);
        (await ReadRowsAsync()).ShouldAllBe(translation => translation.TranslatedText == Polish);
        (await CountPermitsAsync()).ShouldBe((3, 0));
    }

    [Fact]
    public async Task Fill_WhenTheCommitFailsBeforeItLands_ShouldRunTheSaveAgainOnce()
    {
        // Arrange
        Translation row = Row(1);
        await SeedAsync(row, Row(2), Row(3));
        LoseTheFirstCommit interceptor = new(afterTheCommit: false);
        await using WebApplicationFactory<Program> host = CreateHost(saveLimit: 1_000, interceptor: interceptor);
        using HttpClient client = Client(host, AuthConstants.Roles.Translator, Guid.NewGuid());

        // Act
        HttpResponseMessage response = await FillAsync(client, row.Id, Polish);

        // Assert
        interceptor.Commits.ShouldBe(2);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadRowsAsync()).ShouldAllBe(translation => translation.TranslatedText == Polish);
        (await CountPermitsAsync()).ShouldBe((3, 0));
    }

    [Fact]
    public async Task Fill_WithADifferentPlaceholderCountThanTheEnglish_ShouldReturn400AndLeaveNoTrace()
    {
        // Arrange
        Translation row = Row(1, source: "Talk to <--DO_NOT_TOUCH!-->");
        await SeedAsync(row, Row(2, source: "Talk to <--DO_NOT_TOUCH!-->"));

        // Act
        HttpResponseMessage response = await FillAsync(TranslatorClient(), row.Id, "Porozmawiaj z kimś");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(response)).ShouldBe("TranslationEntity.TranslatedText.PlaceholderCountMismatch");
        (await ReadRowsAsync()).ShouldAllBe(translation => translation.TranslatedText == null);
        (await CountPermitsAsync()).ShouldBe((0, 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Fill_WithABlankText_ShouldReturn400(string typedText)
    {
        // Arrange
        Translation row = Row(1);
        await SeedAsync(row, Row(2));

        // Act
        HttpResponseMessage response = await FillAsync(TranslatorClient(), row.Id, typedText);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(response)).ShouldBe("Translations.Validation");
    }

    [Fact]
    public async Task Fill_OnARemovedRow_ShouldReturn422()
    {
        // Arrange
        Translation row = Row(1, removed: true);
        await SeedAsync(row, Row(2));

        // Act
        HttpResponseMessage response = await FillAsync(TranslatorClient(), row.Id, Polish);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorCodeAsync(response)).ShouldBe("TranslationEntity.CannotEditRemoved");
    }

    [Fact]
    public async Task Fill_WithUnknownId_ShouldReturn404()
    {
        // Act
        HttpResponseMessage response = await FillAsync(TranslatorClient(), TranslationId.Create(), Polish);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Fill_WithoutToken_ShouldReturn401()
    {
        // Arrange
        Translation row = Row(1);
        await SeedAsync(row, Row(2));

        // Act
        HttpResponseMessage response = await FillAsync(_factory.CreateClient(), row.Id, Polish);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadRowsAsync()).ShouldAllBe(translation => translation.TranslatedText == null);
    }

    [Fact]
    public async Task Fill_WithoutTheTranslatorRole_ShouldReturn403()
    {
        // Arrange
        Translation row = Row(1);
        await SeedAsync(row, Row(2));
        using HttpClient client = Client(_factory, "Reader", Guid.NewGuid());

        // Act
        HttpResponseMessage response = await FillAsync(client, row.Id, Polish);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadRowsAsync()).ShouldAllBe(translation => translation.TranslatedText == null);
    }

    [Fact]
    public async Task Fill_ShouldWriteOneInformationLineThatNamesTheRowAndTheFilledCount()
    {
        // Arrange
        Translation row = Row(1);
        await SeedAsync(row, Row(2), Row(3));
        await using WebApplicationFactory<Program> host = CreateHost(saveLimit: 1_000);
        using HttpClient client = Client(host, AuthConstants.Roles.Translator, Guid.NewGuid());

        // Act
        HttpResponseMessage response = await FillAsync(client, row.Id, Polish);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        List<CapturingLoggerFactory.LogEntry> filled = _loggerFactory.Entries
            .Where(entry => entry.Category == HandlerLogCategory && entry.EventId.Id == EventIds.IdenticalRowsFilled)
            .ToList();
        filled.Count.ShouldBe(1);
        filled[0].Level.ShouldBe(LogLevel.Information);
        filled[0].Message.ShouldContain($"({FileId}, 1)");
        filled[0].Message.ShouldContain("into 2 empty rows");
        filled[0].Message.ShouldNotStartWith(TranslationSaveBudgetLogText.OverwrittenPrefix);
        filled[0].Message.ShouldNotStartWith(TranslationSaveBudgetLogText.BudgetPrefix);
    }

    [Theory]
    [InlineData(2_000)]
    [InlineData(14_000)]
    public async Task Fill_IntoALargeGroup_ShouldFillEveryRow(int emptyRows)
    {
        // Arrange: 2,000 is the size of a common one-word group; 14,000 is just above the largest group on
        // prod (13,682 rows, 2026-11-27). The time is written to the output for the PR, never asserted.
        Translation row = Row(1);
        await SeedManyAsync(row, emptyRows);
        await using WebApplicationFactory<Program> host = CreateHost(saveLimit: emptyRows + 1);
        using HttpClient budgetedClient = Client(host, AuthConstants.Roles.Translator, Guid.NewGuid());
        budgetedClient.Timeout = TimeSpan.FromMinutes(5);
        Stopwatch stopwatch = Stopwatch.StartNew();

        // Act
        HttpResponseMessage response = await FillAsync(budgetedClient, row.Id, Polish);
        stopwatch.Stop();

        // Assert
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"Fill of {emptyRows} empty rows: {stopwatch.Elapsed.TotalSeconds:F2} s"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<FillIdenticalTranslationsResponse>(JsonOptions))!.FilledCount.ShouldBe(emptyRows);
        (await CountTranslatedRowsAsync()).ShouldBe(emptyRows + 1);
        (await CountPermitsAsync()).ShouldBe((emptyRows + 1, 0));
    }

    public FillIdenticalTranslationsTests(TranslationSystemApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    private static Task<HttpResponseMessage> FillAsync(HttpClient client, TranslationId id, string translatedText)
        => client.PostAsJsonAsync(
            $"/api/v1/translations/{id.Value}/fill-identical", new FillIdenticalTranslationsRequest(translatedText));

    private HttpClient TranslatorClient() => Client(_factory, AuthConstants.Roles.Translator, Guid.NewGuid());

    private static HttpClient Client(WebApplicationFactory<Program> host, string role, Guid subject)
    {
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(role, AuthConstants.Scopes.Api, subject));
        return client;
    }

    private static HttpClient ReviewerClient(WebApplicationFactory<Program> host, Guid subject)
    {
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TranslationSystemApiFactory.CreateAccessTokenWithRoles(
                subject, AuthConstants.Roles.Translator, AuthConstants.Roles.Reviewer));
        return client;
    }

    private WebApplicationFactory<Program> CreateHost(int saveLimit, IInterceptor? interceptor = null)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "TranslationSaveBudget:SaveLimit", saveLimit.ToString(CultureInfo.InvariantCulture) },
                    { "TranslationSaveBudget:SaveAlertThreshold", saveLimit.ToString(CultureInfo.InvariantCulture) },
                    { "TranslationSaveBudget:OverwriteLimit", saveLimit.ToString(CultureInfo.InvariantCulture) },
                    { "TranslationSaveBudget:OverwriteAlertThreshold", saveLimit.ToString(CultureInfo.InvariantCulture) }
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILoggerFactory>(_loggerFactory);

                // AddDbContext calls add up since EF Core 9, as the factory's own SQL recorder relies on.
                if (interceptor is not null)
                {
                    services.AddDbContext<ApplicationWriteDbContext>(
                        options => options.AddInterceptors(interceptor),
                        contextLifetime: ServiceLifetime.Scoped,
                        optionsLifetime: ServiceLifetime.Singleton);
                }
            });
        });
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.TryGetProperty("errorCode", out JsonElement code) ? code.GetString() : null;
    }

    private async Task SeedAsync(params Translation[] rows)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        dbContext.Translations.AddRange(rows);
        await dbContext.SaveChangesAsync();
    }

    private async Task SeedManyAsync(Translation row, int emptyRows)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
        dbContext.Translations.Add(row);
        dbContext.Translations.AddRange(Enumerable.Range(2, emptyRows).Select(gossipId => Row(gossipId)));
        await dbContext.SaveChangesAsync();
    }

    private Translation Row(
        long gossipId,
        string source = SourceText,
        string? polish = null,
        bool approve = false,
        bool removed = false,
        string? argsOrder = null,
        string? argsId = null)
    {
        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create(source, argsOrder, argsId).Value,
            _versionId,
            Now).Value;

        if (polish is not null)
        {
            row.ProvideTranslation(polish, _seederId, Now);
        }

        if (approve)
        {
            row.Approve(_seederId, Now);
        }

        if (removed)
        {
            row.MarkRemoved(_versionId, Now);
        }

        return row;
    }

    /// <summary>A row whose English changed to <see cref="SourceText"/> after it got Polish (spec 0001).</summary>
    private Translation NeedsReviewRow(long gossipId, string polish)
    {
        Translation row = Row(gossipId, source: "Old spear[E]", polish: polish);
        row.ApplySourceChange(TranslationSource.Create(SourceText, null, null).Value, _versionId, Now);
        return row;
    }

    private async Task<IReadOnlyList<Translation>> ReadRowsAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        return await dbContext.Translations
            .AsNoTracking()
            .OrderBy(translation => translation.FragmentKey.GossipId)
            .ToListAsync();
    }

    private async Task<int> CountTranslatedRowsAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        return await dbContext.Translations.CountAsync(translation => translation.TranslatedText != null);
    }

    private async Task<(int Saves, int Overwrites)> CountPermitsAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        int saves = await dbContext.TranslationSaves.CountAsync();
        int overwrites = await dbContext.TranslationSaves.CountAsync(translationSave => translationSave.ReplacedPolish);
        return (saves, overwrites);
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

    private async Task RebuildArtifactAsync()
    {
        IPrecomputedTranslationFileProjector builder = _factory.Services.GetRequiredService<IPrecomputedTranslationFileProjector>();
        await builder.RebuildAsync("pl", CancellationToken.None);
    }

    private async Task WaitForRebuildsAsync()
    {
        TranslationFileRebuildScheduler scheduler = _factory.Services.GetRequiredService<TranslationFileRebuildScheduler>();
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (scheduler.PendingCount > 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Saves one twin on its own connection right before the fill's write, as a parallel save would, so the
    /// fill meets a moved concurrency token.
    /// </summary>
    private sealed class SaveTwinBeforeTheFillWrites : SaveChangesInterceptor
    {
        private readonly int _fileId;
        private readonly long _gossipId;
        private readonly string _polish;
        private int _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            DbContext context = eventData.Context!;
            bool fillsTheTwin = context.ChangeTracker.Entries<Translation>()
                .Any(entry => entry.State == EntityState.Modified && entry.Entity.FragmentKey.GossipId == _gossipId);
            if (fillsTheTwin && Interlocked.Exchange(ref _done, 1) == 0)
            {
                await using NpgsqlConnection connection = new(context.Database.GetConnectionString());
                await connection.OpenAsync(cancellationToken);
                await using NpgsqlCommand command = new(
                    "UPDATE translation.\"Translations\" SET \"TranslatedText\" = @polish WHERE \"FileId\" = @fileId AND \"GossipId\" = @gossipId",
                    connection);
                command.Parameters.AddWithValue("polish", _polish);
                command.Parameters.AddWithValue("fileId", _fileId);
                command.Parameters.AddWithValue("gossipId", _gossipId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public SaveTwinBeforeTheFillWrites(int fileId, long gossipId, string polish)
        {
            _fileId = fileId;
            _gossipId = gossipId;
            _polish = polish;
        }
    }

    /// <summary>
    /// Throws the transient error of a lost commit answer once, after the fill's commit landed or before it.
    /// Only the commit that writes translations counts, not the caller's first-save provisioning.
    /// </summary>
    private sealed class LoseTheFirstCommit : DbTransactionInterceptor
    {
        private readonly bool _afterTheCommit;
        private int _commits;

        public int Commits => _commits;

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (WritesTranslations(eventData.Context)
                && Interlocked.Increment(ref _commits) == 1
                && !_afterTheCommit)
            {
                throw new NpgsqlException("The connection broke before the commit.", new TimeoutException());
            }

            return await base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
            if (_afterTheCommit && WritesTranslations(eventData.Context) && _commits == 1)
            {
                throw new NpgsqlException("The answer to the commit was lost.", new TimeoutException());
            }
        }

        public LoseTheFirstCommit(bool afterTheCommit)
        {
            _afterTheCommit = afterTheCommit;
        }

        // The fill accepts its changes only after the commit, so they are still Modified here.
        private static bool WritesTranslations(DbContext? context) =>
            context is not null
            && context.ChangeTracker.Entries<Translation>().Any(entry => entry.State == EntityState.Modified);
    }
}
