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
/// Pins what the loader tests cannot reach: the import panel follows the admin-only <c>register</c> rel
/// (#158), the inputs carry the names static SSR binds, and the summary survives the redirect after an
/// import (#933).
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
        // A translator lists the versions, but the collection carries no `register` rel.
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
        // Static SSR binds an IFormFile only as a property of the form model, so the inputs must be named
        // ImportInput.*. Any other name binds null.
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
        // Every version is Superseded, so the API sends `import` on none of them (#610).
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
        // Without the list there is no register rel, but the error and the export download still show.
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
        // The import codes carry the line number and the removal counters, so this page keeps the drawer
        // (#703). The exact wording is pinned in ApiProblemAlertTests.
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
        // The GET side of the redirect after an import (#933).
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
        // The form posts to the current address, which still carries the last summary (#933).
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
        // Otherwise the browser selects the first option, and a second file could go to another version
        // (#933).
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
        // If this request cannot load the list, the admin must still see that the import worked, or they
        // send the 80 MB file again (#933).
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

    /// <summary>Carries an <c>import</c> link, as for an admin on a version not superseded.</summary>
    private static GameVersionResponse ImportableVersion(string version) =>
        new(GameVersionId.Create(Guid.NewGuid()), version, DateTimeOffset.UnixEpoch, GameVersionStatus.Unprocessed)
        {
            Links = [new LinkDto($"https://tms.example/hateoas/import/{version}", Rels.Import, "POST")]
        };

    /// <summary>What a non-admin sees, and what an admin sees on a superseded row.</summary>
    private static GameVersionResponse NonImportableVersion(string version) =>
        new(GameVersionId.Create(Guid.NewGuid()), version, DateTimeOffset.UnixEpoch, GameVersionStatus.Superseded);

    private void StubVersions(params GameVersionResponse[] versions) =>
        _client
            .GetApiResultAsync<CollectionResponse<GameVersionResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(new CollectionResponse<GameVersionResponse> { Items = versions }));

    private void StubVersionsWithRegisterRel(params GameVersionResponse[] versions) =>
        _client
            .GetApiResultAsync<CollectionResponse<GameVersionResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(new CollectionResponse<GameVersionResponse>
            {
                Items = versions,
                Links = [new LinkDto("https://tms.example/api/v1/game-versions", Rels.Register, "POST")]
            }));
}
