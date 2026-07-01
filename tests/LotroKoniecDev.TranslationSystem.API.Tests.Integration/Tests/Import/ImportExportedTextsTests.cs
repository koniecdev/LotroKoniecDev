using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.Bulk;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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

    [Theory]
    [InlineData("")]
    [InlineData("# only a comment, no rows")]
    public async Task Import_EmptyOrCommentsOnlyFile_ShouldReturn422EmptyUpload(string fileContent)
    {
        // Arrange — an upload with no translatable rows must be rejected rather than marking the
        // version processed with no content.
        GameVersionId versionId = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient();

        // Act
        HttpResponseMessage response = await client.PostAsync(ImportRoute(versionId), TextContent(fileContent));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorCodeAsync(response)).ShouldBe("Import.EmptyUpload");
        (await CountTranslationsAsync()).ShouldBe(0);
        (await GetVersionStatusAsync(versionId)).ShouldBe(GameVersionStatus.Unprocessed);
    }

    [Fact]
    public async Task Import_DuplicateFragmentKeyInUpload_ShouldReturn422()
    {
        // Arrange — two rows for the same (FileId, GossipId) make the upload ambiguous.
        GameVersionId versionId = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient();

        // Act
        HttpResponseMessage response = await client.PostAsync(
            ImportRoute(versionId), ExportContent(Line(1, "Alpha"), Line(1, "Alpha duplicate")));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorCodeAsync(response)).ShouldBe("Import.DuplicateFragmentKey");
        (await CountTranslationsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Import_WithOctetStreamFilePart_ShouldStillSucceed()
    {
        // Arrange — the endpoint does not gate on the file part's declared MIME type; the `||` parser
        // is the sole gate, so a valid body served as application/octet-stream is still imported.
        GameVersionId versionId = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient();

        ByteArrayContent fileContent = new(Encoding.UTF8.GetBytes(Line(1, "Alpha")));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using MultipartFormDataContent body = new() { { fileContent, "file", "exported.bin" } };

        // Act
        HttpResponseMessage response = await client.PostAsync(ImportRoute(versionId), body);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountTranslationsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Import_BodyExceedingConfiguredUploadLimit_ShouldReturn413()
    {
        // Arrange — a host whose upload ceiling is tiny, so a small payload deterministically trips the
        // limit. Production lifts the ceiling to ImportUploadLimits.MaxUploadBytes (256 MB) so the
        // ~80 MB exported.txt posts in one request (#208); here we only prove the ceiling is enforced.
        // The single Import:MaxUploadBytes key drives both the request-body cap (RequestSizeLimitAttribute)
        // and the multipart form-length cap (FormOptions), so this asserts the configured ceiling is
        // enforced without isolating which cap fires; both map to the same 413 (see BadHttpRequestExceptionHandler).
        const long uploadLimit = 64 * 1024;
        using WebApplicationFactory<Program> factory = WithUploadLimit(uploadLimit);
        GameVersionId versionId = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient(factory);

        string oversized = string.Join('\n', Enumerable.Repeat(Line(1, "Alpha"), 5_000));
        Encoding.UTF8.GetByteCount(oversized).ShouldBeGreaterThan((int)uploadLimit);

        // Act
        HttpResponseMessage response = await client.PostAsync(ImportRoute(versionId), TextContent(oversized));

        // Assert — rejected at the transport layer before any row is written.
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await CountTranslationsAsync()).ShouldBe(0);
        (await GetVersionStatusAsync(versionId)).ShouldBe(GameVersionStatus.Unprocessed);
    }

    [Fact]
    public async Task Import_BodyWithinConfiguredUploadLimit_ShouldStillSucceed()
    {
        // Arrange — the same tiny ceiling, but a payload comfortably under it must still import: the
        // limit is a ceiling, not a blanket rejection.
        const long uploadLimit = 64 * 1024;
        using WebApplicationFactory<Program> factory = WithUploadLimit(uploadLimit);
        GameVersionId versionId = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient(factory);

        // Act
        HttpResponseMessage response = await client.PostAsync(ImportRoute(versionId), ExportContent(Line(1, "Alpha")));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountTranslationsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Import_LargeBaseline_ShouldBulkInsertEveryRowWithinTheBudget()
    {
        // Arrange — a full-catalog-scale baseline (all added rows). Per-row EF wrote ~700k rows in
        // ~3 min (#214); the COPY path (ADR-0011) must load a hundreds-of-thousands-row baseline in
        // seconds. The budget is spec 0004's < 10 s, which also unambiguously separates the bulk path
        // from any regression to the multi-minute per-row write (a per-row write of this count alone
        // would run tens of seconds). The count sits in the AC's "hundreds of thousands" range at a
        // point that keeps the < 10 s ceiling stable on slower CI hardware.
        const int rowCount = 200_000;
        GameVersionId versionId = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient();
        using MultipartFormDataContent body = LargeExportContent(rowCount);

        // Act
        Stopwatch stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response = await client.PostAsync(ImportRoute(versionId), body);
        stopwatch.Stop();
        ImportSummary? summary = await response.Content.ReadFromJsonAsync<ImportSummary>();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        summary.ShouldNotBeNull();
        summary.Added.ShouldBe(rowCount);
        (await CountTranslationsAsync()).ShouldBe(rowCount);
        (await GetVersionStatusAsync(versionId)).ShouldBe(GameVersionStatus.Processed);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Import_BulkCopyThenFailureBeforeCommit_ShouldRollBackTheCopiedRows()
    {
        // Arrange — the COPY runs on the write context's connection inside the import transaction, so
        // a failure anywhere in that transaction must discard the COPY'd rows too (spec 0001 all-or-
        // nothing). This pins the connection-enlistment the ADR-0011 atomicity design relies on:
        // COPY the rows successfully, then throw before the commit.
        using IServiceScope scope = _factory.Services.CreateScope();
        IBulkTranslationInserter inserter = scope.ServiceProvider.GetRequiredService<IBulkTranslationInserter>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        List<Translation> rows = [NewUntranslated(10, "Ten"), NewUntranslated(11, "Eleven")];
        InvalidOperationException induced = new("induced mid-transaction failure");

        // Act
        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
            {
                await inserter.InsertAsync(rows, transactionToken);
                throw induced;
            }));

        // Assert — the transaction rolled back, so not one COPY'd row survived.
        thrown.ShouldBeSameAs(induced);
        (await CountTranslationsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Import_BaselineRowWithArguments_ShouldCopyArgsColumnsVerbatim()
    {
        // Arrange — a real exported.txt row carries the argument columns; the COPY writer's non-null
        // args branch must round-trip them. Every other baseline fixture uses NULL args, so this pins
        // the non-null path and the COPY mapping of the args/source/version columns for an added row.
        GameVersionId versionId = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient();
        string rowWithArgs = $"{FileId}||7||Greet <--DO_NOT_TOUCH!--> friend||2-1||1-1||1";

        // Act
        HttpResponseMessage response = await client.PostAsync(ImportRoute(versionId), TextContent(rowWithArgs));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Translation? copied = await GetTranslationAsync(7);
        copied.ShouldNotBeNull();
        copied.Source.Text.ShouldBe("Greet <--DO_NOT_TOUCH!--> friend");
        copied.Source.ArgsOrder.ShouldBe("2-1");
        copied.Source.ArgsId.ShouldBe("1-1");
        copied.Status.ShouldBe(TranslationStatus.Untranslated);
        copied.IntroducedInVersion.ShouldBe(versionId);
    }

    [Fact]
    public async Task ExecuteInTransaction_WhenFirstCommitFailsTransiently_ShouldRetryAndPersistTheWholeUnit()
    {
        // Arrange — the write context enables retry-on-failure. An interceptor makes the FIRST commit
        // fail with a transient serialization error, so the execution strategy re-runs the whole unit.
        // This pins the accept-deferral in ExecuteInTransactionAsync: without it, the retry would
        // re-COPY the added row but silently drop the tracked version-processed flag (the changes were
        // accepted on the first, failed attempt), leaving the version Unprocessed. The context is built
        // by hand so the interceptor attaches via AddInterceptors, mirroring the production retry config.
        GameVersionId versionId = await SeedVersionAsync("48.0");

        string connectionString;
        using (IServiceScope seedScope = _factory.Services.CreateScope())
        {
            connectionString = seedScope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>()
                .Database.GetConnectionString()!;
        }

        FailFirstCommitInterceptor interceptor = new();
        DbContextOptions<ApplicationWriteDbContext> options =
            new DbContextOptionsBuilder<ApplicationWriteDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(
                    maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null))
                .AddInterceptors(interceptor)
                .Options;

        await using ApplicationWriteDbContext dbContext = new(options);
        BulkTranslationInserter inserter = new(dbContext);

        // A tracked mutation (mark the version processed) plus a COPY, enlisted as one unit.
        GameVersion version = await dbContext.GameVersions.SingleAsync(row => row.Id == versionId);
        version.MarkAsProcessed();
        List<Translation> rows = [NewUntranslated(20, "Twenty")];

        // Act
        await dbContext.ExecuteInTransactionAsync(transactionToken => inserter.InsertAsync(rows, transactionToken));

        // Assert — the transient commit fired and the strategy retried (self-check), and both halves
        // of the unit are durable: the COPY'd row AND the tracked version flip survived the retry.
        interceptor.CommitAttempts.ShouldBeGreaterThanOrEqualTo(2);
        (await CountTranslationsAsync()).ShouldBe(1);
        (await GetVersionStatusAsync(versionId)).ShouldBe(GameVersionStatus.Processed);
    }

    // Fails the first transaction commit with a transient serialization error (SQLSTATE 40001) the
    // Npgsql retrying execution strategy retries, then lets every later commit through — reproducing a
    // transient fault landing exactly at commit time.
    private sealed class FailFirstCommitInterceptor : DbTransactionInterceptor
    {
        private int _commitAttempts;

        public int CommitAttempts => _commitAttempts;

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _commitAttempts) == 1)
            {
                throw new PostgresException("simulated transient commit failure", "ERROR", "ERROR", "40001");
            }

            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }
    }

    private static MultipartFormDataContent LargeExportContent(int rowCount)
    {
        StringBuilder builder = new(rowCount * 40);
        for (int gossipId = 1; gossipId <= rowCount; gossipId++)
        {
            builder.Append(FileId).Append("||").Append(gossipId).Append("||Source text ")
                .Append(gossipId).Append("||NULL||NULL||1\n");
        }

        return TextContent(builder.ToString());
    }

    private static Translation NewUntranslated(int gossipId, string text)
        => Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create(text, argsOrder: null, argsId: null).Value,
            GameVersionId.Create(),
            DateTimeOffset.UtcNow).Value;

    private WebApplicationFactory<Program> WithUploadLimit(long maxUploadBytes) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configBuilder) =>
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Import:MaxUploadBytes"] = maxUploadBytes.ToString(CultureInfo.InvariantCulture)
                })));

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.TryGetProperty("errorCode", out JsonElement code) ? code.GetString() : null;
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

    private HttpClient AdminClient() => AdminClient(_factory);

    private static HttpClient AdminClient(WebApplicationFactory<Program> factory)
    {
        HttpClient client = factory.CreateClient();
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
