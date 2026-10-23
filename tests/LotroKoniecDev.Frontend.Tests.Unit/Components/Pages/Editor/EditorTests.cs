using System.Globalization;
using AngleSharp.Dom;
using Bunit.TestDoubles;
using LotroKoniecDev.Frontend.Components.Pages.Editor;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Constants;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using EditorComponent = LotroKoniecDev.Frontend.Components.Pages.Editor.Editor;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Editor;

/// <summary>
/// Pins what the <see cref="PlaceholderAnalyzer"/> tests cannot reach: the placeholder highlight and the
/// count warning on the rendered page. It also covers the redirect after post (#321) and the recovery
/// form a failed save leaves (#1122).
/// </summary>
public sealed class EditorTests : BunitContext
{
    private const string Token = "<--DO_NOT_TOUCH!-->";

    private readonly ITranslationSystemClient _client = Substitute.For<ITranslationSystemClient>();

    public EditorTests()
    {
        Services.AddAntiforgery();
        Services.AddSingleton(_client);
        Services.AddSingleton(StubDiscoveryCache.AdvertisingGet(Rels.Translations, Rels.Upsert));
        Services.AddScoped<TranslationEditorLoader>();
        AddAuthorization().SetAuthorized("Frodo");
    }

    [Fact]
    public void Render_WhenSourceHasMarkers_HighlightsEveryPlaceholderMarkerInTheEnglishSource()
    {
        StubLoad(BuildDetail(
            sourceText: $"You gained {Token} of {Token}.",
            translatedText: $"Zdobyto {Token} z {Token}."));

        IRenderedComponent<EditorComponent> component = RenderEditor();

        IReadOnlyList<IElement> highlights = component.FindAll(".editor-source .ph-token");
        highlights.Count.ShouldBe(2);
        highlights.ShouldAllBe(span => span.TextContent == Token);
    }

    [Fact]
    public void Render_WhenSourceHasNoMarkers_RendersTheSourceWithoutAnyHighlightSpans()
    {
        StubLoad(BuildDetail(sourceText: "Plain English line.", translatedText: "Zwykły polski."));

        IRenderedComponent<EditorComponent> component = RenderEditor();

        component.Find(".editor-source").TextContent.ShouldBe("Plain English line.");
        component.FindAll(".editor-source .ph-token").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenTranslationDropsAMarker_ShowsThePlaceholderMismatchWarning()
    {
        StubLoad(BuildDetail(
            sourceText: $"You have {Token} of {Token} items.",
            translatedText: $"Masz {Token} przedmiotów."));

        IRenderedComponent<EditorComponent> component = RenderEditor();

        IElement warning = component.Find(".status-warning");
        warning.TextContent.ShouldContain("Liczba znaczników się nie zgadza");
    }

    [Fact]
    public void Render_WhenPlaceholderCountsMatch_DoesNotShowTheMismatchWarning()
    {
        StubLoad(BuildDetail(
            sourceText: $"You gained {Token} reputation.",
            translatedText: $"Zdobyto {Token} reputacji."));

        IRenderedComponent<EditorComponent> component = RenderEditor();

        component.FindAll(".status-warning").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenTheRowFailsToLoad_RendersTheLoadErrorInsteadOfTheEditorGrid()
    {
        _client
            .GetApiResultAsync<TranslationDetailResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<TranslationDetailResponse>(new() { Title = "Nie znaleziono." }));

        IRenderedComponent<EditorComponent> component = RenderEditor();

        component.Find(".error-message").TextContent.ShouldContain("Nie znaleziono.");
        component.FindAll(".editor-grid").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenRowWasInvalidatedByAnUpdate_ShowsTheSupersededEnglishWithItsOwnHighlighting()
    {
        // spec 0001: after a game update the previous English stays visible for comparison.
        StubLoad(BuildDetail(
            sourceText: $"You gained {Token} renown.",
            translatedText: $"Zdobyto {Token} sławy.",
            previousSourceText: $"You earned {Token} renown."));

        IRenderedComponent<EditorComponent> component = RenderEditor();

        IElement superseded = component.Find(".editor-superseded .editor-source");
        superseded.TextContent.ShouldBe("You earned <--DO_NOT_TOUCH!--> renown.");
        superseded.QuerySelectorAll(".ph-token").Length.ShouldBe(1);
    }

    [Fact]
    public void Render_WhenRowHasNoPreviousSource_DoesNotRenderTheSupersededBlock()
    {
        StubLoad(BuildDetail(
            sourceText: $"You gained {Token} renown.",
            translatedText: $"Zdobyto {Token} sławy."));

        IRenderedComponent<EditorComponent> component = RenderEditor();

        component.FindAll(".editor-superseded").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenDetailCarriesTheUpsertRel_RendersTheSaveForm()
    {
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canEdit: true));

        IRenderedComponent<EditorComponent> component = RenderEditor();

        component.FindAll("textarea#translated").Count.ShouldBe(1);
    }

    [Fact]
    public void Render_SaveForm_CapsTheTextareaAtTheDatPieceLimit()
    {
        // The API rejects a longer text (#598); the browser cap only saves the translator the surprise.
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canEdit: true));

        IRenderedComponent<EditorComponent> component = RenderEditor();

        component.Find("textarea#translated").GetAttribute("maxlength")
            .ShouldBe(DatFormatConstants.MaxTranslatedTextLength.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Render_WhenDetailLacksTheUpsertRel_HidesTheSaveFormAndShowsTheReadOnlyNote()
    {
        // A soft-removed row advertises `self` only; the editor must offer no way to edit it.
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canEdit: false));

        IRenderedComponent<EditorComponent> component = RenderEditor();

        component.FindAll("textarea#translated").ShouldBeEmpty();
        component.Find(".text-dim").TextContent.ShouldContain("tylko do odczytu");
    }

    [Fact]
    public void Render_WhenDetailCarriesTheApproveRel_ShowsTheApproveButton()
    {
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canApprove: true));

        IRenderedComponent<EditorComponent> component = RenderEditor();

        component.FindAll(".editor-approve").Count.ShouldBe(1);
        component.Find(".editor-approve button").TextContent.ShouldContain("Zatwierdź");
    }

    [Fact]
    public void Render_WhenDetailLacksTheApproveRel_HidesTheApproveButton()
    {
        // A caller who is not an admin: only the missing rel hides the button.
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canApprove: false));

        IRenderedComponent<EditorComponent> component = RenderEditor();

        component.FindAll(".editor-approve").ShouldBeEmpty();
    }

    [Fact]
    public async Task Save_WhenSucceeds_RedirectsToTheRowWithASavedFlagFollowingTheUpsertLinkHref()
    {
        Guid id = Guid.NewGuid();
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canEdit: true));
        _client
            .PutApiResultAsync<TranslationDetailResponse>(SaveHref, Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(BuildDetail("Hello.", "Cześć.", canEdit: true)));
        BunitNavigationManager navigation = Navigation();
        IRenderedComponent<EditorComponent> component = RenderEditor(id);

        // The fixture carries the upsert rel but not the approve rel, so the save form is the only form.
        await component.Find("form").SubmitAsync();

        // Only a PUT to the loaded row's upsert href is stubbed to succeed, so the redirect proves it was
        // followed.
        navigation.Uri.ShouldEndWith($"/editor/{id}?saved=true");
    }

    [Fact]
    public void Render_WhenSavedFlagIsInTheQuery_ShowsTheSaveConfirmation()
    {
        Guid id = Guid.NewGuid();
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canEdit: true));
        Navigation().NavigateTo($"/editor/{id}?saved=true");

        IRenderedComponent<EditorComponent> component = RenderEditor(id);

        component.Find(".status-message.status-success").TextContent.ShouldContain("Tłumaczenie zapisano");
    }

    [Theory]
    [InlineData("saved", "Tłumaczenie zapisano.")]
    [InlineData("approved", "Tłumaczenie zatwierdzono i dołączono do dystrybucji.")]
    public void Render_WhenTheRowFailsToLoadAfterTheRedirect_StillShowsTheConfirmationAboveTheLoadError(
        string successFlag,
        string confirmation)
    {
        // If the row cannot be loaded again, the translator must still see that the save worked (#1122).
        Guid id = Guid.NewGuid();
        _client
            .GetApiResultAsync<TranslationDetailResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<TranslationDetailResponse>(new() { Title = "Nie znaleziono." }));
        Navigation().NavigateTo($"/editor/{id}?{successFlag}=true");

        IRenderedComponent<EditorComponent> component = RenderEditor(id);

        component.Find(".status-message.status-success").TextContent.ShouldContain(confirmation);
        component.Find(".error-message").TextContent.ShouldContain("Nie znaleziono.");
    }

    [Fact]
    public async Task Save_WhenSaveFails_DoesNotRedirectAndShowsTheErrorInline()
    {
        // A rejected save does not redirect, so the error and the form stay on screen.
        Guid id = Guid.NewGuid();
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canEdit: true));
        _client
            .PutApiResultAsync<TranslationDetailResponse>(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<TranslationDetailResponse>(new() { Title = "Zapis odrzucony." }));
        BunitNavigationManager navigation = Navigation();
        string uriBeforeSubmit = navigation.Uri;
        IRenderedComponent<EditorComponent> component = RenderEditor(id);

        await component.Find("form").SubmitAsync();

        navigation.Uri.ShouldBe(uriBeforeSubmit);
        component.Find(".status-message.status-error").TextContent.ShouldContain("Zapis odrzucony.");
        component.FindAll("textarea#translated").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Save_WhenTheApiRejectsAnEmptyTranslation_ShowsPolishAndNotTheApiEnglish()
    {
        // #548: the API's English "Validation Error" must not reach the Polish page.
        Guid id = Guid.NewGuid();
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canEdit: true));
        _client
            .PutApiResultAsync<TranslationDetailResponse>(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<TranslationDetailResponse>(new()
            {
                Title = "Validation Error",
                Detail = "'Translated Text' must not be empty.",
                Status = 400,
                Extensions = { ["errorCode"] = "Translations.Validation" }
            }));
        IRenderedComponent<EditorComponent> component = RenderEditor(id);

        await component.Find("form").SubmitAsync();

        IElement error = component.Find(".status-message.status-error");
        error.QuerySelector(".problem-headline")!.TextContent
            .ShouldBe("Tłumaczenie nie może być puste i nie może przekraczać dozwolonej długości.");
        error.QuerySelector(".problem-headline")!.TextContent.ShouldNotContain("Validation Error");
    }

    [Fact]
    public async Task Approve_WhenApproveFails_DoesNotRedirectAndShowsTheErrorInline()
    {
        Guid id = Guid.NewGuid();
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canApprove: true));
        _client
            .PostApiResultAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure(new() { Title = "Zatwierdzenie odrzucone." }));
        BunitNavigationManager navigation = Navigation();
        string uriBeforeSubmit = navigation.Uri;
        IRenderedComponent<EditorComponent> component = RenderEditor(id);

        await component.Find(".editor-approve").SubmitAsync();

        navigation.Uri.ShouldBe(uriBeforeSubmit);
        component.Find(".status-message.status-error").TextContent.ShouldContain("Zatwierdzenie odrzucone.");
    }

    [Fact]
    public async Task Approve_WhenFailsWhileASavedFlagLingers_SuppressesTheStaleSaveBannerAndShowsTheError()
    {
        // `?saved=true` from an earlier save is still in the URL.
        Guid id = Guid.NewGuid();
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canApprove: true));
        _client
            .PostApiResultAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure(new() { Title = "Zatwierdzenie odrzucone." }));
        Navigation().NavigateTo($"/editor/{id}?saved=true");
        IRenderedComponent<EditorComponent> component = RenderEditor(id);

        await component.Find(".editor-approve").SubmitAsync();

        component.Find(".status-message.status-error").TextContent.ShouldContain("Zatwierdzenie odrzucone.");
        component.Markup.ShouldNotContain("Tłumaczenie zapisano");
    }

    [Fact]
    public async Task Save_WhenSaveFailsAndTheRowCannotBeReloaded_KeepsTheDraftInAResubmittableRecoveryForm()
    {
        // When the reload and the PUT both fail, the text comes back in a form that can be sent again.
        // The first GET succeeds, so the form renders; every later GET, the reload, fails.
        _client
            .GetApiResultAsync<TranslationDetailResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canEdit: true)),
                ApiResult.Failure<TranslationDetailResponse>(new() { Title = "Nie znaleziono." }));
        _client
            .PutApiResultAsync<TranslationDetailResponse>(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<TranslationDetailResponse>(new() { Title = "Zapis odrzucony." }));
        IRenderedComponent<EditorComponent> component = RenderEditor();

        await component.Find("form").SubmitAsync();
        // The state a real resubmit arrives in: the draft is kept and the load fails.
        component.Render();

        component.Find(".error-message").TextContent.ShouldContain("Zapis odrzucony.");
        IElement recoveryForm = component.Find("form");
        recoveryForm.QuerySelectorAll("input[type=hidden][name=FileIdField]").Length.ShouldBe(1);
        recoveryForm.QuerySelectorAll("input[type=hidden][name=GossipIdField]").Length.ShouldBe(1);
        recoveryForm.QuerySelectorAll("textarea[name=DraftField]").Length.ShouldBe(1);
        recoveryForm.QuerySelector("button[type=submit]")!.TextContent.ShouldContain("Zapisz ponownie");
        component.FindAll(".editor-grid").ShouldBeEmpty();
    }

    [Fact]
    public async Task Save_RecoveryForm_CapsItsTextareaAtTheDatPieceLimitToo()
    {
        // The recovery textarea is a separate control in Editor.razor, so its limit is pinned on its own.
        _client
            .GetApiResultAsync<TranslationDetailResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canEdit: true)),
                ApiResult.Failure<TranslationDetailResponse>(new() { Title = "Nie znaleziono." }));
        _client
            .PutApiResultAsync<TranslationDetailResponse>(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<TranslationDetailResponse>(new() { Title = "Zapis odrzucony." }));
        IRenderedComponent<EditorComponent> component = RenderEditor();

        await component.Find("form").SubmitAsync();
        component.Render();

        component.Find("textarea#translated-recovery").GetAttribute("maxlength")
            .ShouldBe(DatFormatConstants.MaxTranslatedTextLength.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Save_FromTheRecoveryFormWithNoLoadedRow_PutsToTheUpsertHrefFromTheServiceDocument()
    {
        // With no row, the resubmit reads the upsert rel from discovery (#610). Only a PUT to that href is
        // stubbed to succeed.
        Guid id = Guid.NewGuid();
        _client
            .GetApiResultAsync<TranslationDetailResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canEdit: true)),
                ApiResult.Failure<TranslationDetailResponse>(new() { Title = "Nie znaleziono." }));
        _client
            .PutApiResultAsync<TranslationDetailResponse>(SaveHref, Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<TranslationDetailResponse>(new() { Title = "Zapis odrzucony." }));
        _client
            .PutApiResultAsync<TranslationDetailResponse>(
                StubDiscoveryCache.HrefFor(Rels.Upsert), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(BuildDetail("Hello.", "Cześć.", canEdit: true)));
        BunitNavigationManager navigation = Navigation();
        IRenderedComponent<EditorComponent> component = RenderEditor(id);

        // The first submit is rejected, then the reload fails, so the recovery form has no row.
        await component.Find("form").SubmitAsync();
        component.Render();
        await component.Find("form").SubmitAsync();

        navigation.Uri.ShouldEndWith($"/editor/{id}?saved=true");
    }

    [Fact]
    public async Task Save_FromTheRecoveryFormWhenTheUpsertRelIsNotAdvertised_ShowsTheErrorAndDoesNotPut()
    {
        // Discovery leaves `upsert` out, as for a read-only caller. The page must say so instead of
        // sending a PUT to a guessed URL (#610).
        BunitContext context = new();
        context.Services.AddAntiforgery();
        ITranslationSystemClient client = Substitute.For<ITranslationSystemClient>();
        context.Services.AddSingleton(client);
        context.Services.AddSingleton(StubDiscoveryCache.AdvertisingGet(Rels.Translations));
        context.Services.AddScoped<TranslationEditorLoader>();
        context.AddAuthorization().SetAuthorized("Frodo");
        client
            .GetApiResultAsync<TranslationDetailResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canEdit: true)),
                ApiResult.Failure<TranslationDetailResponse>(new() { Title = "Nie znaleziono." }));
        client
            .PutApiResultAsync<TranslationDetailResponse>(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<TranslationDetailResponse>(new() { Title = "Zapis odrzucony." }));
        IRenderedComponent<EditorComponent> component =
            context.Render<EditorComponent>(parameters => parameters.Add(p => p.Id, Guid.NewGuid()));

        await component.Find("form").SubmitAsync();
        component.Render();
        await component.Find("form").SubmitAsync();

        // Only the first submit's PUT: the resubmit never reached the client.
        await client.Received(1).PutApiResultAsync<TranslationDetailResponse>(
            Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
        component.Markup.ShouldContain("Ta funkcja jest niedostępna");
    }

    [Fact]
    public async Task Approve_WhenSucceeds_RedirectsToTheRowWithAnApprovedFlagFollowingTheApproveLinkHref()
    {
        Guid id = Guid.NewGuid();
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canApprove: true));
        _client
            .PostApiResultAsync(ApproveHref, Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success());
        BunitNavigationManager navigation = Navigation();
        IRenderedComponent<EditorComponent> component = RenderEditor(id);

        await component.Find(".editor-approve").SubmitAsync();

        // Only a POST to the approve href is stubbed to succeed, so the redirect proves it was followed.
        navigation.Uri.ShouldEndWith($"/editor/{id}?approved=true");
    }

    [Fact]
    public void Render_WhenApprovedFlagIsInTheQuery_ShowsTheApproveConfirmation()
    {
        Guid id = Guid.NewGuid();
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canApprove: true));
        Navigation().NavigateTo($"/editor/{id}?approved=true");

        IRenderedComponent<EditorComponent> component = RenderEditor(id);

        component.Find(".status-message.status-success").TextContent.ShouldContain("Tłumaczenie zatwierdzono");
    }

    private BunitNavigationManager Navigation() =>
        (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<EditorComponent> RenderEditor() =>
        RenderEditor(Guid.NewGuid());

    private IRenderedComponent<EditorComponent> RenderEditor(Guid id) =>
        Render<EditorComponent>(parameters => parameters.Add(component => component.Id, id));

    private void StubLoad(TranslationDetailResponse detail)
    {
        _client
            .GetApiResultAsync<TranslationDetailResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(detail));
    }

    // Distinctive hrefs, so a test can prove the editor follows the link.
    private const string SaveHref = "https://tms.example/hateoas/translations";
    private const string ApproveHref = "https://tms.example/hateoas/translations/abc/approve";

    private static TranslationDetailResponse BuildDetail(
        string sourceText,
        string? translatedText,
        string? previousSourceText = null,
        bool canEdit = true,
        bool canApprove = false)
    {
        List<LinkDto> links = [];
        if (canEdit)
        {
            links.Add(new LinkDto(SaveHref, Rels.Upsert, "PUT"));
        }

        if (canApprove)
        {
            links.Add(new LinkDto(ApproveHref, Rels.Approve, "POST"));
        }

        return new TranslationDetailResponse(
            TranslationId.Create(Guid.NewGuid()),
            FileId: 620756992,
            GossipId: 1002,
            SourceText: sourceText,
            ArgsOrder: null,
            ArgsId: null,
            TranslatedText: translatedText,
            PreviousSourceText: previousSourceText,
            Submitter: null,
            Approver: null,
            Status: TranslationStatus.Draft,
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch)
        {
            Links = links
        };
    }
}
