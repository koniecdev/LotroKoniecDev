using System.Net.Http.Headers;
using System.Text;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Import;

[Collection("TranslationApi")]
public sealed class ImportExportedTextsTests : IAsyncLifetime
{
    private const int FileId = 620756992;

    private readonly TranslationSystemApiFactory _factory;

    public ImportExportedTextsTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"Translators\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Import_AsAdminOnBaseline_ShouldReturn200AndCreateUntranslatedRows()
    {
        // Arrange
        GameVersionId versionId = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient();

        // Act
        HttpResponseMessage response = await client.PostAsync(
            ImportRoute(versionId),
            ExportContent(Line(1, "Alpha"), Line(2, "Beta"), Line(3, "Gamma")));
        ImportSummary? summary = await response.Content.ReadFromJsonAsync<ImportSummary>();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        summary.ShouldNotBeNull();
        summary.Added.ShouldBe(3);
        summary.Unchanged.ShouldBe(0);
        (await CountTranslationsAsync()).ShouldBe(3);
        (await GetTranslationAsync(1))!.Status.ShouldBe(TranslationStatus.Untranslated);
        (await GetVersionStatusAsync(versionId)).ShouldBe(GameVersionStatus.Processed);
    }

    [Fact]
    public async Task Import_IdenticalReUpload_ShouldBeIdempotent()
    {
        // Arrange
        GameVersionId versionId = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient();
        await client.PostAsync(ImportRoute(versionId), ExportContent(Line(1, "Alpha"), Line(2, "Beta")));
        DateTimeOffset firstSeenAt = (await GetTranslationAsync(1))!.UpdatedAt;

        // Act — re-upload the identical file to the same (now processed) version.
        HttpResponseMessage response = await client.PostAsync(
            ImportRoute(versionId),
            ExportContent(Line(1, "Alpha"), Line(2, "Beta")));
        ImportSummary? summary = await response.Content.ReadFromJsonAsync<ImportSummary>();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        summary.ShouldNotBeNull();
        summary.Added.ShouldBe(0);
        summary.Unchanged.ShouldBe(2);
        (await CountTranslationsAsync()).ShouldBe(2);
        // Unchanged rows are a byte-for-byte no-op — the timestamp must not advance on re-import.
        (await GetTranslationAsync(1))!.UpdatedAt.ShouldBe(firstSeenAt);
    }

    [Fact]
    public async Task Import_SecondVersion_ShouldApplyAllDiffOutcomesAndInvalidatePolish()
    {
        // Arrange — baseline three rows, then attach Polish to row 1 so a source change invalidates it.
        GameVersionId firstVersion = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient();
        await client.PostAsync(ImportRoute(firstVersion), ExportContent(Line(1, "Alpha"), Line(2, "Beta"), Line(3, "Gamma")));
        await AttachPolishAsync(gossipId: 1, polish: "Alfa");

        GameVersionId secondVersion = await SeedVersionAsync("48.1");

        // Act — row 1 reworded, row 2 unchanged, row 3 removed, row 4 added.
        HttpResponseMessage response = await client.PostAsync(
            $"{ImportRoute(secondVersion)}?allowMassRemoval=true",
            ExportContent(Line(1, "Alpha reworded"), Line(2, "Beta"), Line(4, "Delta")));
        ImportSummary? summary = await response.Content.ReadFromJsonAsync<ImportSummary>();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        summary.ShouldNotBeNull();
        summary.Added.ShouldBe(1);
        summary.SourceChanged.ShouldBe(1);
        summary.Invalidated.ShouldBe(1);
        summary.Removed.ShouldBe(1);
        summary.Unchanged.ShouldBe(1);

        Translation? invalidated = await GetTranslationAsync(1);
        invalidated!.Status.ShouldBe(TranslationStatus.NeedsReview);
        invalidated.PreviousSourceText.ShouldBe("Alpha");
        invalidated.Source.Text.ShouldBe("Alpha reworded");

        (await GetTranslationAsync(3))!.IsRemoved.ShouldBeTrue();
        (await GetTranslationAsync(4)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Import_MassRemovalWithoutOverride_ShouldReturn422AndLeaveStateIntact()
    {
        // Arrange — baseline three rows, then upload that drops two of them (67% > 20%).
        GameVersionId firstVersion = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient();
        await client.PostAsync(ImportRoute(firstVersion), ExportContent(Line(1, "Alpha"), Line(2, "Beta"), Line(3, "Gamma")));

        GameVersionId secondVersion = await SeedVersionAsync("48.1");

        // Act
        HttpResponseMessage response = await client.PostAsync(ImportRoute(secondVersion), ExportContent(Line(1, "Alpha")));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await GetTranslationAsync(2))!.IsRemoved.ShouldBeFalse();
        (await GetTranslationAsync(3))!.IsRemoved.ShouldBeFalse();
        // A rejected import is all-or-nothing: the version must not flip to processed.
        (await GetVersionStatusAsync(secondVersion)).ShouldBe(GameVersionStatus.Unprocessed);
    }

    [Fact]
    public async Task Import_TruncatedFile_ShouldReturn422()
    {
        // Arrange
        GameVersionId versionId = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient();
        string truncated = string.Join('\n', Line(1, "Alpha"), "620756992||2||missing trailing fields");

        // Act
        HttpResponseMessage response = await client.PostAsync(ImportRoute(versionId), TextContent(truncated));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CountTranslationsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Import_AsTranslator_ShouldReturn403()
    {
        // Arrange
        GameVersionId versionId = await SeedVersionAsync("48.0");
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator));

        // Act
        HttpResponseMessage response = await client.PostAsync(ImportRoute(versionId), ExportContent(Line(1, "Alpha")));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Import_ForUnknownVersion_ShouldReturn404()
    {
        // Arrange
        using HttpClient client = AdminClient();

        // Act
        HttpResponseMessage response = await client.PostAsync(
            ImportRoute(GameVersionId.Create()),
            ExportContent(Line(1, "Alpha")));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Import_WithoutToken_ShouldReturn401()
    {
        // Arrange
        GameVersionId versionId = await SeedVersionAsync("48.0");
        using HttpClient client = _factory.CreateClient();

        // Act
        HttpResponseMessage response = await client.PostAsync(ImportRoute(versionId), ExportContent(Line(1, "Alpha")));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static string ImportRoute(GameVersionId versionId)
        => $"/api/v1/game-versions/{versionId.Value}/import";

    private static string Line(int gossipId, string text) => $"{FileId}||{gossipId}||{text}||NULL||NULL||1";

    private static MultipartFormDataContent ExportContent(params string[] lines) => TextContent(string.Join('\n', lines));

    private static MultipartFormDataContent TextContent(string export)
    {
        ByteArrayContent fileContent = new(Encoding.UTF8.GetBytes(export));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        return new MultipartFormDataContent { { fileContent, "file", "exported.txt" } };
    }

    private HttpClient AdminClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Admin));
        return client;
    }

    private async Task<GameVersionId> SeedVersionAsync(string version)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create(version).Value, DateTimeOffset.UtcNow).Value;
        dbContext.GameVersions.Add(gameVersion);
        await dbContext.SaveChangesAsync();
        return gameVersion.Id;
    }

    private async Task AttachPolishAsync(int gossipId, string polish)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        // The submitter is a local TranslatorId (ADR-0004); ensure its FK target exists.
        Translator submitter = await dbContext.Translators.FirstOrDefaultAsync()
            ?? AddSubmitter(dbContext);

        Translation translation = await dbContext.Translations
            .SingleAsync(row => row.FragmentKey.FileId == FileId && row.FragmentKey.GossipId == gossipId);
        translation.ProvideTranslation(polish, submitter.Id, DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync();
    }

    private static Translator AddSubmitter(ApplicationWriteDbContext dbContext)
    {
        Translator submitter = Translator.Create(
            IdentityId.Create(), DisplayName.Create("Seed Author").Value, email: null, DateTimeOffset.UtcNow).Value;
        dbContext.Translators.Add(submitter);
        return submitter;
    }

    private async Task<Translation?> GetTranslationAsync(int gossipId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        return await dbContext.Translations
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.FragmentKey.FileId == FileId && row.FragmentKey.GossipId == gossipId);
    }

    private async Task<int> CountTranslationsAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        return await dbContext.Translations.CountAsync();
    }

    private async Task<GameVersionStatus> GetVersionStatusAsync(GameVersionId versionId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        GameVersion version = await dbContext.GameVersions.AsNoTracking().SingleAsync(row => row.Id == versionId);
        return version.Status;
    }
}
