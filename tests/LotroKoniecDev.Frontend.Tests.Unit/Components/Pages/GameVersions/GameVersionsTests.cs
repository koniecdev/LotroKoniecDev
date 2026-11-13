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
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using GameVersionsComponent = LotroKoniecDev.Frontend.Components.Pages.GameVersions.GameVersions;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.GameVersions;

/// <summary>
/// Pins what the loader tests cannot reach: the register form and the delete link follow the admin-only
/// rels (#158), and a delete asks a question that names the version first (#549). bUnit cannot send a
/// typed SSR text field, so a register with a version is covered by <c>GameVersionsLoaderTests</c>. The
/// real form posts and the redirect after a delete are in <c>GameVersionsDeleteConfirmationTests</c>.
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
        // #736: 21:48 UTC on a summer day is 23:48 in Poland.
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
        // #741: nothing finds a version by itself, an admin types it in (ADR-0030).
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
    public void Render_WhenItemLacksTheDeleteRel_DoesNotShowADeleteControl()
    {
        AuthorizeAs("Frodo");
        StubVersions(Version("48.0", GameVersionStatus.Unprocessed));

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        component.FindAll("table.data-table tbody .col-actions *").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenItemHasTheDeleteRel_ShowsADeleteLinkToTheQuestionAndNoDeleteForm()
    {
        AuthorizeAs("Sam");
        GameVersionResponse version = Version("48.0", GameVersionStatus.Unprocessed, canDelete: true);
        StubVersions(version);

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        IElement deleteLink = component.Find(".col-actions a");
        deleteLink.TextContent.ShouldBe("Usuń");
        deleteLink.GetAttribute("href").ShouldBe(ConfirmDeleteUri(version));
        component.FindAll("table.data-table form").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(GameVersionStatus.Unprocessed)]
    [InlineData(GameVersionStatus.Superseded)]
    public void Render_WhenTheQueryNamesARowWithTheDeleteRel_AsksAQuestionThatNamesTheVersion(GameVersionStatus status)
    {
        AuthorizeAs("Sam");
        GameVersionResponse version = Version("48.0", status, canDelete: true);
        StubVersions(version);
        Navigation().NavigateTo(ConfirmDeleteUri(version));

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        IElement question = component.Find(".version-delete-confirm");
        question.TextContent.ShouldContain("Na pewno usunąć wersję 48.0?");
        question.QuerySelector("form button[type=submit]").ShouldNotBeNull().TextContent.ShouldBe("Tak, usuń");
        question.QuerySelector("a").ShouldNotBeNull().GetAttribute("href").ShouldBe("/game-versions");
    }

    [Fact]
    public void Render_WhenAskingToConfirmADelete_KeepsAnInertUsuńInTheRowAndPutsTheQuestionInTheRowBelow()
    {
        // An inert copy with the link's classes keeps the link's place, and the question cannot widen the
        // table from its own row. So the second click of a double click on "Usuń" lands on the copy, never on
        // "Tak, usuń", at any width (#549).
        AuthorizeAs("Sam");
        GameVersionResponse version = Version("48.0", GameVersionStatus.Unprocessed, canDelete: true);
        StubVersions(version);
        Navigation().NavigateTo(ConfirmDeleteUri(version));

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        IElement[] rows = [.. component.FindAll("table.data-table tbody tr")];
        rows.Length.ShouldBe(2);
        IElement actionsCell = rows[0].QuerySelector(".col-actions").ShouldNotBeNull();
        IElement inertDelete = actionsCell.Children.ShouldHaveSingleItem();
        inertDelete.TagName.ShouldBe("SPAN");
        inertDelete.TextContent.ShouldBe("Usuń");
        inertDelete.GetAttribute("aria-disabled").ShouldBe("true");
        inertDelete.ClassList.ShouldBe(["btn", "btn-ghost", "btn-sm", "is-disabled"], ignoreOrder: true);
        rows[1].QuerySelector("td").ShouldNotBeNull().GetAttribute("colspan").ShouldBe("4");
        rows[1].QuerySelector(".version-delete-confirm").ShouldNotBeNull();
    }

    [Fact]
    public void Render_WhenAskingToConfirmADelete_PutsCancelLastAndTheDangerButtonRightBeforeIt()
    {
        AuthorizeAs("Sam");
        GameVersionResponse version = Version("48.0", GameVersionStatus.Unprocessed, canDelete: true);
        StubVersions(version);
        Navigation().NavigateTo(ConfirmDeleteUri(version));

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        IElement[] controls = [.. component.FindAll(".version-delete-confirm a, .version-delete-confirm button")];
        controls.Length.ShouldBe(2);
        controls[0].TagName.ShouldBe("BUTTON");
        controls[0].ClassList.ShouldContain("btn-danger");
        controls[1].TagName.ShouldBe("A");
        controls[1].TextContent.ShouldBe("Anuluj");
    }

    [Fact]
    public void Render_WhenTheQueryNamesOneOfTwoDeletableRows_AsksOnlyAboutThatRow()
    {
        AuthorizeAs("Sam");
        GameVersionResponse named = Version("48.1", GameVersionStatus.Unprocessed, canDelete: true);
        GameVersionResponse other = Version("48.0", GameVersionStatus.Unprocessed, canDelete: true);
        StubVersions(named, other);
        Navigation().NavigateTo(ConfirmDeleteUri(named));

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        component.FindAll("table.data-table form").Count.ShouldBe(1);
        component.Find(".version-delete-confirm").TextContent.ShouldContain("48.1");
        component.FindAll(".col-actions a")
            .Select(link => link.GetAttribute("href"))
            .ShouldContain(ConfirmDeleteUri(other));
    }

    [Theory]
    [InlineData("3f2504e0-4f89-41d3-9a0c-0305e82c3301")]
    [InlineData("zzz")]
    [InlineData("")]
    public void Render_WhenTheQueryNamesNoListedRow_ShowsThePlainList(string confirmDelete)
    {
        AuthorizeAs("Sam");
        StubVersions(Version("48.0", GameVersionStatus.Unprocessed, canDelete: true));
        Navigation().NavigateTo($"/game-versions?confirm-delete={confirmDelete}");

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        component.FindAll("table.data-table form, .version-delete-confirm").ShouldBeEmpty();
        component.Find(".col-actions a").TextContent.ShouldBe("Usuń");
    }

    [Fact]
    public void Render_WhenTheQueryNamesARowWithoutTheDeleteRel_ShowsNoQuestionAndNoDeleteControl()
    {
        // A translator, or a Processed row: the address alone never opens a delete.
        AuthorizeAs("Frodo");
        GameVersionResponse version = Version("47.2", GameVersionStatus.Processed);
        StubVersions(version);
        Navigation().NavigateTo(ConfirmDeleteUri(version));

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        component.FindAll("table.data-table a, table.data-table button, table.data-table form, table.data-table .btn")
            .ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenTheDeletedFlagIsInTheQuery_ShowsTheDeleteConfirmation()
    {
        AuthorizeAs("Sam");
        StubVersions(Version("48.0", GameVersionStatus.Unprocessed));
        Navigation().NavigateTo("/game-versions?deleted=true");

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        component.Find(".status-message.status-success").TextContent.ShouldContain("Usunięto wersję gry.");
    }

    [Theory]
    [InlineData("zzz")]
    [InlineData("false")]
    [InlineData("")]
    public void Render_WhenTheDeletedFlagIsNotTrue_ShowsNoDeleteConfirmation(string deleted)
    {
        AuthorizeAs("Sam");
        StubVersions(Version("48.0", GameVersionStatus.Unprocessed));
        Navigation().NavigateTo($"/game-versions?deleted={deleted}");

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        component.FindAll(".status-message").ShouldBeEmpty();
        component.FindAll("table.data-table").ShouldNotBeEmpty();
    }

    [Fact]
    public void Render_WhenTheDeletedFlagIsInTheQueryAndTheListFails_ShowsTheConfirmationAndTheLoadError()
    {
        // The delete happened even when the load after its redirect fails.
        AuthorizeAs("Sam");
        _client
            .GetApiResultAsync<CollectionResponse<GameVersionResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<CollectionResponse<GameVersionResponse>>(new() { Title = "Nie udało się wczytać wersji." }));
        Navigation().NavigateTo("/game-versions?deleted=true");

        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        component.Find(".status-message.status-success").TextContent.ShouldContain("Usunięto wersję gry.");
        component.Find(".error-message").TextContent.ShouldContain("Nie udało się wczytać wersji.");
    }

    [Fact]
    public async Task Register_WhenVersionEmpty_ShowsTheValidationNoticeAndDoesNotCallRegister()
    {
        AuthorizeAs("Sam");
        StubVersionsWithRegisterRel(Version("48.0", GameVersionStatus.Unprocessed));
        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        await component.Find("form").SubmitAsync();

        component.Find(".status-message.status-error").TextContent.ShouldContain("Podaj wersję");
        await _client.DidNotReceive().PostApiResultAsync<GameVersionResponse>(
            Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
        // A rejected register must not wipe the list.
        component.FindAll("table.data-table").ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Delete_WhenConfirmed_RedirectsToTheListWithTheDeletedFlagFollowingTheDeleteLinkHref()
    {
        AuthorizeAs("Sam");
        GameVersionResponse version = Version("48.0", GameVersionStatus.Unprocessed, canDelete: true);
        StubVersions(version);
        _client.DeleteApiResultAsync(DeleteHref(version), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success());
        BunitNavigationManager navigation = Navigation();
        navigation.NavigateTo(ConfirmDeleteUri(version));
        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        await component.Find("table.data-table form").SubmitAsync();

        // Only a DELETE to the row's own href is stubbed to succeed, so the redirect proves it was followed.
        navigation.Uri.ShouldEndWith("/game-versions?deleted=true");
    }

    [Fact]
    public async Task Delete_WhenApiRefuses_SurfacesTheProblemAndKeepsTheListAndTheAddress()
    {
        AuthorizeAs("Sam");
        GameVersionResponse version = Version("48.0", GameVersionStatus.Unprocessed, canDelete: true);
        StubVersions(version);
        _client.DeleteApiResultAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure(new() { Title = "Nie można usunąć przetworzonej wersji.", Status = 422 }));
        BunitNavigationManager navigation = Navigation();
        navigation.NavigateTo(ConfirmDeleteUri(version));
        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        await component.Find("table.data-table form").SubmitAsync();

        component.Find(".status-message.status-error").TextContent.ShouldContain("Nie można usunąć");
        // A refused delete must not wipe the list, and it does not redirect.
        component.FindAll("table.data-table").ShouldNotBeEmpty();
        navigation.Uri.ShouldEndWith(ConfirmDeleteUri(version));
    }

    [Fact]
    public async Task Delete_WhenTheApiRefusesInEnglish_ShowsPolishInTheSharedActionErrorBlock()
    {
        // #548: the error block register shares; register's own submit cannot be driven in bUnit.
        AuthorizeAs("Sam");
        GameVersionResponse version = Version("48.0", GameVersionStatus.Unprocessed, canDelete: true);
        StubVersions(version);
        _client.DeleteApiResultAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure(new()
            {
                Title = "Data Conflict",
                Detail = "Game version with ID '…' is referenced by one or more translations and cannot be deleted.",
                Status = 422,
                Extensions = { ["errorCode"] = "GameVersionEntity.CannotDeleteReferencedVersion" }
            }));
        Navigation().NavigateTo(ConfirmDeleteUri(version));
        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        await component.Find("table.data-table form").SubmitAsync();

        IElement error = component.Find(".status-message.status-error");
        error.QuerySelector(".problem-headline")!.TextContent
            .ShouldBe("Tej wersji gry nie można usunąć, bo są z nią powiązane tłumaczenia.");
        error.QuerySelector(".problem-headline")!.TextContent.ShouldNotContain("Data Conflict");
        // The API's wording only restates the Polish here, so it is not rendered (#703).
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

    private BunitNavigationManager Navigation() =>
        (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

    private void AuthorizeAs(string userName) =>
        AddAuthorization().SetAuthorized(userName);

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

    private static string ConfirmDeleteUri(GameVersionResponse version) =>
        $"/game-versions?confirm-delete={version.Id.Value}";

    private static string DeleteHref(GameVersionResponse version) =>
        version.Links.Single(link => link.Rel == Rels.Delete).Href;
}
