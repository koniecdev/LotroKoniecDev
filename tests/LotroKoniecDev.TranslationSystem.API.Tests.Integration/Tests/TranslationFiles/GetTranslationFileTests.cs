using System.Net.Http.Headers;
using System.Text;
using LotroKoniecDev.Application.Parsers;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.TranslationFiles;

[Collection("TranslationApi")]
public sealed class GetTranslationFileTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private const string Route = "/api/v1/translation-files/pl";
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly IdentityId Submitter = IdentityId.Create();

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;

    public GetTranslationFileTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"TranslationArtifacts\" CASCADE;");

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create("48.0").Value, Now).Value;
        dbContext.GameVersions.Add(gameVersion);
        await dbContext.SaveChangesAsync();
        _versionId = gameVersion.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Get_AfterRebuild_ShouldReturn200WithApprovedNonRemovedRowsOnly()
    {
        // Arrange — only the approved, non-removed row belongs in the distributed file.
        await SeedAsync(gossipId: 1, polish: "Alfa", status: SeedStatus.Approved);
        await SeedAsync(gossipId: 2, polish: "Beta", status: SeedStatus.Draft);
        await SeedAsync(gossipId: 3, polish: "Gamma", status: SeedStatus.NeedsReview);
        await SeedAsync(gossipId: 4, polish: "Delta", status: SeedStatus.ApprovedThenRemoved);
        await RebuildAsync();

        // Act
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(Route);
        string body = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldNotBeNull();
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

        // Act — re-request with the ETag the client already holds.
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.IfNoneMatch.Add(etag);
        HttpResponseMessage second = await client.GetAsync(Route);

        // Assert
        second.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await second.Content.ReadAsStringAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_WithIfNoneMatchListContainingTheETag_ShouldReturn304()
    {
        // Arrange — If-None-Match is a list (RFC 9110); a stale tag alongside the current one still 304s.
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
    public async Task Get_WithoutToken_ShouldBeAnonymousAndReturn200()
    {
        // Arrange
        await SeedAsync(gossipId: 1, polish: "Alfa", status: SeedStatus.Approved);
        await RebuildAsync();

        // Act — no Authorization header at all.
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
        // Act — nothing imported or built yet.
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(Route);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_AfterApprovingAnotherRow_ShouldAppearInNextDownloadWithNewETag()
    {
        // Arrange — first artifact has only Alfa.
        await SeedAsync(gossipId: 1, polish: "Alfa", status: SeedStatus.Approved);
        await RebuildAsync();
        HttpResponseMessage firstDownload = await _factory.CreateClient().GetAsync(Route);
        EntityTagHeaderValue firstEtag = firstDownload.Headers.ETag!;

        // Act — approve a second row (no game-version bump) and rebuild.
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
        // Arrange — import the English baseline (admin), then approve Polish for both rows.
        using HttpClient admin = AdminClient();
        await admin.PostAsync(
            $"/api/v1/game-versions/{_versionId.Value}/import",
            ExportContent(
                $"{FileId}||1||English source one||1-2||3-4||1",
                $"{FileId}||2||English with || inside||NULL||NULL||1"));
        await ApprovePolishAsync(gossipId: 1, polish: "Polski jeden");
        await ApprovePolishAsync(gossipId: 2, polish: "Polski || dwa");
        await RebuildAsync();

        // Act — download, write to a temp file, and parse it with the patcher's own parser.
        string body = await (await _factory.CreateClient().GetAsync(Route)).Content.ReadAsStringAsync();
        string tempFile = Path.Combine(Path.GetTempPath(), $"polish_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempFile, body);

        try
        {
            IReadOnlyList<LotroKoniecDev.Domain.Models.Translation> parsed =
                new TranslationFileParser().ParseFile(tempFile).Value;

            // Assert — both rows round-trip: args preserved, || anchoring preserved, all approved.
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
                row.ProvideTranslation(polish, Submitter, Now);
                break;
            case SeedStatus.NeedsReview:
                row.ProvideTranslation(polish, Submitter, Now);
                row.ApplySourceChange(TranslationSource.Create("English reworded", null, null).Value, _versionId, Now);
                break;
            case SeedStatus.Approved:
                row.ProvideTranslation(polish, Submitter, Now);
                row.Approve(Now);
                break;
            case SeedStatus.ApprovedThenRemoved:
                row.ProvideTranslation(polish, Submitter, Now);
                row.Approve(Now);
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
        row.ProvideTranslation(polish, Submitter, Now);
        row.Approve(Now);
        await dbContext.SaveChangesAsync();
    }

    private async Task RebuildAsync()
    {
        ITranslationArtifactBuilder builder = _factory.Services.GetRequiredService<ITranslationArtifactBuilder>();
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
