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
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
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
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"Translators\", "
            + "translation.\"TranslationArtifacts\" CASCADE;");
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

        // Act: re-upload the identical file to the same (now processed) version.
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
        // A row that did not change is left completely alone, so its timestamp must not move on a
        // re-import.
        (await GetTranslationAsync(1))!.UpdatedAt.ShouldBe(firstSeenAt);
    }

    [Fact]
    public async Task Import_SecondVersion_ShouldApplyAllDiffOutcomesAndInvalidatePolish()
    {
        // Arrange: baseline three rows, then attach Polish to row 1 so a source change invalidates it.
        GameVersionId firstVersion = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient();
        await client.PostAsync(ImportRoute(firstVersion), ExportContent(Line(1, "Alpha"), Line(2, "Beta"), Line(3, "Gamma")));
        await AttachPolishAsync(gossipId: 1, polish: "Alfa");

        GameVersionId secondVersion = await SeedVersionAsync("48.1");

        // Act: row 1 reworded, row 2 unchanged, row 3 removed, row 4 added.
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
    public async Task Import_ReAddedRemovedRowWithIdenticalSource_ShouldRestoreApprovedStatusAndDistributeItAgain()
    {
        // Arrange: approve Polish for row 1, remove it in the next version, then a third version
        // re-adds the identical English source (spec 0001: re-adding a removed pair with an
        // unchanged source restores the previous status, including Approved).
        GameVersionId firstVersion = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient();
        await client.PostAsync(ImportRoute(firstVersion), ExportContent(Line(1, "Alpha"), Line(2, "Beta")));
        await AttachPolishAsync(gossipId: 1, polish: "Alfa");
        await ApprovePolishAsync(gossipId: 1);

        GameVersionId secondVersion = await SeedVersionAsync("48.1");
        await client.PostAsync($"{ImportRoute(secondVersion)}?allowMassRemoval=true", ExportContent(Line(2, "Beta")));
        (await GetTranslationAsync(1))!.IsRemoved.ShouldBeTrue();

        // Wait for the removal's debounced rebuild to drop the row from the artifact, so the final
        // poll strictly witnesses re-entry instead of a stale pre-removal artifact.
        await TranslationFileDownloadPolling.DownloadWhenConvergedAsync(
            _factory.CreateClient(),
            "/api/v1/translation-files/pl",
            (candidate, content) => candidate.IsSuccessStatusCode && !content.Contains($"{FileId}||1||"));

        GameVersionId thirdVersion = await SeedVersionAsync("48.2");

        // Act
        HttpResponseMessage response = await client.PostAsync(
            ImportRoute(thirdVersion), ExportContent(Line(1, "Alpha"), Line(2, "Beta")));
        ImportSummary? summary = await response.Content.ReadFromJsonAsync<ImportSummary>();

        // Assert: the restore leg is the only outcome: every counter stays put and the row is
        // reported through the warning, with its Approved status and Polish intact.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        summary.ShouldNotBeNull();
        summary.Added.ShouldBe(0);
        summary.SourceChanged.ShouldBe(0);
        summary.Invalidated.ShouldBe(0);
        summary.Removed.ShouldBe(0);
        summary.Unchanged.ShouldBe(1);
        summary.Warnings.ShouldContain(warning => warning.Contains("1 previously-removed row"));

        Translation? restored = await GetTranslationAsync(1);
        restored!.IsRemoved.ShouldBeFalse();
        restored.Status.ShouldBe(TranslationStatus.Approved);
        restored.TranslatedText.ShouldBe("Alfa");

        // The still-Approved row re-enters the distributed file once the debounced background
        // rebuild scheduled by the import converges (PERF-04).
        (HttpResponseMessage download, string file) = await TranslationFileDownloadPolling.DownloadWhenConvergedAsync(
            _factory.CreateClient(),
            "/api/v1/translation-files/pl",
            (candidate, content) => candidate.IsSuccessStatusCode && content.Contains($"{FileId}||1||Alfa||NULL||NULL||1"));
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        file.ShouldContain($"{FileId}||1||Alfa||NULL||NULL||1");
    }

    [Fact]
    public async Task Import_ReAddedRemovedRowWithChangedSource_ShouldClearRemovalAndInvalidate()
    {
        // Arrange: same removal setup, but the third version re-adds row 1 with a reworded source
        // (spec 0001: a changed-source re-add lands as NeedsReview with PreviousSourceText set).
        GameVersionId firstVersion = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient();
        await client.PostAsync(ImportRoute(firstVersion), ExportContent(Line(1, "Alpha"), Line(2, "Beta")));
        await AttachPolishAsync(gossipId: 1, polish: "Alfa");
        await ApprovePolishAsync(gossipId: 1);

        GameVersionId secondVersion = await SeedVersionAsync("48.1");
        await client.PostAsync($"{ImportRoute(secondVersion)}?allowMassRemoval=true", ExportContent(Line(2, "Beta")));
        (await GetTranslationAsync(1))!.IsRemoved.ShouldBeTrue();

        GameVersionId thirdVersion = await SeedVersionAsync("48.2");

        // Act
        HttpResponseMessage response = await client.PostAsync(
            ImportRoute(thirdVersion), ExportContent(Line(1, "Alpha reworded"), Line(2, "Beta")));
        ImportSummary? summary = await response.Content.ReadFromJsonAsync<ImportSummary>();

        // Assert: the changed-source re-add routes through the source-change leg (counted, never
        // warned as a restore): removal cleared, the Polish invalidated for review against the kept
        // previous English.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        summary.ShouldNotBeNull();
        summary.Added.ShouldBe(0);
        summary.SourceChanged.ShouldBe(1);
        summary.Invalidated.ShouldBe(1);
        summary.Removed.ShouldBe(0);
        summary.Unchanged.ShouldBe(1);
        summary.Warnings.ShouldNotContain(warning => warning.Contains("previously-removed"));

        Translation? reAdded = await GetTranslationAsync(1);
        reAdded!.IsRemoved.ShouldBeFalse();
        reAdded.Status.ShouldBe(TranslationStatus.NeedsReview);
        reAdded.PreviousSourceText.ShouldBe("Alpha");
        reAdded.Source.Text.ShouldBe("Alpha reworded");
        reAdded.TranslatedText.ShouldBe("Alfa");
    }

    [Fact]
    public async Task Import_ExportFromPatchedDat_ShouldTreatEchoesAsUnchangedAndInvalidateOnlyTheRealEnglishChange()
    {
        // Arrange: the U49 shape (spec 0012 / #563): a translated, approved corpus, then an export
        // taken from the admin's OWN patched DAT. Resident rows 1-2 come back carrying our Polish as
        // their "source" (echo; row 2 is placeholder-bearing, so its args columns ride along and pin
        // the projection's echo triple), row 3 was collateral-reverted to its identical English,
        // row 4's English really changed (its chunk was replaced, so it reads English too), row 5 is
        // untranslated and untouched.
        const string placeholderPolish = "Witaj <--DO_NOT_TOUCH!--> przyjacielu";
        GameVersionId firstVersion = await SeedVersionAsync("48.8");
        using HttpClient client = AdminClient();
        static MultipartFormDataContent Baseline() =>
            ExportContent(
                Line(1, "Alpha"),
                LineWithArgs(2, "Greet <--DO_NOT_TOUCH!--> friend", "1-1"),
                Line(3, "Gamma"),
                Line(4, "Delta"),
                Line(5, "Epsilon"));
        await client.PostAsync(ImportRoute(firstVersion), Baseline());
        foreach ((int gossipId, string polish) in new[] { (1, "Alfa"), (2, placeholderPolish), (3, "Gama"), (4, "Delty") })
        {
            await AttachPolishAsync(gossipId, polish);
            await ApprovePolishAsync(gossipId);
        }

        // Pin the pre-import artifact to all four approved rows (an idempotent re-upload schedules a
        // rebuild; the approvals above went straight to the DB and did not), so the final poll
        // strictly witnesses the invalidated row LEAVING the file rather than an intermediate build.
        await client.PostAsync(ImportRoute(firstVersion), Baseline());
        await TranslationFileDownloadPolling.DownloadWhenConvergedAsync(
            _factory.CreateClient(),
            "/api/v1/translation-files/pl",
            (candidate, content) => candidate.IsSuccessStatusCode && content.Contains($"{FileId}||4||Delty||NULL||NULL||1"));

        DateTimeOffset residentUpdatedAt = (await GetTranslationAsync(1))!.UpdatedAt;
        GameVersionId secondVersion = await SeedVersionAsync("49.1");

        // Act
        HttpResponseMessage response = await client.PostAsync(
            ImportRoute(secondVersion),
            ExportContent(
                Line(1, "Alfa"),
                LineWithArgs(2, placeholderPolish, "1-1"),
                Line(3, "Gamma"),
                Line(4, "Delta reworded"),
                Line(5, "Epsilon")));
        ImportSummary? summary = await response.Content.ReadFromJsonAsync<ImportSummary>();

        // Assert: only the real English change is invalidated; the echoes are visible in the
        // summary and byte-for-byte untouched (source, Polish, status, timestamp).
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        summary.ShouldNotBeNull();
        summary.Added.ShouldBe(0);
        summary.SourceChanged.ShouldBe(1);
        summary.Invalidated.ShouldBe(1);
        summary.Removed.ShouldBe(0);
        summary.Unchanged.ShouldBe(4);
        summary.Echoed.ShouldBe(2);

        Translation? echoed = await GetTranslationAsync(1);
        echoed!.Source.Text.ShouldBe("Alpha");
        echoed.TranslatedText.ShouldBe("Alfa");
        echoed.Status.ShouldBe(TranslationStatus.Approved);
        echoed.PreviousSourceText.ShouldBeNull();
        echoed.LastSourceChangeInVersion.ShouldBeNull();
        echoed.UpdatedAt.ShouldBe(residentUpdatedAt);

        Translation? echoedWithArgs = await GetTranslationAsync(2);
        echoedWithArgs!.Source.Text.ShouldBe("Greet <--DO_NOT_TOUCH!--> friend");
        echoedWithArgs.Source.ArgsOrder.ShouldBe("1-1");
        echoedWithArgs.Source.ArgsId.ShouldBe("1-1");
        echoedWithArgs.TranslatedText.ShouldBe(placeholderPolish);
        echoedWithArgs.Status.ShouldBe(TranslationStatus.Approved);

        Translation? collateral = await GetTranslationAsync(3);
        collateral!.Source.Text.ShouldBe("Gamma");
        collateral.Status.ShouldBe(TranslationStatus.Approved);

        Translation? invalidated = await GetTranslationAsync(4);
        invalidated!.Status.ShouldBe(TranslationStatus.NeedsReview);
        invalidated.PreviousSourceText.ShouldBe("Delta");
        invalidated.Source.Text.ShouldBe("Delta reworded");
        invalidated.TranslatedText.ShouldBe("Delty");

        // The distributed file keeps every echoed (still Approved) row and drops only the invalidated
        // one, once the import's debounced background rebuild converges (PERF-04).
        (HttpResponseMessage download, string file) = await TranslationFileDownloadPolling.DownloadWhenConvergedAsync(
            _factory.CreateClient(),
            "/api/v1/translation-files/pl",
            (candidate, content) => candidate.IsSuccessStatusCode && !content.Contains($"{FileId}||4||"));
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        file.ShouldContain($"{FileId}||1||Alfa||NULL||NULL||1");
        file.ShouldContain($"{FileId}||2||{placeholderPolish}||1-1||1-1||1");
        file.ShouldContain($"{FileId}||3||Gama||NULL||NULL||1");
        file.ShouldNotContain($"{FileId}||4||");
    }

    [Fact]
    public async Task Import_MassRemovalWithoutOverride_ShouldReturn422AndLeaveStateIntact()
    {
        // Arrange: baseline three rows, then upload that drops two of them (67% > 20%).
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
    public async Task Import_WhileOlderVersionUnprocessed_ShouldSupersedeItInTheSameTransaction()
    {
        // Arrange: the admin skips the older still-unprocessed version and uploads only the newer one
        // (spec 0001, stacked versions). Detected-at is set explicitly so "older" is unambiguous.
        GameVersionId olderVersion = await SeedVersionAsync("48.0", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        GameVersionId newerVersion = await SeedVersionAsync("48.1", new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero));
        using HttpClient client = AdminClient();

        // Act
        HttpResponseMessage response = await client.PostAsync(
            ImportRoute(newerVersion), ExportContent(Line(1, "Alpha"), Line(2, "Beta")));
        ImportSummary? summary = await response.Content.ReadFromJsonAsync<ImportSummary>();

        // Assert: the newer version processed, the older one superseded in the same commit, and the
        // admin is told what was skipped.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        summary.ShouldNotBeNull();
        (await GetVersionStatusAsync(newerVersion)).ShouldBe(GameVersionStatus.Processed);
        (await GetVersionStatusAsync(olderVersion)).ShouldBe(GameVersionStatus.Superseded);
        summary.Warnings.ShouldContain(warning => warning.Contains("1 older unprocessed version"));
    }

    [Fact]
    public async Task Import_AgainstASupersededVersion_ShouldReturn422AndPersistNothing()
    {
        // Arrange: process the newer version, which supersedes the older one, then try an out-of-date
        // import against that older version. The upload matches the catalog exactly, so the
        // mass-removal guard cannot fire first and the supersede check is what rejects it.
        GameVersionId olderVersion = await SeedVersionAsync("48.0", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        GameVersionId newerVersion = await SeedVersionAsync("48.1", new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero));
        using HttpClient client = AdminClient();
        await client.PostAsync(ImportRoute(newerVersion), ExportContent(Line(1, "Alpha"), Line(2, "Beta")));

        // Act
        HttpResponseMessage response = await client.PostAsync(
            ImportRoute(olderVersion), ExportContent(Line(1, "Alpha"), Line(2, "Beta")));

        // Assert: rejected with the supersede error and nothing changed: the older version stays
        // superseded, the newer stays processed, and the catalog is untouched.
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorCodeAsync(response)).ShouldBe("GameVersionEntity.SupersededCannotBeProcessed");
        (await GetVersionStatusAsync(olderVersion)).ShouldBe(GameVersionStatus.Superseded);
        (await GetVersionStatusAsync(newerVersion)).ShouldBe(GameVersionStatus.Processed);
        (await CountTranslationsAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Import_WhileANewerVersionIsUnprocessed_ShouldLeaveTheNewerVersionUnprocessed()
    {
        // Arrange: three stacked unprocessed versions; the admin uploads only the middle one. Only
        // versions detected before it are superseded; a version detected after it stays pending.
        GameVersionId olderVersion = await SeedVersionAsync("48.0", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        GameVersionId targetVersion = await SeedVersionAsync("48.1", new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero));
        GameVersionId newerVersion = await SeedVersionAsync("48.2", new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        using HttpClient client = AdminClient();

        // Act
        HttpResponseMessage response = await client.PostAsync(
            ImportRoute(targetVersion), ExportContent(Line(1, "Alpha")));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetVersionStatusAsync(targetVersion)).ShouldBe(GameVersionStatus.Processed);
        (await GetVersionStatusAsync(olderVersion)).ShouldBe(GameVersionStatus.Superseded);
        (await GetVersionStatusAsync(newerVersion)).ShouldBe(GameVersionStatus.Unprocessed);
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
        // Arrange: an upload with no translatable rows must be rejected rather than marking the
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
        // Arrange: two rows for the same (FileId, GossipId) make the upload ambiguous.
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
    public async Task Import_DuplicateFragmentKeysFarApart_ShouldReturn422ThroughTheStreamingPath()
    {
        // Arrange: the duplicate of row 1 sits thousands of lines later, so the streaming Pass 1
        // must catch it from the accumulated key map, not line adjacency (spec 0006).
        GameVersionId versionId = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient();

        StringBuilder builder = new();
        builder.Append(Line(1, "Alpha")).Append('\n');
        for (int gossipId = 2; gossipId <= 3_000; gossipId++)
        {
            builder.Append(Line(gossipId, $"Text {gossipId}")).Append('\n');
        }

        builder.Append(Line(1, "Alpha duplicate"));

        // Act
        HttpResponseMessage response = await client.PostAsync(ImportRoute(versionId), TextContent(builder.ToString()));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorCodeAsync(response)).ShouldBe("Import.DuplicateFragmentKey");
        (await CountTranslationsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Import_ChangesSpanningMultipleApplyChunks_ShouldApplyEveryChunk()
    {
        // Arrange: a chunk size of 2 forces the 5 reworded rows through 3 apply chunks and the
        // 3 removed rows through 2 (spec 0006 chunked apply), all inside the one transaction.
        using WebApplicationFactory<Program> factory = WithApplyChunkSize(2);
        GameVersionId firstVersion = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient(factory);
        await client.PostAsync(ImportRoute(firstVersion), ExportContent(
            Line(1, "A"), Line(2, "B"), Line(3, "C"), Line(4, "D"), Line(5, "E"),
            Line(6, "F"), Line(7, "G"), Line(8, "H")));

        GameVersionId secondVersion = await SeedVersionAsync("48.1");

        // Act: rows 1-5 reworded, rows 6-8 removed (37% removal needs the override).
        HttpResponseMessage response = await client.PostAsync(
            $"{ImportRoute(secondVersion)}?allowMassRemoval=true",
            ExportContent(Line(1, "A2"), Line(2, "B2"), Line(3, "C2"), Line(4, "D2"), Line(5, "E2")));
        ImportSummary? summary = await response.Content.ReadFromJsonAsync<ImportSummary>();

        // Assert: every chunk landed: all five sources updated, all three removals applied, and
        // the version still flips to processed after the chunked saves cleared the change tracker.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        summary.ShouldNotBeNull();
        summary.SourceChanged.ShouldBe(5);
        summary.Removed.ShouldBe(3);
        summary.Unchanged.ShouldBe(0);
        (await GetTranslationAsync(1))!.Source.Text.ShouldBe("A2");
        (await GetTranslationAsync(3))!.Source.Text.ShouldBe("C2");
        (await GetTranslationAsync(5))!.Source.Text.ShouldBe("E2");
        (await GetTranslationAsync(6))!.IsRemoved.ShouldBeTrue();
        (await GetTranslationAsync(8))!.IsRemoved.ShouldBeTrue();
        (await GetVersionStatusAsync(secondVersion)).ShouldBe(GameVersionStatus.Processed);
    }

    [Fact]
    public async Task Import_WithOctetStreamFilePart_ShouldStillSucceed()
    {
        // Arrange: the endpoint does not gate on the file part's declared MIME type; the `||` parser
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
        // Arrange: a host whose upload ceiling is tiny, so a small payload deterministically trips the
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

        // Assert: rejected at the transport layer before any row is written.
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await CountTranslationsAsync()).ShouldBe(0);
        (await GetVersionStatusAsync(versionId)).ShouldBe(GameVersionStatus.Unprocessed);
    }

    [Fact]
    public async Task Import_BodyWithinConfiguredUploadLimit_ShouldStillSucceed()
    {
        // Arrange: the same tiny ceiling, but a payload comfortably under it must still import: the
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
        // Arrange: a baseline the size of the whole catalog, where every row is new. Writing row by row
        // with EF took about 3 minutes for 700k rows (#214), and the COPY path (ADR-0011) has to load
        // hundreds of thousands of rows in seconds.
        // Spec 0004's original limit of 10 seconds was set before Translations gained the three
        // GameVersion pointer indexes and their foreign keys (#439). Each copied row now updates those
        // indexes and checks the keys, which pushed CI runners to around 10 seconds, measured at 10.3 to
        // 11.7 on the N-1 gate. 20 seconds gives room again and still clearly separates the bulk path
        // from a return to row-by-row writes, which by #214's measurement would take about 50 seconds at
        // this row count.
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
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Import_BulkCopyThenFailureBeforeCommit_ShouldRollBackTheCopiedRows()
    {
        // Arrange: the COPY runs on the write context's connection inside the import transaction, so
        // a failure anywhere in that transaction must discard the COPY'd rows too (spec 0001 all-or-
        // nothing). This pins the connection-enlistment the ADR-0011 atomicity design relies on:
        // COPY the rows successfully, then throw before the commit.
        GameVersionId versionId = await SeedVersionAsync("48.0");
        using IServiceScope scope = _factory.Services.CreateScope();
        IBulkTranslationInserter inserter = scope.ServiceProvider.GetRequiredService<IBulkTranslationInserter>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        List<Translation> rows = [NewUntranslated(10, "Ten", versionId), NewUntranslated(11, "Eleven", versionId)];
        InvalidOperationException induced = new("induced mid-transaction failure");

        // Act
        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
            {
                await inserter.InsertAsync(AsStream(rows), transactionToken);
                throw induced;
            }));

        // Assert: the transaction rolled back, so not one COPY'd row survived.
        thrown.ShouldBeSameAs(induced);
        (await CountTranslationsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Import_BaselineRowWithArguments_ShouldCopyArgsColumnsVerbatim()
    {
        // Arrange: a real exported.txt row carries the argument columns; the COPY writer's non-null
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
        // Arrange: the write context enables retry-on-failure. An interceptor makes the FIRST commit
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
        List<Translation> rows = [NewUntranslated(20, "Twenty", versionId)];

        // Act
        await dbContext.ExecuteInTransactionAsync(transactionToken => inserter.InsertAsync(AsStream(rows), transactionToken));

        // Assert: the transient commit fired and the strategy retried (self-check), and both halves
        // of the unit are durable: the COPY'd row AND the tracked version flip survived the retry.
        interceptor.CommitAttempts.ShouldBeGreaterThanOrEqualTo(2);
        (await CountTranslationsAsync()).ShouldBe(1);
        (await GetVersionStatusAsync(versionId)).ShouldBe(GameVersionStatus.Processed);
    }

    // Fails the first commit with a temporary serialization error (SQLSTATE 40001), which the Npgsql
    // retrying execution strategy retries, and lets every later commit through. That reproduces a
    // temporary fault arriving exactly at commit time.
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

    // The pointer has to reference a GameVersion row that exists, because the version pointer columns
    // carry foreign keys (#355), and in production copied rows always come from a real version anyway.
    private static Translation NewUntranslated(int gossipId, string text, GameVersionId versionId)
        => Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create(text, argsOrder: null, argsId: null).Value,
            versionId,
            DateTimeOffset.UtcNow).Value;

    // The inserter takes a stream (spec 0006); re-enumerating this iterator restarts it, matching
    // the retrying execution strategy's re-run semantics.
    private static async IAsyncEnumerable<Translation> AsStream(IReadOnlyList<Translation> translations)
    {
        await Task.Yield();
        foreach (Translation translation in translations)
        {
            yield return translation;
        }
    }

    private WebApplicationFactory<Program> WithUploadLimit(long maxUploadBytes) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configBuilder) =>
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Import:MaxUploadBytes"] = maxUploadBytes.ToString(CultureInfo.InvariantCulture)
                })));

    private WebApplicationFactory<Program> WithApplyChunkSize(int applyChunkSize) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configBuilder) =>
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Import:ApplyChunkSize"] = applyChunkSize.ToString(CultureInfo.InvariantCulture)
                })));

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.TryGetProperty("errorCode", out JsonElement code) ? code.GetString() : null;
    }

    [Fact]
    public async Task Import_SevenColumnExportWithMatchingDigests_ShouldImportExactlyLikeSixColumns()
    {
        // Arrange: the CLI's export carries the source_digest column since ADR-0047 §2; the TMS
        // verifies it and otherwise treats the line as it always did. Row 2 is uploaded six-column
        // in the same file to prove both widths coexist (older exports, hand-made files).
        GameVersionId versionId = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient();

        // Act
        HttpResponseMessage response = await client.PostAsync(
            ImportRoute(versionId),
            ExportContent(DigestedLine(1, "Alpha"), Line(2, "Beta")));
        ImportSummary? summary = await response.Content.ReadFromJsonAsync<ImportSummary>();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        summary.ShouldNotBeNull();
        summary.Added.ShouldBe(2);
        (await GetTranslationAsync(1))!.Source.Text.ShouldBe("Alpha");
        (await GetTranslationAsync(2))!.Source.Text.ShouldBe("Beta");
    }

    [Fact]
    public async Task Import_SevenColumnExportWithAWrongDigest_ShouldReturn422AndPersistNothing()
    {
        // Arrange: a digest that does not match its own row means the wrong file was uploaded, or the
        // two contexts compute the digest differently (ADR-0047 §2). It fails loudly at import time, for
        // the whole upload, like any line that cannot be parsed (ADR-0042), and never as `source moved`
        // on a player's machine.
        GameVersionId versionId = await SeedVersionAsync("48.0");
        using HttpClient client = AdminClient();

        // Act
        HttpResponseMessage response = await client.PostAsync(
            ImportRoute(versionId),
            ExportContent(DigestedLine(1, "Alpha"), WronglyDigestedLine(2, "Beta")));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorCodeAsync(response)).ShouldBe("Import.ParseFailed");
        (await CountTranslationsAsync()).ShouldBe(0);
        (await GetVersionStatusAsync(versionId)).ShouldBe(GameVersionStatus.Unprocessed);
    }

    private static string ImportRoute(GameVersionId versionId)
        => $"/api/v1/game-versions/{versionId.Value}/import";

    private static string Line(int gossipId, string text) => $"{FileId}||{gossipId}||{text}||NULL||NULL||1";

    // A seven-column export line as the CLI writes it since ADR-0047: the last field is the digest of
    // the row's own source triple, so the import can verify it against its own SourceHash.
    private static string DigestedLine(int gossipId, string text)
        => $"{Line(gossipId, text)}||{SourceHash.Compute(text, null, null).ToWireDigest()}";

    private static string WronglyDigestedLine(int gossipId, string text)
        => $"{Line(gossipId, text)}||{SourceHash.Compute(text + " (not this)", null, null).ToWireDigest()}";

    // The exporter emits identity args from the fragment's argument count (args_order == args_id),
    // for the pristine English and for a patched fragment alike (spec 0012 echo triple).
    private static string LineWithArgs(int gossipId, string text, string args) => $"{FileId}||{gossipId}||{text}||{args}||{args}||1";

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

    private Task<GameVersionId> SeedVersionAsync(string version) => SeedVersionAsync(version, DateTimeOffset.UtcNow);

    private async Task<GameVersionId> SeedVersionAsync(string version, DateTimeOffset detectedAt)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create(version).Value, detectedAt).Value;
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

    private async Task ApprovePolishAsync(int gossipId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        Translator approver = await dbContext.Translators.FirstAsync();
        Translation translation = await dbContext.Translations
            .SingleAsync(row => row.FragmentKey.FileId == FileId && row.FragmentKey.GossipId == gossipId);
        translation.Approve(approver.Id, DateTimeOffset.UtcNow);
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
