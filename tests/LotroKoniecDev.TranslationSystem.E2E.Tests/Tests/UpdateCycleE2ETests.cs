using System.Net.Http.Headers;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.E2E.Tests.Clients;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Shouldly;
using Xunit.Abstractions;

namespace LotroKoniecDev.TranslationSystem.E2E.Tests.Tests;

/// <summary>
/// The M2 update lifecycle over real HTTP: a later version rewords an approved row, so the import
/// invalidates it and it leaves the file under a new ETag.
/// </summary>
public sealed class UpdateCycleE2ETests : E2ETestBase
{
    private const int FileId = 620_756_992;
    private const string Language = "pl";

    private readonly ITestOutputHelper _output;

    public UpdateCycleE2ETests(E2ETestFixture fixture, ITestOutputHelper output) : base(fixture)
    {
        _output = output;
    }

    [Fact]
    public async Task UpdateCycle_SecondImportRewordsApprovedRow_InvalidatesAndDropsItFromDownload()
    {
        try
        {
            string adminToken = await LoginAsAdminAsync();
            TranslationSystemApiClient admin = CreateTmsClient(adminToken);
            TranslationSystemApiClient anonymous = CreateTmsClient();

            // Baseline: import, then translate and approve row 1.
            GameVersionResponse firstVersion = await admin.RegisterGameVersionAsync("48.0");
            await admin.ImportAsync(firstVersion.Id.Value, Line(1, "English one"), Line(2, "English two"));
            TranslationDetailResponse edited = await admin.UpsertAsync(FileId, gossipId: 1, translatedText: "Polski jeden");
            await admin.ApproveRawAsync(edited.Id.Value);

            // The rebuild runs in the background (PERF-04), so poll.
            (HttpResponseMessage firstDownload, string firstFile) = await TranslationFileDownloadPolling.DownloadWhenConvergedAsync(
                anonymous,
                Language,
                (candidate, content) => candidate.IsSuccessStatusCode && content.Contains($"{FileId}||1||Polski jeden||NULL||NULL||1"));
            firstFile.ShouldContain($"{FileId}||1||Polski jeden||NULL||NULL||1");
            firstDownload.Headers.ETag.ShouldNotBeNull();
            EntityTagHeaderValue firstEtag = firstDownload.Headers.ETag!;

            // A game update rewords row 1's English.
            GameVersionResponse secondVersion = await admin.RegisterGameVersionAsync("48.1");
            ImportSummary update = await admin.ImportAsync(
                secondVersion.Id.Value, Line(1, "English one reworded"), Line(2, "English two"));
            update.SourceChanged.ShouldBe(1);
            update.Invalidated.ShouldBe(1);
            update.Unchanged.ShouldBe(1);

            // Row 1 is now NeedsReview with its old English kept.
            TranslationDetailResponse row1 = await admin.GetTranslationAsync(edited.Id.Value);
            row1.Status.ShouldBe(TranslationStatus.NeedsReview);
            row1.PreviousSourceText.ShouldBe("English one");

            // The invalidated row leaves the file once the rebuild converges.
            (HttpResponseMessage secondDownload, string secondFile) = await TranslationFileDownloadPolling.DownloadWhenConvergedAsync(
                anonymous,
                Language,
                (candidate, content) => candidate.IsSuccessStatusCode && !content.Contains($"{FileId}||1||"));
            secondDownload.Headers.ETag.ShouldNotBe(firstEtag);
            secondFile.ShouldNotContain($"{FileId}||1||");
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
