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
/// Pins what the loader tests cannot reach: the register form and the delete buttons follow the admin-only
/// rels (#158). The two delete-submit tests pin the success and error lines both actions share. bUnit
/// cannot send a typed SSR text field, so a register with a version is covered by
/// <c>GameVersionsLoaderTests</c> instead.
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
    public async Task Delete_WhenSubmitted_CallsTheLoaderAndConfirms()
    {
        AuthorizeAs("Sam");
        StubVersions(Version("48.0", GameVersionStatus.Unprocessed, canDelete: true));
        _client.DeleteApiResultAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success());
        IRenderedComponent<GameVersionsComponent> component = RenderPage();

        await component.Find(".col-actions form").SubmitAsync();

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
        // A refused delete must not wipe the list.
        component.FindAll("table.data-table").ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Delete_WhenTheApiRefusesInEnglish_ShowsPolishInTheSharedActionErrorBlock()
    {
        // #548: the error block register shares; register's own submit cannot be driven in bUnit.
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
}
