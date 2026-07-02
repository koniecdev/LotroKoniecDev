using System.Net;
using System.Net.Http.Headers;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.E2E.Tests.Clients;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Shouldly;
using Xunit.Abstractions;

namespace LotroKoniecDev.TranslationSystem.E2E.Tests.Tests;

/// <summary>
/// Drives the whole M2 core loop through the public HTTP endpoints of the containerised tms-api — register a
/// game version, import the English baseline, list, translate, approve, distribute — with a real Admin token
/// from auth-api (Admin also satisfies the translator policy), proving the assembled slices compose end-to-end
/// over the network and that the distributed file is ETag-cacheable.
/// </summary>
public sealed class CoreLoopE2ETests : E2ETestBase
{
    private const int FileId = 620_756_992;
    private const string Language = "pl";

    private readonly ITestOutputHelper _output;

    public CoreLoopE2ETests(E2ETestFixture fixture, ITestOutputHelper output) : base(fixture)
    {
        _output = output;
    }

    [Fact]
    public async Task CoreLoop_RegisterImportEditApproveDownload_FlowsThroughEndpointsAndCaches()
    {
        try
        {
            string adminToken = await LoginAsAdminAsync();
            TranslationSystemApiClient admin = CreateTmsClient(adminToken);
            TranslationSystemApiClient anonymous = CreateTmsClient();

            // Register a game version, then import the English baseline against it.
            GameVersionResponse version = await admin.RegisterGameVersionAsync("48.0");
            ImportSummary import = await admin.ImportAsync(version.Id.Value, Line(1, "English one"), Line(2, "English two"));
            import.Added.ShouldBe(2);

            // The catalog now lists two untranslated rows.
            PaginationResponse<TranslationListItemResponse> list = await admin.ListTranslationsAsync();
            list.TotalCount.ShouldBe(2);
            list.Items.ShouldAllBe(item => item.Status == TranslationStatus.Untranslated);

            // Translate row 1 -> Draft (lazily provisioning the translator), then approve it -> published.
            TranslationDetailResponse edited = await admin.UpsertAsync(FileId, gossipId: 1, translatedText: "Polski jeden");
            edited.Status.ShouldBe(TranslationStatus.Draft);
            edited.Submitter.ShouldNotBeNull();

            HttpResponseMessage approve = await admin.ApproveRawAsync(edited.Id.Value);
            approve.StatusCode.ShouldBe(HttpStatusCode.NoContent);

            // Download the distributed file (anonymous): the approved row is in, the untranslated one
            // is out. The artifact is rebuilt in the background, debounced (PERF-04) — poll until the
            // approve's rebuild has converged.
            (HttpResponseMessage download, string file) = await TranslationFileDownloadPolling.DownloadWhenConvergedAsync(
                anonymous,
                Language,
                (candidate, content) => candidate.IsSuccessStatusCode && content.Contains($"{FileId}||1||Polski jeden||NULL||NULL||1"));
            download.StatusCode.ShouldBe(HttpStatusCode.OK);
            download.Headers.ETag.ShouldNotBeNull();
            EntityTagHeaderValue etag = download.Headers.ETag!;

            file.ShouldContain($"{FileId}||1||Polski jeden||NULL||NULL||1");
            file.ShouldNotContain($"{FileId}||2||");

            // The download is cacheable: a conditional re-request with the held ETag is a 304.
            HttpResponseMessage conditional = await anonymous.DownloadFileRawAsync(Language, etag);
            conditional.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        }
        catch (Exception)
        {
            _output.WriteLine(await Fixture.GetAuthApiLogsAsync());
            _output.WriteLine(await Fixture.GetTmsApiLogsAsync());
            throw;
        }
    }

    private static string Line(int gossipId, string text) => $"{FileId}||{gossipId}||{text}||NULL||NULL||1";
}
