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
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.GameVersions;

/// <summary>
/// Renders the <see cref="GameVersionsComponent"/> through bUnit over a stubbed TMS client, to pin what
/// the loader tests cannot reach: the list shows every version with its Polish status label; the
/// register form appears only when the collection carries the admin-only <c>register</c> rel and the
/// per-row delete button only when the item carries the admin-only <c>delete</c> rel (#158), never
/// because of a role check in the page; submitting the register form with no version shows the
/// validation notice without posting; and submitting a delete form reaches the loader and then either
/// confirms or shows a problem.
/// <para>
/// The two blocks both actions render through, the <c>_actionMessage</c> success line and the
/// <c>_actionProblem</c> error line, are pinned here by the two delete-submit tests, one success and one
/// refusal from the API. The empty-register test pins the validation-failure render and that the list
/// survives it.
/// A register submit with a typed version cannot be driven here, because bUnit's static SSR form
/// submission does not send a text field set from code. That is the same reason
/// <c>ImportExportTests</c> and <c>EditorTests</c> never fill SSR text inputs. So the register POST
/// request and its success and 422 handling are covered by <c>GameVersionsLoaderTests</c>, and the full
/// typed-form flow by the Playwright E2E tests (ADR-0009).
/// </para>
/// </summary>
public sealed class GameVersionsTests : BunitContext
{
    private readonly ITranslationSystemClient _client = Substitute.For<ITranslationSystemClient>();

    public GameVersionsTests()
    {
        Services.AddAntiforgery();
        Services.AddSingleton(_client);
        Services.AddSingleton(StubDiscoveryCache.AdvertisingGet(Rels.GameVersions));
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
    public void Render_WhenVersionsExist_ShowsTheRegistrationTimeInPolandTime()
    {
        // The API sends the instant in UTC, the page shows the clock the reader is looking at (#736):
        // 21:48 UTC on a summer day is 23:48 in Poland.
        AuthorizeAs("Frodo");
        StubVersions(Version(
            "49.4",
            GameVersionStatus.Unprocessed,
            detectedAt: new DateTimeOffset(2026, 8, 24, 21, 48, 0, TimeSpan.Zero)));

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        string table = component.Find("table.data-table").TextContent;
        table.ShouldContain("2026-08-24 23:48 czasu polskiego");
        table.ShouldNotContain("UTC");
    }

    [Fact]
    public void Render_WhenVersionsExist_NamesTheDateColumnAfterRegistrationAndNeverClaimsDetection()
    {
        // #741: the wiki says plainly that nothing finds a version by itself — an admin types it in.
        // The page used to head this column "Wykryto" and promise a forum reader that was cut to
        // post-MVP (ADR-0030), which sent testers hunting for a mechanism that does not exist.
        AuthorizeAs("Frodo");
        StubVersions(Version("49.4", GameVersionStatus.Unprocessed));

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        component.Find("table.data-table thead").TextContent.ShouldContain("Data rejestracji");
        component.Markup.ToLowerInvariant().ShouldNotContain("wykry");
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
    public async Task Delete_WhenTheApiRefusesInEnglish_ShowsPolishInTheSharedActionErrorBlock()
    {
        // #548: the shared _actionProblem block, which register uses too, used to print the API's English
        // straight into the Polish page. Register's own submit cannot be driven in bUnit, see the class
        // summary above, so the delete test pins the block both actions render through.
        AuthorizeAs("Sam");
        StubVersions(Version("48.0", GameVersionStatus.Unprocessed, canDelete: true));
        _client.DeleteApiResultAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure(new()
            {
                Title = "Data Conflict",
                Detail = "Game version with ID '…' is referenced by one or more translations and cannot be deleted.",
                Status = 422,
                Extensions = { ["errorCode"] = "GameVersionEntity.CannotDeleteReferencedVersion" }
            }));
        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        await component.Find(".col-actions form").SubmitAsync();

        IElement error = component.Find(".status-message.status-error");
        error.QuerySelector(".problem-headline")!.TextContent
            .ShouldBe("Tej wersji gry nie można usunąć, bo są z nią powiązane tłumaczenia.");
        error.QuerySelector(".problem-headline")!.TextContent.ShouldNotContain("Data Conflict");
        // The API's wording only restates the Polish here, so it is not rendered at all (#703). It is
        // kept behind the technical-details block for the import codes whose English carries data.
        error.QuerySelectorAll("details.problem-tech").ShouldBeEmpty();
        error.TextContent.ShouldNotContain("is referenced by one or more translations");
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
    public void Render_WhenNoVersionsExist_SaysNoneWasRegisteredRatherThanNoneDetected()
    {
        AuthorizeAs("Frodo");
        StubVersions();

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        component.Find(".empty").TextContent.ShouldContain("Nie zarejestrowano jeszcze żadnej wersji gry.");
        component.Markup.ToLowerInvariant().ShouldNotContain("wykry");
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

    private static GameVersionResponse Version(
        string version,
        GameVersionStatus status,
        bool canDelete = false,
        DateTimeOffset? detectedAt = null)
    {
        GameVersionId id = GameVersionId.Create(Guid.NewGuid());
        List<LinkDto> links = [];
        if (canDelete)
        {
            links.Add(new LinkDto($"https://tms.example/api/v1/game-versions/{id.Value}", Rels.Delete, "DELETE"));
        }

        return new GameVersionResponse(id, version, detectedAt ?? DateTimeOffset.UnixEpoch, status) { Links = links };
    }
}
