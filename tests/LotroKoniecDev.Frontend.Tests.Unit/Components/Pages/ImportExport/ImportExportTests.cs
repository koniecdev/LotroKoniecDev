using AngleSharp.Dom;
using Bunit.TestDoubles;
using LotroKoniecDev.Frontend.Components.Pages.ImportExport;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
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
        await _client.DidNotReceive().SendMultipartApiResultAsync<TranslationSystem.Contracts.Import.ImportSummary>(
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
