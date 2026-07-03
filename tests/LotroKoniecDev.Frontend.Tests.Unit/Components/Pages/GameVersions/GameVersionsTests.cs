using AngleSharp.Dom;
using Bunit.TestDoubles;
using LotroKoniecDev.Frontend.Components.Pages.GameVersions;
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
using GameVersionsComponent = LotroKoniecDev.Frontend.Components.Pages.GameVersions.GameVersions;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.GameVersions;

/// <summary>
/// Renders the <see cref="GameVersionsComponent"/> through bUnit over a stubbed TMS client, locking
/// down the render wiring the loader tests cannot reach: the list shows every version with its Polish
/// status label; the manual-register form is gated on the collection's admin-only <c>register</c> rel
/// and the per-row delete button on the item's admin-only <c>delete</c> rel (#158) — not a locally
/// recomputed role; submitting the register form with no version surfaces the validation notice without
/// posting; and submitting a delete form follows through to the loader and confirms or surfaces a problem.
/// <para>
/// The shared post-action render blocks — the <c>_actionMessage</c> success line and the
/// <c>_actionProblem</c> error line, both reused verbatim by register and delete — are pinned at the
/// page level by the two delete-submit tests (success + API refusal), and the empty-register test pins
/// the page-level validation-failure render plus list survival. The register submit that carries a typed
/// version cannot be driven here: bUnit's static-SSR form submission does not serialize a programmatically
/// set text field (mirrors why <c>ImportExportTests</c>/<c>EditorTests</c> never populate SSR text inputs),
/// so the register POST request shape and its success/422 result mapping are covered by
/// <c>GameVersionsLoaderTests</c>, and the full typed-form flow by the Playwright E2E (ADR-0009).
/// </para>
/// </summary>
public sealed class GameVersionsTests : BunitContext
{
    private readonly ITranslationSystemClient _client = Substitute.For<ITranslationSystemClient>();

    public GameVersionsTests()
    {
        Services.AddAntiforgery();
        Services.AddSingleton(_client);
        Services.AddScoped<GameVersionsLoader>();
    }

    [Fact]
    public void Render_WhenVersionsExist_ListsEachVersionWithItsStatusLabel()
    {
        AuthorizeAs("Frodo");
        StubVersions(
            Version("48.0", GameVersionStatus.Unprocessed),
            Version("47.2", GameVersionStatus.Processed));

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        component.FindAll("table.data-table tbody tr").Count.ShouldBe(2);
        string table = component.Find("table.data-table").TextContent;
        table.ShouldContain("48.0");
        table.ShouldContain("Nieprzetworzona");
        table.ShouldContain("47.2");
        table.ShouldContain("Przetworzona");
    }

    [Fact]
    public void Render_WhenCollectionLacksTheRegisterRel_DoesNotShowTheRegisterForm()
    {
        AuthorizeAs("Frodo");
        StubVersions(Version("48.0", GameVersionStatus.Unprocessed));

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        component.FindAll("#new-version").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenCollectionHasTheRegisterRel_ShowsTheRegisterFormBoundToTheFormModel()
    {
        AuthorizeAs("Sam");
        StubVersionsWithRegisterRel(Version("48.0", GameVersionStatus.Unprocessed));

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        component.Find("#new-version").GetAttribute("name").ShouldBe("RegisterInput.Version");
    }

    [Fact]
    public void Render_WhenItemLacksTheDeleteRel_DoesNotShowADeleteButton()
    {
        AuthorizeAs("Frodo");
        StubVersions(Version("48.0", GameVersionStatus.Unprocessed));

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        component.FindAll(".col-actions button").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenItemHasTheDeleteRel_ShowsADeleteButton()
    {
        AuthorizeAs("Sam");
        StubVersions(Version("48.0", GameVersionStatus.Unprocessed, canDelete: true));

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        component.Find(".col-actions button").TextContent.ShouldContain("Usuń");
    }

    [Fact]
    public async Task Register_WhenVersionEmpty_ShowsTheValidationNoticeAndDoesNotCallRegister()
    {
        AuthorizeAs("Sam");
        // A single non-deletable item keeps the register form as the only form on the page.
        StubVersionsWithRegisterRel(Version("48.0", GameVersionStatus.Unprocessed));
        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        await component.Find("form").SubmitAsync();

        component.Find(".status-message.status-error").TextContent.ShouldContain("Podaj wersję");
        await _client.DidNotReceive().PostApiResultAsync<GameVersionResponse>(
            Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
        // A rejected register must not wipe the list (the reload still renders the table).
        component.FindAll("table.data-table").ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Delete_WhenSubmitted_CallsTheLoaderAndConfirms()
    {
        AuthorizeAs("Sam");
        // No register rel + one deletable item → the delete form is the only form to submit.
        StubVersions(Version("48.0", GameVersionStatus.Unprocessed, canDelete: true));
        _client.DeleteApiResultAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success());
        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        await component.Find(".col-actions form").SubmitAsync();

        // Behaviour-visible proof the delete ran: the confirmation only renders when the loader succeeded.
        component.Find(".status-message.status-success").TextContent.ShouldContain("Usunięto");
    }

    [Fact]
    public async Task Delete_WhenApiRefuses_SurfacesTheProblemAndKeepsTheList()
    {
        AuthorizeAs("Sam");
        StubVersions(Version("48.0", GameVersionStatus.Unprocessed, canDelete: true));
        _client.DeleteApiResultAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure(new() { Title = "Nie można usunąć przetworzonej wersji.", Status = 422 }));
        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        await component.Find(".col-actions form").SubmitAsync();

        component.Find(".status-message.status-error").TextContent.ShouldContain("Nie można usunąć");
        // The refused delete must not wipe the list (the reload still renders the table).
        component.FindAll("table.data-table").ShouldNotBeEmpty();
    }

    [Fact]
    public void Render_WhenNoVersionsExist_ShowsTheEmptyStateInsteadOfTheTable()
    {
        AuthorizeAs("Frodo");
        StubVersions();

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        component.Find(".empty").TextContent.ShouldContain("Brak wersji gry");
        component.FindAll("table.data-table").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenTheCollectionFailsToLoad_SurfacesTheErrorAndHidesTheRegisterFormAndTable()
    {
        AuthorizeAs("Sam");
        _client
            .GetApiResultAsync<CollectionResponse<GameVersionResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<CollectionResponse<GameVersionResponse>>(new() { Title = "Nie udało się wczytać wersji." }));

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        component.Find(".error-message").TextContent.ShouldContain("Nie udało się wczytać wersji.");
        component.FindAll("#new-version").ShouldBeEmpty();
        component.FindAll("table.data-table").ShouldBeEmpty();
    }

    private IRenderedComponent<GameVersionsComponent> RenderPage() =>
        Render<GameVersionsComponent>();

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

    private static GameVersionResponse Version(string version, GameVersionStatus status, bool canDelete = false)
    {
        GameVersionId id = GameVersionId.Create(Guid.NewGuid());
        List<LinkDto> links = [];
        if (canDelete)
        {
            links.Add(new LinkDto($"https://tms.example/api/v1/game-versions/{id.Value}", Rels.Delete, "DELETE"));
        }

        return new GameVersionResponse(id, version, DateTimeOffset.UnixEpoch, status) { Links = links };
    }
}
