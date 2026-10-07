using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using LotroKoniecDev.Application.Features.TranslationFileSyncing;
using LotroKoniecDev.Application.Parsers;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.TranslationFiles;

[Collection("TranslationApi")]
public sealed class GetTranslationFileTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private const string Route = "/api/v1/translation-files/pl";
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;
    private TranslatorId _submitterId;

    public GetTranslationFileTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"TranslationArtifacts\", translation.\"Translators\" CASCADE;");

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create("48.0").Value, Now).Value;
        dbContext.GameVersions.Add(gameVersion);

        Translator submitter = Translator.Create(
            IdentityId.Create(), DisplayName.Create("Seed Author").Value, email: null, Now).Value;
        dbContext.Translators.Add(submitter);

        await dbContext.SaveChangesAsync();
        _versionId = gameVersion.Id;
        _submitterId = submitter.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Get_AfterRebuild_ShouldReturn200WithApprovedNonRemovedRowsOnly()
    {
        // Arrange: only the approved, non-removed row belongs in the distributed file.
        await SeedAsync(gossipId: 1, polish: "Alfa", status: SeedStatus.Approved);
        await SeedAsync(gossipId: 2, polish: "Beta", status: SeedStatus.Draft);
        await SeedAsync(gossipId: 3, polish: "Gamma", status: SeedStatus.NeedsReview);
        await SeedAsync(gossipId: 4, polish: "Delta", status: SeedStatus.ApprovedThenRemoved);
        await RebuildAsync();

        // Act
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(Route);
        string body = await response.Content.ReadAsStringAsync();

        // Assert: Cache-Control must be the endpoint's revalidation pair, not the no-store
        // stamp GlobalNoCacheMiddleware applies when an endpoint sets nothing.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldNotBeNull();
        response.Headers.CacheControl.ShouldNotBeNull();
        response.Headers.CacheControl.Private.ShouldBeTrue();
        response.Headers.CacheControl.NoCache.ShouldBeTrue();
        response.Headers.CacheControl.NoStore.ShouldBeFalse();
        body.ShouldContain($"{FileId}||1||Alfa||NULL||NULL||1");
        body.ShouldNotContain($"{FileId}||2||");
        body.ShouldNotContain($"{FileId}||3||");
        body.ShouldNotContain($"{FileId}||4||");
    }

    [Fact]
    public async Task Get_WithMatchingIfNoneMatch_ShouldReturn304()
    {
        // Arrange
        await SeedAsync(gossipId: 1, polish: "Alfa", status: SeedStatus.Approved);
        await RebuildAsync();
        HttpResponseMessage first = await _factory.CreateClient().GetAsync(Route);
        EntityTagHeaderValue etag = first.Headers.ETag!;

        // Act: re-request with the ETag the client already holds.
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.IfNoneMatch.Add(etag);
        HttpResponseMessage second = await client.GetAsync(Route);

        // Assert: a 304 must re-state the current validator (RFC 9110 §15.4.5) and the
        // revalidation Cache-Control, or intermediaries would evict the cached artifact.
        second.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await second.Content.ReadAsStringAsync()).ShouldBeEmpty();
        second.Headers.ETag.ShouldBe(etag);
        second.Headers.CacheControl.ShouldNotBeNull();
        second.Headers.CacheControl.Private.ShouldBeTrue();
        second.Headers.CacheControl.NoCache.ShouldBeTrue();
        second.Headers.CacheControl.NoStore.ShouldBeFalse();
    }

    [Fact]
    public async Task Get_WithIfNoneMatchListContainingTheETag_ShouldReturn304()
    {
        // Arrange: If-None-Match is a list (RFC 9110); a stale tag alongside the current one still 304s.
        await SeedAsync(gossipId: 1, polish: "Alfa", status: SeedStatus.Approved);
        await RebuildAsync();
        EntityTagHeaderValue etag = (await _factory.CreateClient().GetAsync(Route)).Headers.ETag!;

        // Act
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.IfNoneMatch.Add(new EntityTagHeaderValue("\"stale-tag\""));
        client.DefaultRequestHeaders.IfNoneMatch.Add(etag);
        HttpResponseMessage response = await client.GetAsync(Route);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task Get_WithStaleIfNoneMatch_ShouldReturn200WithCurrentContentAndETag()
    {
        // Arrange
        await SeedAsync(gossipId: 1, polish: "Alfa", status: SeedStatus.Approved);
        await RebuildAsync();
        EntityTagHeaderValue currentEtag = (await _factory.CreateClient().GetAsync(Route)).Headers.ETag!;

        // Act: the canonical CLI update download: the held validator no longer matches.
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.IfNoneMatch.Add(new EntityTagHeaderValue("\"stale-tag\""));
        HttpResponseMessage response = await client.GetAsync(Route);
        string body = await response.Content.ReadAsStringAsync();

        // Assert: full body plus the current validator, so the client re-syncs in one round-trip.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldBe(currentEtag);
        body.ShouldContain($"{FileId}||1||Alfa||NULL||NULL||1");
    }

    [Fact]
    public async Task Get_WithWildcardIfNoneMatch_ShouldReturn304()
    {
        // Arrange
        await SeedAsync(gossipId: 1, polish: "Alfa", status: SeedStatus.Approved);
        await RebuildAsync();

        // Act: "*" matches any current representation (RFC 9110 §13.1.2).
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        HttpResponseMessage response = await client.GetAsync(Route);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task Get_WithMatchingIfNoneMatch_ShouldNotQueryContentColumn()
    {
        // Arrange
        await SeedAsync(gossipId: 1, polish: "Alfa", status: SeedStatus.Approved);
        await RebuildAsync();
        EntityTagHeaderValue etag = (await _factory.CreateClient().GetAsync(Route)).Headers.ETag!;

        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.IfNoneMatch.Add(etag);
        _factory.ReadContextSqlRecorder.Clear();

        // Act
        HttpResponseMessage response = await client.GetAsync(Route);

        // Assert: the revalidation's cost model (PERF-01/#286): the hash column is read, the
        // multi-MB Content column is not. The quoted-identifier match is exact on purpose:
        // "ContentHash" contains the bare substring, so only "Content" with its closing quote
        // proves the column itself was fetched.
        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        IReadOnlyList<string> commands = _factory.ReadContextSqlRecorder.Commands;
        commands.ShouldContain(command => command.Contains("\"ContentHash\""));
        commands.ShouldAllBe(command => !command.Contains("\"Content\""));
    }

    [Fact]
    public async Task Get_WithoutToken_ShouldBeAnonymousAndReturn200()
    {
        // Arrange
        await SeedAsync(gossipId: 1, polish: "Alfa", status: SeedStatus.Approved);
        await RebuildAsync();

        // Act: no Authorization header at all.
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(Route);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get_WithUnsupportedLanguage_ShouldReturn400()
    {
        // Act
        HttpResponseMessage response = await _factory.CreateClient().GetAsync("/api/v1/translation-files/de");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_WhenNoArtifactBuilt_ShouldReturn404()
    {
        // Act: nothing imported or built yet.
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(Route);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_ETagIsTheSha256OfTheBody_SoThePatcherIntegrityCheckAcceptsIt()
    {
        // Arrange
        await SeedAsync(gossipId: 1, polish: "Zażółć gęślą jaźń", status: SeedStatus.Approved);
        await RebuildAsync();

        // Act
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(Route);
        string body = await response.Content.ReadAsStringAsync();

        // Assert: the patcher rejects any download whose body does not hash-match the ETag
        // (AUDIT-SEC-01/#391), so the strong ETag must stay the hex SHA-256 of the UTF-8 body with
        // nothing (e.g. a BOM) added in transit. Verified with the patcher's own integrity check.
        response.Headers.ETag!.IsWeak.ShouldBeFalse();
        response.Headers.ETag.Tag.ShouldBe($"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}\"");
        TranslationFileContentIntegrity.Matches(body, response.Headers.ETag.ToString()).ShouldBeTrue();
    }

    [Fact]
    public async Task Get_FullDownload_ShouldDeclareUtf8PlainText()
    {
        // Arrange
        await SeedAsync(gossipId: 1, polish: "Alfa", status: SeedStatus.Approved);
        await RebuildAsync();

        // Act
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(Route);

        // Assert: the CLI decodes the body by this charset, so it must survive the switch from a
        // string body to a streamed file.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType.ShouldNotBeNull();
        response.Content.Headers.ContentType.MediaType.ShouldBe("text/plain");
        response.Content.Headers.ContentType.CharSet.ShouldBe("utf-8");
    }

    [Fact]
    public async Task Get_SecondFullDownload_ShouldServeTheDiskCopyWithoutReadingTheContentColumn()
    {
        // Arrange: the first full download writes the disk copy.
        await SeedAsync(gossipId: 1, polish: "Alfa", status: SeedStatus.Approved);
        await RebuildAsync();
        EntityTagHeaderValue etag = (await _factory.CreateClient().GetAsync(Route)).Headers.ETag!;

        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.IfNoneMatch.Add(new EntityTagHeaderValue("\"stale-tag\""));
        _factory.ReadContextSqlRecorder.Clear();

        // Act
        HttpResponseMessage response = await client.GetAsync(Route);
        string body = await response.Content.ReadAsStringAsync();

        // Assert: PERF-09/#715. A stale client gets the full body without the multi-MB column being
        // read from the database again.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldBe(etag);
        body.ShouldContain($"{FileId}||1||Alfa||NULL||NULL||1");
        IReadOnlyList<string> commands = _factory.ReadContextSqlRecorder.Commands;
        commands.ShouldContain(command => command.Contains("\"ContentHash\""));
        commands.ShouldAllBe(command => !command.Contains("\"Content\""));
    }

    [Fact]
    public async Task Get_BurstOfConcurrentMisses_ShouldReadTheContentColumnOnceAndServeOneBody()
    {
        // Arrange: a fresh artifact and no disk copy yet, the update-day shape. Every client's ETag
        // went stale at once.
        const int burstSize = 24;
        await SeedAsync(gossipId: 1, polish: "Alfa", status: SeedStatus.Approved);
        await SeedAsync(gossipId: 2, polish: "Beta", status: SeedStatus.Approved);
        await RebuildAsync();
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.IfNoneMatch.Add(new EntityTagHeaderValue("\"stale-tag\""));
        _factory.ReadContextSqlRecorder.Clear();

        // Act
        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, burstSize).Select(_ => client.GetAsync(Route)));
        string[] bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));

        // Assert: the content is loaded from the database once for the whole burst, so the API's
        // memory does not grow with the number of players. Every one of them gets the same body under
        // a tag the patcher accepts.
        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        responses.Select(response => response.Headers.ETag!.Tag).Distinct().Count().ShouldBe(1);
        bodies.Distinct().Count().ShouldBe(1);
        TranslationFileContentIntegrity.Matches(bodies[0], responses[0].Headers.ETag!.ToString()).ShouldBeTrue();
        _factory.ReadContextSqlRecorder.Commands
            .Count(command => command.Contains("\"Content\""))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Get_FromTheDiskCopy_ShouldStillPassThePatcherIntegrityCheck()
    {
        // Arrange: Polish letters and a surrogate pair, where a different encoder would change the
        // bytes. The first download writes the copy and the second one reads it back.
        await SeedAsync(gossipId: 1, polish: "Zażółć gęślą jaźń 🐉", status: SeedStatus.Approved);
        await RebuildAsync();
        await _factory.CreateClient().GetAsync(Route);

        // Act
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(Route);
        string body = await response.Content.ReadAsStringAsync();

        // Assert: same check as the patcher runs on a download (AUDIT-SEC-01, #391).
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain("Zażółć gęślą jaźń 🐉");
        response.Headers.ETag!.IsWeak.ShouldBeFalse();
        TranslationFileContentIntegrity.Matches(body, response.Headers.ETag.ToString()).ShouldBeTrue();
    }

    [Fact]
    public async Task Get_FromTheDiskCopy_ShouldSendTheExactUtf8BytesTheETagHashes()
    {
        // Arrange: reading the body as a string would hide a BOM, so this test reads the raw bytes.
        await SeedAsync(gossipId: 1, polish: "Zażółć gęślą jaźń 🐉", status: SeedStatus.Approved);
        await RebuildAsync();
        await _factory.CreateClient().GetAsync(Route);

        // Act
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(Route);
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.AsSpan().StartsWith(Encoding.UTF8.Preamble).ShouldBeFalse();
        response.Content.Headers.ContentLength.ShouldBe(body.Length);
        response.Headers.ETag!.Tag.ShouldBe($"\"{Convert.ToHexString(SHA256.HashData(body))}\"");
    }

    [Fact]
    public async Task Get_WhenTheStoredHashIsMalformed_ShouldAnswer500NotBlameTheClient()
    {
        // Arrange: a fault in our own data, not in the request.
        await SeedAsync(gossipId: 1, polish: "Alfa", status: SeedStatus.Approved);
        await RebuildAsync();
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
            await dbContext.Database.ExecuteSqlAsync(
                $"UPDATE translation.\"TranslationArtifacts\" SET \"ContentHash\" = {"not-a-content-hash"}");
        }

        // Act
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(Route);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Get_WhenTheDiskCopyWasDeleted_ShouldWriteItAgainFromTheDatabase()
    {
        // Arrange: something cleared the temp folder after the copy was written.
        await SeedAsync(gossipId: 1, polish: "Alfa", status: SeedStatus.Approved);
        await RebuildAsync();
        HttpResponseMessage first = await _factory.CreateClient().GetAsync(Route);
        string firstBody = await first.Content.ReadAsStringAsync();
        _factory.DeleteTranslationFileCopies();
        _factory.ReadContextSqlRecorder.Clear();

        // Act
        HttpResponseMessage second = await _factory.CreateClient().GetAsync(Route);
        string secondBody = await second.Content.ReadAsStringAsync();

        // Assert
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Headers.ETag.ShouldBe(first.Headers.ETag);
        secondBody.ShouldBe(firstBody);
        _factory.ReadContextSqlRecorder.Commands
            .Count(command => command.Contains("\"Content\""))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Get_AfterARebuild_ShouldKeepOnlyTheNewCopyOnDisk()
    {
        // Arrange: at target scale every copy is tens of MB, so old ones must not pile up per rebuild.
        await SeedAsync(gossipId: 1, polish: "Alfa", status: SeedStatus.Approved);
        await RebuildAsync();
        await _factory.CreateClient().GetAsync(Route);
        await SeedAsync(gossipId: 2, polish: "Beta", status: SeedStatus.Approved);
        await RebuildAsync();

        // Act
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(Route);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string currentHash = response.Headers.ETag!.Tag.Trim('"');
        string[] copies = Directory.GetFiles(_factory.TranslationFileCopiesDirectory);
        copies.Length.ShouldBe(1);
        Path.GetFileName(copies[0]).ShouldContain(currentHash);
    }

    [Fact]
    public async Task Get_AfterApprovingAnotherRow_ShouldAppearInNextDownloadWithNewETag()
    {
        // Arrange: first artifact has only Alfa.
        await SeedAsync(gossipId: 1, polish: "Alfa", status: SeedStatus.Approved);
        await RebuildAsync();
        HttpResponseMessage firstDownload = await _factory.CreateClient().GetAsync(Route);
        EntityTagHeaderValue firstEtag = firstDownload.Headers.ETag!;

        // Act: approve a second row (no game-version bump) and rebuild.
        await SeedAsync(gossipId: 2, polish: "Beta", status: SeedStatus.Approved);
        await RebuildAsync();
        HttpResponseMessage secondDownload = await _factory.CreateClient().GetAsync(Route);
        string body = await secondDownload.Content.ReadAsStringAsync();

        // Assert
        secondDownload.Headers.ETag.ShouldNotBe(firstEtag);
        body.ShouldContain($"{FileId}||1||Alfa||NULL||NULL||1");
        body.ShouldContain($"{FileId}||2||Beta||NULL||NULL||1");
    }

    [Fact]
    public async Task RoundTrip_ImportApproveDownload_ShouldParseIdenticallyWithThePatcher()
    {
        // Arrange: import the English baseline (admin), then approve Polish for both rows.
        using HttpClient admin = AdminClient();
        await admin.PostAsync(
            $"/api/v1/game-versions/{_versionId.Value}/import",
            ExportContent(
                $"{FileId}||1||English source one||1-2||3-4||1",
                $"{FileId}||2||English with || inside||NULL||NULL||1"));
        await ApprovePolishAsync(gossipId: 1, polish: "Polski jeden");
        await ApprovePolishAsync(gossipId: 2, polish: "Polski || dwa");
        await RebuildAsync();

        // Act: download, write to a temp file, and parse it with the patcher's own parser.
        string body = await (await _factory.CreateClient().GetAsync(Route)).Content.ReadAsStringAsync();
        string tempFile = Path.Combine(Path.GetTempPath(), $"polish_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempFile, body);

        try
        {
            IReadOnlyList<LotroKoniecDev.Domain.Models.Translation> parsed =
                new TranslationFileParser().ParseFile(tempFile).Value.Translations;

            // Assert: both rows round-trip: args preserved, || anchoring preserved, all approved.
            parsed.Count.ShouldBe(2);

            LotroKoniecDev.Domain.Models.Translation one = parsed.Single(translation => translation.GossipId == 1);
            one.Content.ShouldBe("Polski jeden");
            one.ArgsOrder.ShouldBe([0, 1]);
            one.ArgsId.ShouldBe([2, 3]);
            one.IsApproved.ShouldBeTrue();

            LotroKoniecDev.Domain.Models.Translation two = parsed.Single(translation => translation.GossipId == 2);
            two.Content.ShouldBe("Polski || dwa");
            two.ArgsOrder.ShouldBeNull();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private enum SeedStatus
    {
        Approved,
        Draft,
        NeedsReview,
        ApprovedThenRemoved,
    }

    private async Task SeedAsync(int gossipId, string polish, SeedStatus status)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create("English", null, null).Value,
            _versionId,
            Now).Value;

        switch (status)
        {
            case SeedStatus.Draft:
                row.ProvideTranslation(polish, _submitterId, Now);
                break;
            case SeedStatus.NeedsReview:
                row.ProvideTranslation(polish, _submitterId, Now);
                row.ApplySourceChange(TranslationSource.Create("English reworded", null, null).Value, _versionId, Now);
                break;
            case SeedStatus.Approved:
                row.ProvideTranslation(polish, _submitterId, Now);
                row.Approve(_submitterId, Now);
                break;
            case SeedStatus.ApprovedThenRemoved:
                row.ProvideTranslation(polish, _submitterId, Now);
                row.Approve(_submitterId, Now);
                row.MarkRemoved(_versionId, Now);
                break;
        }

        dbContext.Translations.Add(row);
        await dbContext.SaveChangesAsync();
    }

    private async Task ApprovePolishAsync(int gossipId, string polish)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translation row = await dbContext.Translations
            .SingleAsync(translation => translation.FragmentKey.FileId == FileId && translation.FragmentKey.GossipId == gossipId);
        row.ProvideTranslation(polish, _submitterId, Now);
        row.Approve(_submitterId, Now);
        await dbContext.SaveChangesAsync();
    }

    private async Task RebuildAsync()
    {
        IPrecomputedTranslationFileProjector builder = _factory.Services.GetRequiredService<IPrecomputedTranslationFileProjector>();
        await builder.RebuildAsync("pl", CancellationToken.None);
    }

    private HttpClient AdminClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Admin));
        return client;
    }

    private static MultipartFormDataContent ExportContent(params string[] lines)
    {
        ByteArrayContent fileContent = new(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        return new MultipartFormDataContent { { fileContent, "file", "exported.txt" } };
    }
}
