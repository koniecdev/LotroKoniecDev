using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.Application.Parsers;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.CoreLoop;

/// <summary>
/// Proves the M2 DoD end-to-end through the public HTTP endpoints only — no DB seed helpers for the
/// translation lifecycle — so the assembled slices (register version, import+diff, list/get, upsert,
/// approve, distribution) compose into the real admin/translator loop, and the downloaded file
/// round-trips through the patcher's own parser (the <c>||</c> cross-context contract guard). The
/// per-endpoint and per-field cases live in the sibling suites; this suite owns the integrated flow.
/// </summary>
[Collection("TranslationApi")]
public sealed class CoreLoopTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private const string TranslationsRoute = "/api/v1/translations";
    private const string FileRoute = "/api/v1/translation-files/pl";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;

    public CoreLoopTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"TranslationArtifacts\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CoreLoop_RegisterImportEditApproveDownload_FlowsThroughEndpointsParsesWithPatcherAndCaches()
    {
        using HttpClient admin = AdminClient();
        using HttpClient translator = TranslatorClient();

        // Register a game version, then import the English baseline against it (admin).
        Guid versionId = await RegisterVersionAsync(admin, "48.0");
        ImportSummary import = await ImportAsync(admin, versionId, Line(1, "English one"), Line(2, "English two"));
        import.Added.ShouldBe(2);

        // The catalog now lists two untranslated rows (translator).
        PaginationResponse<TranslationListItemResponse>? list = await (await translator.GetAsync(TranslationsRoute))
            .Content.ReadFromJsonAsync<PaginationResponse<TranslationListItemResponse>>(JsonOptions);
        list.ShouldNotBeNull();
        list.TotalCount.ShouldBe(2);
        list.Items.ShouldAllBe(item => item.Status == TranslationStatus.Untranslated);

        // Translate row 1 (translator) -> Draft, then approve it (admin) -> published.
        TranslationDetailResponse edited = await UpsertAsync(translator, gossipId: 1, polish: "Polski jeden");
        edited.Status.ShouldBe(TranslationStatus.Draft);

        HttpResponseMessage approve = await admin.PostAsync(ApproveRoute(edited.Id.Value), null);
        approve.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Download the distributed file (anonymous): the approved row is in, the untranslated one is out.
        HttpResponseMessage download = await _factory.CreateClient().GetAsync(FileRoute);
        string file = await download.Content.ReadAsStringAsync();
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        download.Headers.ETag.ShouldNotBeNull();
        EntityTagHeaderValue etag = download.Headers.ETag!;
        file.ShouldContain($"{FileId}||1||Polski jeden||NULL||NULL||1");
        file.ShouldNotContain($"{FileId}||2||");

        // The file round-trips through the patcher's own parser (the || contract drift guard).
        string tempFile = Path.Combine(Path.GetTempPath(), $"polish_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempFile, file);
        try
        {
            IReadOnlyList<LotroKoniecDev.Domain.Models.Translation> parsed =
                new TranslationFileParser().ParseFile(tempFile).Value;

            LotroKoniecDev.Domain.Models.Translation only = parsed.ShouldHaveSingleItem();
            only.FileId.ShouldBe(FileId);
            ((long)only.GossipId).ShouldBe(1L);
            only.Content.ShouldBe("Polski jeden");
            only.IsApproved.ShouldBeTrue();
        }
        finally
        {
            File.Delete(tempFile);
        }

        // The download is cacheable: a conditional re-request with the held ETag is a 304.
        using HttpClient conditional = _factory.CreateClient();
        conditional.DefaultRequestHeaders.IfNoneMatch.Add(etag);
        (await conditional.GetAsync(FileRoute)).StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task UpdateCycle_SecondImportRewordsApprovedRow_InvalidatesAndDropsItFromDownload()
    {
        using HttpClient admin = AdminClient();
        using HttpClient translator = TranslatorClient();

        // Baseline: import, translate + approve row 1, so it is in the distributed file.
        Guid firstVersion = await RegisterVersionAsync(admin, "48.0");
        await ImportAsync(admin, firstVersion, Line(1, "English one"), Line(2, "English two"));
        TranslationDetailResponse edited = await UpsertAsync(translator, gossipId: 1, polish: "Polski jeden");
        await admin.PostAsync(ApproveRoute(edited.Id.Value), null);

        HttpResponseMessage firstDownload = await _factory.CreateClient().GetAsync(FileRoute);
        (await firstDownload.Content.ReadAsStringAsync()).ShouldContain($"{FileId}||1||Polski jeden||NULL||NULL||1");
        firstDownload.Headers.ETag.ShouldNotBeNull();
        EntityTagHeaderValue firstEtag = firstDownload.Headers.ETag!;

        // A game update reword's row 1's English source on the next version's import.
        Guid secondVersion = await RegisterVersionAsync(admin, "48.1");
        ImportSummary update = await ImportAsync(admin, secondVersion, Line(1, "English one reworded"), Line(2, "English two"));
        update.SourceChanged.ShouldBe(1);
        update.Invalidated.ShouldBe(1);
        update.Unchanged.ShouldBe(1);

        // Row 1 is now NeedsReview with its superseded English kept for side-by-side review.
        TranslationDetailResponse? row1 = await (await translator.GetAsync($"{TranslationsRoute}/{edited.Id.Value}"))
            .Content.ReadFromJsonAsync<TranslationDetailResponse>(JsonOptions);
        row1.ShouldNotBeNull();
        row1.Status.ShouldBe(TranslationStatus.NeedsReview);
        row1.PreviousSourceText.ShouldBe("English one");

        // The invalidated row drops out of the freshly regenerated distributed file (new ETag).
        HttpResponseMessage secondDownload = await _factory.CreateClient().GetAsync(FileRoute);
        secondDownload.Headers.ETag.ShouldNotBe(firstEtag);
        (await secondDownload.Content.ReadAsStringAsync()).ShouldNotContain($"{FileId}||1||");
    }

    private static string ApproveRoute(Guid translationId) => $"{TranslationsRoute}/{translationId}/approve";

    private static string Line(int gossipId, string text) => $"{FileId}||{gossipId}||{text}||NULL||NULL||1";

    private async Task<Guid> RegisterVersionAsync(HttpClient admin, string version)
    {
        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/v1/game-versions", new RegisterGameVersionRequest(version));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        GameVersionResponse? body = await response.Content.ReadFromJsonAsync<GameVersionResponse>(JsonOptions);
        body.ShouldNotBeNull();
        return body.Id.Value;
    }

    private async Task<ImportSummary> ImportAsync(HttpClient admin, Guid versionId, params string[] lines)
    {
        ByteArrayContent fileContent = new(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        using MultipartFormDataContent form = new() { { fileContent, "file", "exported.txt" } };

        HttpResponseMessage response = await admin.PostAsync($"/api/v1/game-versions/{versionId}/import", form);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ImportSummary? summary = await response.Content.ReadFromJsonAsync<ImportSummary>(JsonOptions);
        summary.ShouldNotBeNull();
        return summary;
    }

    private async Task<TranslationDetailResponse> UpsertAsync(HttpClient translator, int gossipId, string polish)
    {
        HttpResponseMessage response = await translator.PutAsJsonAsync(
            TranslationsRoute, new UpsertTranslationRequest(FileId, gossipId, polish));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        TranslationDetailResponse? body = await response.Content.ReadFromJsonAsync<TranslationDetailResponse>(JsonOptions);
        body.ShouldNotBeNull();
        return body;
    }

    private HttpClient AdminClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Admin));
        return client;
    }

    private HttpClient TranslatorClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator));
        return client;
    }
}
