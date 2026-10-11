using AngleSharp.Dom;
using Bunit.TestDoubles;
using LotroKoniecDev.Frontend.Components.Pages.ImportExport;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using CompletedImport = LotroKoniecDev.Frontend.Components.Pages.ImportExport.ImportSummaryRedirect.CompletedImport;
using ImportExportComponent = LotroKoniecDev.Frontend.Components.Pages.ImportExport.ImportExport;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.ImportExport;

/// <summary>
/// Renders the <see cref="ImportExportComponent"/> through bUnit over a stubbed TMS client, to pin what
/// the loader tests cannot reach: the export download link is shown to every logged-in translator and
/// points at the server download route; the import panel appears only when the game-versions collection
/// carries the admin-only <c>register</c> rel (#158), never because of a role check in the page; the
/// version selector is filled from the listed versions; and every import input is named after the
/// <c>[SupplyParameterFromForm]</c> model path (<c>ImportInput.*</c>), which is the guard against the
/// M3-07 binding bug and what makes a fresh <c>exported.txt</c> bind on post.
/// Submitting with no file shows the validation notice, which proves the submit reaches the handler.
/// </summary>
public sealed class ImportExportTests : BunitContext
{
    private readonly ITranslationSystemClient _client = Substitute.For<ITranslationSystemClient>();

    public ImportExportTests()
    {
        Services.AddAntiforgery();
        Services.AddSingleton(_client);
        Services.AddSingleton(StubDiscoveryCache.AdvertisingGet(Rels.GameVersions, Rels.TranslationFile));
        Services.AddScoped<ImportExportLoader>();
        Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        Services.AddSingleton<ImportSummaryRedirect>();
    }

    [Fact]
    public void Render_WhenAuthenticated_OffersTheDownloadLinkTargetingTheServerDownloadRoute()
    {
        AuthorizeAs("Frodo");
        StubVersions();

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        IElement download = component.Find("a[download]");
        download.GetAttribute("href").ShouldBe("/download/polish.txt");
        download.GetAttribute("download").ShouldBe(ImportExportLoader.DownloadFileName);
    }

    [Fact]
    public void Render_WhenCollectionLacksTheRegisterRel_DoesNotShowTheImportPanel()
    {
        // A translator who is not an admin can list the versions, but the collection carries no
        // `register` rel, so the import panel stays hidden. The server decides that, not a role check
        // here.
        AuthorizeAs("Frodo");
        StubVersions(NonImportableVersion("48.0"));

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.FindAll("form").ShouldBeEmpty();
        component.FindAll("input[type=file]").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenCollectionHasTheRegisterRel_ShowsTheImportFormWithAVersionOptionPerListedVersion()
    {
        AuthorizeAs("Sam");
        StubVersionsWithRegisterRel(ImportableVersion("48.0"), ImportableVersion("47.2"));

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.FindAll("select[name=\"ImportInput.GameVersionId\"] option").Count.ShouldBe(2);
    }

    [Fact]
    public void Render_WhenCollectionHasTheRegisterRel_NamesEveryImportInputAfterTheFormModelPropertyPath()
    {
        // The guard against the SSR binding bug: Blazor's static SSR only binds an IFormFile as a
        // property of a [SupplyParameterFromForm] model, so the inputs have to be named ImportInput.*.
        // A plain name such as "UploadFile" binds null and the import can never run.
        AuthorizeAs("Sam");
        StubVersionsWithRegisterRel(ImportableVersion("48.0"));

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.Find("input[type=file]").GetAttribute("name").ShouldBe("ImportInput.File");
        component.Find("select").GetAttribute("name").ShouldBe("ImportInput.GameVersionId");
        component.Find("input[type=checkbox]").GetAttribute("name").ShouldBe("ImportInput.AllowMassRemoval");
    }

    [Fact]
    public async Task Submit_WhenNoFileChosen_ShowsTheChooseFileValidationAndDoesNotCallImport()
    {
        AuthorizeAs("Sam");
        StubVersionsWithRegisterRel(ImportableVersion("48.0"));
        IRenderedComponent<ImportExportComponent> component = RenderPage();

        await component.Find("form").SubmitAsync();

        component.Find(".status-down").TextContent.ShouldContain("Wybierz plik exported.txt");
        await _client.DidNotReceive().SendMultipartApiResultAsync<ImportSummary>(
            Arg.Any<HttpMethod>(),
            Arg.Any<string>(),
            Arg.Any<MultipartFormDataContent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Render_WhenCollectionHasTheRegisterRelButNoVersionsExist_ShowsTheEmptyStateInsteadOfTheForm()
    {
        AuthorizeAs("Sam");
        StubVersionsWithRegisterRel();

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.Find(".empty").TextContent.ShouldContain("Brak wersji gry");
        component.FindAll("input[type=file]").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenNoVersionAdvertisesTheImportRel_SaysSoInsteadOfOfferingAnUnusableSelector()
    {
        // An admin whose every registered version is Superseded: the API withholds `import` on all of
        // them, so a selector would offer only options the server would refuse on submit (#610).
        AuthorizeAs("Sam");
        StubVersionsWithRegisterRel(NonImportableVersion("47.2"), NonImportableVersion("46.0"));

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.Find(".empty").TextContent.ShouldContain("Brak wersji do importu");
        component.FindAll("input[type=file]").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenOnlySomeVersionsAdvertiseTheImportRel_OffersOnlyThose()
    {
        AuthorizeAs("Sam");
        StubVersionsWithRegisterRel(ImportableVersion("48.0"), NonImportableVersion("46.0"));

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        IReadOnlyList<IElement> options = component.FindAll("select[name=\"ImportInput.GameVersionId\"] option");
        options.ShouldHaveSingleItem().TextContent.ShouldContain("48.0");
    }

    [Fact]
    public void Render_WhenTheCollectionFailsToLoad_HidesTheImportPanelButSurfacesTheErrorAndKeepsTheDownload()
    {
        // A failed fetch means we cannot read the register rel, so the import panel is hidden. The error
        // is still shown, outside that check, so an admin sees what happened, and the export download,
        // which any translator may use, stays available.
        AuthorizeAs("Sam");
        _client
            .GetApiResultAsync<CollectionResponse<GameVersionResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<CollectionResponse<GameVersionResponse>>(new() { Title = "Nie udało się wczytać wersji." }));

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.FindAll("input[type=file]").ShouldBeEmpty();
        component.Find(".error-message").TextContent.ShouldContain("Nie udało się wczytać wersji.");
        component.Find("a[download]").GetAttribute("href").ShouldBe("/download/polish.txt");
    }

    [Fact]
    public void Render_WhenTheVersionsLoadFailsWithAnUnmappedCode_StillOffersTheTechnicalDetails()
    {
        // #703 hides the drawer for mapped codes, and this page is the one that must never lose it: the
        // import codes carry the line number and the removal counters. The exact import wording is
        // pinned on the component (ApiProblemAlertTests), which is the seam this page renders through;
        // this keeps a page-level pin that the block still reaches the screen at all.
        AuthorizeAs("Frodo");
        _client.GetApiResultAsync<CollectionResponse<GameVersionResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<CollectionResponse<GameVersionResponse>>(new()
            {
                Title = "Data Conflict",
                Detail = "Not your row.",
                Status = 422,
                Extensions = { ["errorCode"] = "Catalog.SomethingNobodyMappedYet" }
            }));

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.Find("details.problem-tech .problem-tech-body").TextContent
            .ShouldBe("Catalog.SomethingNobodyMappedYet — Not your row.");
    }

    [Fact]
    public void Render_WhenTheRedirectAfterAnImportCarriesItsSummary_ShowsTheSixCountsAndTheNotes()
    {
        // The GET side of the redirect after an import (#933): the list loads again in this new request,
        // and the summary of the import that sent the admin here is still on screen.
        AuthorizeAs("Sam");
        StubVersionsWithRegisterRel(ImportableVersion("48.0"));
        ImportSummary summary = new(
            Added: 11,
            SourceChanged: 12,
            Invalidated: 13,
            Removed: 14,
            Unchanged: 15,
            Echoed: 16,
            Warnings: ["1 older unprocessed version(s) marked superseded — they will never receive their own upload."]);
        NavigateToTheRedirectAfter(new CompletedImport(Guid.NewGuid(), summary));

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.Find(".status-ok").TextContent.ShouldContain("Import zakończony powodzeniem.");
        component.FindAll(".stat-tile .num").Select(tile => tile.TextContent)
            .ShouldBe(["11", "12", "13", "14", "15", "16"]);
        component.Find(".notice-list").TextContent.ShouldContain("1 older unprocessed version(s) marked superseded");
    }

    [Theory]
    [InlineData("/import-export?summary=forged")]
    [InlineData("/import-export?summary=")]
    [InlineData("/import-export")]
    public void Render_WhenTheAddressCarriesNoSummaryThisPageWrote_ShowsNoSummary(string uri)
    {
        // A link that someone else wrote must not put counts or notes on the page.
        AuthorizeAs("Sam");
        StubVersionsWithRegisterRel(ImportableVersion("48.0"));
        Navigation().NavigateTo(uri);

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.FindAll(".status-ok").ShouldBeEmpty();
        component.FindAll(".stat-tile").ShouldBeEmpty();
        component.FindAll("input[type=file]").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Submit_FromThePageARedirectLeftASummaryOn_ShowsOnlyTheNewOutcome()
    {
        // The form posts to the current address, which still carries the last import's summary (#933). A
        // refused second attempt must not show its error next to the old counts.
        AuthorizeAs("Sam");
        StubVersionsWithRegisterRel(ImportableVersion("48.0"));
        NavigateToTheRedirectAfter(new CompletedImport(Guid.NewGuid(), new ImportSummary(1, 2, 3, 4, 5, 6, ["Stara uwaga."])));
        IRenderedComponent<ImportExportComponent> component = RenderPage();

        await component.Find("form").SubmitAsync();

        component.Find(".status-down").TextContent.ShouldContain("Wybierz plik exported.txt");
        component.FindAll(".status-ok").ShouldBeEmpty();
        component.FindAll(".stat-tile").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenTheRedirectAfterAnImportNamesItsVersion_KeepsThatVersionSelected()
    {
        // Without this the browser selects the first option, and a second file could go to another
        // version than the one the admin just used (#933).
        AuthorizeAs("Sam");
        GameVersionResponse newer = ImportableVersion("48.1");
        GameVersionResponse imported = ImportableVersion("48.0");
        StubVersionsWithRegisterRel(newer, imported);
        NavigateToTheRedirectAfter(new CompletedImport(imported.Id.Value, new ImportSummary(1, 0, 0, 0, 0, 0, [])));

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.Find("select[name=\"ImportInput.GameVersionId\"] option[selected]").GetAttribute("value")
            .ShouldBe(imported.Id.Value.ToString());
    }

    [Fact]
    public void Render_WhenTheRedirectAfterAnImportCannotLoadTheList_StillSaysTheImportWorked()
    {
        // The import committed in the request that redirected here. If this request cannot load the list,
        // the admin must still see that, or they send the 80 MB file again (#933).
        AuthorizeAs("Sam");
        _client
            .GetApiResultAsync<CollectionResponse<GameVersionResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<CollectionResponse<GameVersionResponse>>(new() { Title = "Nie udało się wczytać wersji." }));
        NavigateToTheRedirectAfter(new CompletedImport(Guid.NewGuid(), new ImportSummary(31, 32, 33, 34, 35, 36, [])));

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.Find(".status-ok").TextContent.ShouldContain("Import zakończony powodzeniem.");
        component.FindAll(".stat-tile .num").Select(tile => tile.TextContent)
            .ShouldBe(["31", "32", "33", "34", "35", "36"]);
        component.Find(".error-message").TextContent.ShouldContain("Nie udało się wczytać wersji.");
    }

    private void NavigateToTheRedirectAfter(CompletedImport completedImport) =>
        Navigation().NavigateTo(Services.GetRequiredService<ImportSummaryRedirect>().PageUriWith(completedImport));

    private BunitNavigationManager Navigation() =>
        (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<ImportExportComponent> RenderPage() =>
        Render<ImportExportComponent>();

    private void AuthorizeAs(string userName) =>
        AddAuthorization().SetAuthorized(userName);

    /// <summary>A version the API offers an admin an <c>import</c> link for, so not a superseded one.</summary>
    private static GameVersionResponse ImportableVersion(string version) =>
        new(GameVersionId.Create(Guid.NewGuid()), version, DateTimeOffset.UnixEpoch, GameVersionStatus.Unprocessed)
        {
            Links = [new LinkDto($"https://tms.example/hateoas/import/{version}", Rels.Import, "POST")]
        };

    /// <summary>A version with no <c>import</c> rel: what a non-admin sees, and what an admin sees on a superseded row.</summary>
    private static GameVersionResponse NonImportableVersion(string version) =>
        new(GameVersionId.Create(Guid.NewGuid()), version, DateTimeOffset.UnixEpoch, GameVersionStatus.Superseded);

    /// <summary>Stubs the versions list as a plain translator sees it, with no admin <c>register</c> rel.</summary>
    private void StubVersions(params GameVersionResponse[] versions) =>
        _client
            .GetApiResultAsync<CollectionResponse<GameVersionResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(new CollectionResponse<GameVersionResponse> { Items = versions }));

    /// <summary>Stubs the versions list as an admin sees it, where the collection carries <c>register</c>.</summary>
    private void StubVersionsWithRegisterRel(params GameVersionResponse[] versions) =>
        _client
            .GetApiResultAsync<CollectionResponse<GameVersionResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(new CollectionResponse<GameVersionResponse>
            {
                Items = versions,
                Links = [new LinkDto("https://tms.example/api/v1/game-versions", Rels.Register, "POST")]
            }));
}
