using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Bunit.TestDoubles;
using LotroKoniecDev.Frontend.Components.Pages.ImportExport;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ImportExportComponent = LotroKoniecDev.Frontend.Components.Pages.ImportExport.ImportExport;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.ImportExport;

/// <summary>
/// Renders the <see cref="ImportExportComponent"/> through bUnit over a stubbed TMS client, locking
/// down the render wiring the loader tests cannot reach: the export download link is offered to every
/// authenticated translator and targets the server download route; the admin-only import panel is gated
/// on the <c>Admin</c> role; the game-version selector is populated from the listed versions; and — the
/// regression guard for the M3-07 SSR binding bug — every import input is named after the
/// <c>[SupplyParameterFromForm]</c> form-model property path (<c>ImportInput.*</c>) so a fresh
/// <c>exported.txt</c> actually binds on post. Submitting with no file surfaces the validation notice,
/// proving the submit reaches the handler.
/// </summary>
public sealed class ImportExportTests : BunitContext
{
    private const string AdminRole = "Admin";

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
    public void Render_WhenNotAdmin_DoesNotShowTheImportPanelOrCallTheVersionsEndpoint()
    {
        AuthorizeAs("Frodo");

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.FindAll("form").ShouldBeEmpty();
        component.FindAll("input[type=file]").ShouldBeEmpty();
        _client.DidNotReceive().GetApiResultAsync<CollectionResponse<GameVersionResponse>>(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Render_WhenAdmin_ShowsTheImportFormWithAVersionOptionPerListedVersion()
    {
        AuthorizeAs("Sam", AdminRole);
        StubVersions(
            new GameVersionResponse(new GameVersionId(Guid.NewGuid()), "48.0", DateTimeOffset.UnixEpoch, GameVersionStatus.Unprocessed),
            new GameVersionResponse(new GameVersionId(Guid.NewGuid()), "47.2", DateTimeOffset.UnixEpoch, GameVersionStatus.Processed));

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.FindAll("select[name=\"ImportInput.GameVersionId\"] option").Count.ShouldBe(2);
    }

    [Fact]
    public void Render_WhenAdmin_NamesEveryImportInputAfterTheFormModelPropertyPath()
    {
        // Regression guard for the SSR binding bug: Blazor static-SSR maps an IFormFile only as a
        // property of a [SupplyParameterFromForm] model, so the inputs MUST be named ImportInput.* —
        // a bare name (e.g. "UploadFile") binds null and the import can never run.
        AuthorizeAs("Sam", AdminRole);
        StubVersions(new GameVersionResponse(new GameVersionId(Guid.NewGuid()), "48.0", DateTimeOffset.UnixEpoch, GameVersionStatus.Unprocessed));

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.Find("input[type=file]").GetAttribute("name").ShouldBe("ImportInput.File");
        component.Find("select").GetAttribute("name").ShouldBe("ImportInput.GameVersionId");
        component.Find("input[type=checkbox]").GetAttribute("name").ShouldBe("ImportInput.AllowMassRemoval");
    }

    [Fact]
    public async Task Submit_WhenNoFileChosen_ShowsTheChooseFileValidationAndDoesNotCallImport()
    {
        AuthorizeAs("Sam", AdminRole);
        StubVersions(new GameVersionResponse(new GameVersionId(Guid.NewGuid()), "48.0", DateTimeOffset.UnixEpoch, GameVersionStatus.Unprocessed));
        IRenderedComponent<ImportExportComponent> component = RenderPage();

        await component.Find("form").SubmitAsync();

        component.Find(".status-down").TextContent.ShouldContain("Wybierz plik exported.txt");
        await _client.DidNotReceive().SendMultipartApiResultAsync<LotroKoniecDev.TranslationSystem.Contracts.Import.ImportSummary>(
            Arg.Any<HttpMethod>(),
            Arg.Any<string>(),
            Arg.Any<MultipartFormDataContent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Render_WhenAdminAndNoVersionsExist_ShowsTheEmptyStateInsteadOfTheForm()
    {
        AuthorizeAs("Sam", AdminRole);
        StubVersions();

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.Find(".empty").TextContent.ShouldContain("Brak wersji gry");
        component.FindAll("input[type=file]").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenAdminAndVersionsFailToLoad_ShowsTheErrorInsteadOfTheForm()
    {
        AuthorizeAs("Sam", AdminRole);
        _client
            .GetApiResultAsync<CollectionResponse<GameVersionResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<CollectionResponse<GameVersionResponse>>(new() { Title = "Nie udało się wczytać wersji." }));

        IRenderedComponent<ImportExportComponent> component = RenderPage();

        component.Find(".status-down").TextContent.ShouldContain("Nie udało się wczytać wersji.");
        component.FindAll("input[type=file]").ShouldBeEmpty();
    }

    private IRenderedComponent<ImportExportComponent> RenderPage() =>
        Render<ImportExportComponent>();

    private void AuthorizeAs(string userName, params string[] roles)
    {
        BunitAuthorizationContext authorization = this.AddAuthorization();
        authorization.SetAuthorized(userName);
        if (roles.Length > 0)
        {
            authorization.SetRoles(roles);
        }
    }

    private void StubVersions(params GameVersionResponse[] versions)
    {
        _client
            .GetApiResultAsync<CollectionResponse<GameVersionResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(new CollectionResponse<GameVersionResponse> { Items = versions }));
    }
}
