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

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.ImportExport;

/// <summary>
/// Renders the <see cref="ImportExportComponent"/> through bUnit over a stubbed TMS client, locking
/// down the render wiring the loader tests cannot reach: the export download link is offered to every
/// authenticated translator and targets the server download route; the import panel is gated on the
/// game-versions collection's admin-only <c>register</c> rel (#158) — not a locally recomputed role;
/// the game-version selector is populated from the listed versions; and — the regression guard for the
/// M3-07 SSR binding bug — every import input is named after the <c>[SupplyParameterFromForm]</c>
/// form-model property path (<c>ImportInput.*</c>) so a fresh <c>exported.txt</c> actually binds on
/// post. Submitting with no file surfaces the validation notice, proving the submit reaches the handler.
/// </summary>
public sealed class ImportExportTests : BunitContext
{
    private readonly ITranslationSystemClient _client = Substitute.For<ITranslationSystemClient>();

    public ImportExportTests()
    {
        Services.AddAntiforgery();
        Services.AddSingleton(_client);
        Services.AddScoped<ImportExportLoader>();
    }

    [Fact]
    public void Render_WhenAuthenticated_OffersTheDownloadLinkTargetingTheServerDownloadRoute()
    {
        AuthorizeAs("Frodo");
        StubVersions();

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        IElement download = component.Find("a[download]");
        download.GetAttribute("href").ShouldBe("/import-export/download");
        download.GetAttribute("download").ShouldBe(ImportExportLoader.DownloadFileName);
    }

    [Fact]
    public void Render_WhenCollectionLacksTheRegisterRel_DoesNotShowTheImportPanel()
    {
        // A non-admin translator can list versions but the collection carries no `register` rel, so the
        // admin-only import panel stays hidden — driven by the server's affordance, not a local role check.
        AuthorizeAs("Frodo");
        StubVersions(
            new GameVersionResponse(GameVersionId.Create(Guid.NewGuid()), "48.0", DateTimeOffset.UnixEpoch, GameVersionStatus.Unprocessed));

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.FindAll("form").ShouldBeEmpty();
        component.FindAll("input[type=file]").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenCollectionHasTheRegisterRel_ShowsTheImportFormWithAVersionOptionPerListedVersion()
    {
        AuthorizeAs("Sam");
        StubVersionsWithRegisterRel(
            new GameVersionResponse(GameVersionId.Create(Guid.NewGuid()), "48.0", DateTimeOffset.UnixEpoch, GameVersionStatus.Unprocessed),
            new GameVersionResponse(GameVersionId.Create(Guid.NewGuid()), "47.2", DateTimeOffset.UnixEpoch, GameVersionStatus.Processed));

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.FindAll("select[name=\"ImportInput.GameVersionId\"] option").Count.ShouldBe(2);
    }

    [Fact]
    public void Render_WhenCollectionHasTheRegisterRel_NamesEveryImportInputAfterTheFormModelPropertyPath()
    {
        // Regression guard for the SSR binding bug: Blazor static-SSR maps an IFormFile only as a
        // property of a [SupplyParameterFromForm] model, so the inputs MUST be named ImportInput.* —
        // a bare name (e.g. "UploadFile") binds null and the import can never run.
        AuthorizeAs("Sam");
        StubVersionsWithRegisterRel(new GameVersionResponse(GameVersionId.Create(Guid.NewGuid()), "48.0", DateTimeOffset.UnixEpoch, GameVersionStatus.Unprocessed));

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.Find("input[type=file]").GetAttribute("name").ShouldBe("ImportInput.File");
        component.Find("select").GetAttribute("name").ShouldBe("ImportInput.GameVersionId");
        component.Find("input[type=checkbox]").GetAttribute("name").ShouldBe("ImportInput.AllowMassRemoval");
    }

    [Fact]
    public async Task Submit_WhenNoFileChosen_ShowsTheChooseFileValidationAndDoesNotCallImport()
    {
        AuthorizeAs("Sam");
        StubVersionsWithRegisterRel(new GameVersionResponse(GameVersionId.Create(Guid.NewGuid()), "48.0", DateTimeOffset.UnixEpoch, GameVersionStatus.Unprocessed));
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
    public void Render_WhenTheCollectionFailsToLoad_HidesTheImportPanelButSurfacesTheErrorAndKeepsTheDownload()
    {
        // The register rel can't be read from a failed fetch, so the import panel is hidden — but the
        // error is surfaced (outside the gate) so an admin gets feedback, and the export download
        // (open to any translator) stays available.
        AuthorizeAs("Sam");
        _client
            .GetApiResultAsync<CollectionResponse<GameVersionResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<CollectionResponse<GameVersionResponse>>(new() { Title = "Nie udało się wczytać wersji." }));

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.FindAll("input[type=file]").ShouldBeEmpty();
        component.Find(".error-message").TextContent.ShouldContain("Nie udało się wczytać wersji.");
        component.Find("a[download]").GetAttribute("href").ShouldBe("/import-export/download");
    }

    private IRenderedComponent<ImportExportComponent> RenderPage() =>
        Render<ImportExportComponent>();

    private void AuthorizeAs(string userName) =>
        AddAuthorization().SetAuthorized(userName);

    /// <summary>Stubs the versions list as a plain translator sees it — no admin <c>register</c> rel.</summary>
    private void StubVersions(params GameVersionResponse[] versions) =>
        _client
            .GetApiResultAsync<CollectionResponse<GameVersionResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(new CollectionResponse<GameVersionResponse> { Items = versions }));

    /// <summary>Stubs the versions list as an admin sees it — the collection carries the <c>register</c> rel.</summary>
    private void StubVersionsWithRegisterRel(params GameVersionResponse[] versions) =>
        _client
            .GetApiResultAsync<CollectionResponse<GameVersionResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(new CollectionResponse<GameVersionResponse>
            {
                Items = versions,
                Links = [new LinkDto("https://tms.example/api/v1/game-versions", Rels.Register, "POST")]
            }));
}
