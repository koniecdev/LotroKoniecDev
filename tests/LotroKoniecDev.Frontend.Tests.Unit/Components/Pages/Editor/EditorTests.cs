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
/// Renders the side-by-side <see cref="EditorComponent"/> through bUnit over a stubbed TMS client, to
/// pin what the pure <see cref="PlaceholderAnalyzer"/> tests cannot reach: that every
/// <c>&lt;--DO_NOT_TOUCH!--&gt;</c> marker in the English source is wrapped in a highlight span, and
/// that a different number of placeholders in the source and in the stored Polish shows the warning
/// (the M3-04 feature).
/// It also covers the redirect after a post (#321): a successful save or approve redirects to the row's
/// GET view with a one-time success flag, while a failed save stays on the page so the typed draft is
/// not lost.
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
        // spec 0001 re-review path: a game update superseded the English; the editor keeps the previous
        // version visible for comparison, highlighting its placeholders just like the current source.
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
        // The API rejects a longer text outright (#598); the browser cap keeps a translator from
        // discovering that only after typing past it. Enforcement stays server-side.
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
        // The same logged-in caller who is not a reviewer: the button is hidden purely by the missing rel, with no
        // local role/status recomputation in the editor.
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canApprove: false));

        IRenderedComponent<EditorComponent> component = RenderEditor();

        component.FindAll(".editor-approve").ShouldBeEmpty();
    }

    [Fact]
    public async Task Save_WhenSucceeds_RedirectsToTheRowWithASavedFlagFollowingTheUpsertLinkHref()
    {
        // Redirect after post (#321): a successful save redirects to the row's GET view, so a browser
        // reload is a plain GET and the fresh load works out the approve action again. Nothing is
        // rendered inline after the post.
        Guid id = Guid.NewGuid();
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canEdit: true));
        _client
            .PutApiResultAsync<TranslationDetailResponse>(SaveHref, Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(BuildDetail("Hello.", "Cześć.", canEdit: true)));
        BunitNavigationManager navigation = Navigation();
        IRenderedComponent<EditorComponent> component = RenderEditor(id);

        // The fixture carries the upsert rel but not the approve rel, so the save form is the only form.
        await component.Find("form").SubmitAsync();

        // Behaviour-visible proof the upsert href (read from the loaded detail's links) was followed:
        // only a PUT to that exact href is stubbed to succeed, so the redirect happens iff it was used.
        navigation.Uri.ShouldEndWith($"/editor/{id}?saved=true");
    }

    [Fact]
    public void Render_WhenSavedFlagIsInTheQuery_ShowsTheSaveConfirmation()
    {
        // The GET side of PRG: the just-saved row is loaded fresh and the one-shot query flag surfaces the
        // "saved" confirmation, so the translator still gets positive feedback after the redirect.
        Guid id = Guid.NewGuid();
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canEdit: true));
        Navigation().NavigateTo($"/editor/{id}?saved=true");

        IRenderedComponent<EditorComponent> component = RenderEditor(id);

        component.Find(".status-message.status-success").TextContent.ShouldContain("Tłumaczenie zapisano");
    }

    [Fact]
    public async Task Save_WhenSaveFails_DoesNotRedirectAndShowsTheErrorInline()
    {
        // A rejected save deliberately does not redirect, so the error, and the form the translator can
        // send again, stay on screen instead of being lost in the redirect. The shape of that form is
        // pinned by the recovery test below.
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
        // The exact surface #548 reports: the API rejects an empty translation by design and used to
        // paint its English "Validation Error" into the Polish page.
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
        // Symmetric to the save-failure path: a rejected approve (API 403 / 409 / 422 / …) must not
        // redirect, so the reviewer sees the error in place rather than being bounced to a "success" GET.
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
        // The check for a flag left over in the URL: `?saved=true` from an earlier save redirect is still
        // there, so a failed approve on that page must show only its own error and never the old "saved"
        // message next to it.
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
        // The recovery promise: when both the reload and the PUT fail in the same request, the
        // translator's text must come back in a form they can send again, with the hidden key fields and
        // the textarea, instead of disappearing. The first GET succeeds, so the form renders, and every
        // later GET fails, which is the reload.
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
        // The failed save keeps the draft in the component's state, and the next render runs the load
        // again, which now fails. That is exactly the state a real resubmit request arrives in.
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
        // The recovery textarea is a second control, written separately in its own branch of
        // Editor.razor, so its length limit has to be pinned on its own. The draft it holds is sent to
        // the same API rule (#598).
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
        // With no row to carry the upsert rel, the recovery resubmit reads it from TMS discovery (#610)
        // instead of using a path written into the code. Only a PUT to that exact href is stubbed to
        // succeed, so the redirect happens only if the discovered href was the one used.
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

        // The first submit follows the loaded row's href and is rejected, then the reload fails, so the
        // recovery form has no row. That is exactly the state a real resubmit request arrives in.
        await component.Find("form").SubmitAsync();
        component.Render();
        await component.Find("form").SubmitAsync();

        navigation.Uri.ShouldEndWith($"/editor/{id}?saved=true");
    }

    [Fact]
    public async Task Save_FromTheRecoveryFormWhenTheUpsertRelIsNotAdvertised_ShowsTheErrorAndDoesNotPut()
    {
        // The other half of the recovery path: discovery answers but leaves `upsert` out, which happens
        // for a read-only caller or a session whose token is no longer accepted. The page must say so
        // instead of sending a PUT to a guessed URL (#610).
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

        // Exactly one PUT, the first submit's, which followed the loaded row's href. The recovery
        // resubmit never reached the client, because the rel could not be resolved.
        await client.Received(1).PutApiResultAsync<TranslationDetailResponse>(
            Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
        component.Markup.ShouldContain("Ta funkcja jest niedostępna");
    }

    [Fact]
    public async Task Approve_WhenSucceeds_RedirectsToTheRowWithAnApprovedFlagFollowingTheApproveLinkHref()
    {
        // Post-Redirect-Get (#321): a successful approve redirects to the row's GET view, replacing the
        // old inline reload and making a browser refresh safe.
        Guid id = Guid.NewGuid();
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canApprove: true));
        _client
            .PostApiResultAsync(ApproveHref, Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success());
        BunitNavigationManager navigation = Navigation();
        IRenderedComponent<EditorComponent> component = RenderEditor(id);

        await component.Find(".editor-approve").SubmitAsync();

        // Behaviour-visible proof the approve link href was followed: only a POST to that exact href is
        // stubbed to succeed, so the redirect happens iff the editor used it.
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

    // Deliberately distinctive server hrefs so a wiring test can prove the editor follows the link.
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
