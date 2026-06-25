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
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using EditorComponent = LotroKoniecDev.Frontend.Components.Pages.Editor.Editor;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Editor;

/// <summary>
/// Renders the side-by-side <see cref="EditorComponent"/> through bUnit over a stubbed TMS client,
/// locking down the render wiring the pure <see cref="PlaceholderAnalyzer"/> tests cannot reach: that
/// every <c>&lt;--DO_NOT_TOUCH!--&gt;</c> marker in the English source is wrapped in a highlight span,
/// and that a placeholder-count mismatch between source and the persisted Polish surfaces the advisory
/// warning (the M3-04 placeholder validation feature). The authenticated-but-non-approver state keeps
/// the assertions on the highlighting path.
/// </summary>
public sealed class EditorTests : BunitContext
{
    private const string Token = "<--DO_NOT_TOUCH!-->";

    private readonly ITranslationSystemClient _client = Substitute.For<ITranslationSystemClient>();

    public EditorTests()
    {
        Services.AddAntiforgery();
        Services.AddSingleton(_client);
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

        component.Find(".status-down").TextContent.ShouldContain("Nie znaleziono.");
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
        // The same authenticated non-reviewer: the affordance is gated purely by the absent rel, with no
        // local role/status recomputation in the editor.
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canApprove: false));

        IRenderedComponent<EditorComponent> component = RenderEditor();

        component.FindAll(".editor-approve").ShouldBeEmpty();
    }

    [Fact]
    public async Task Save_WhenSubmitted_FollowsTheUpsertLinkHrefAndConfirms()
    {
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canEdit: true));
        _client
            .PutApiResultAsync<TranslationDetailResponse>(SaveHref, Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(BuildDetail("Hello.", "Cześć.", canEdit: true)));
        IRenderedComponent<EditorComponent> component = RenderEditor();

        // The fixture carries the upsert rel but not the approve rel, so the save form is the only form.
        await component.Find("form").SubmitAsync();

        // Behaviour-visible proof the upsert href (read from the loaded detail's links) was followed:
        // only a PUT to that exact href is stubbed to succeed, so the confirmation renders iff it was used.
        component.Find(".status-ok").TextContent.ShouldContain("Tłumaczenie zapisano");
    }

    [Fact]
    public async Task Approve_WhenSubmitted_FollowsTheApproveLinkHrefAndConfirms()
    {
        StubLoad(BuildDetail(sourceText: "Hello.", translatedText: "Cześć.", canApprove: true));
        _client
            .PostApiResultAsync(ApproveHref, Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success());
        IRenderedComponent<EditorComponent> component = RenderEditor();

        await component.Find(".editor-approve").SubmitAsync();

        // Behaviour-visible proof the approve link href was followed: only a POST to that exact href is
        // stubbed to succeed, so the success confirmation renders iff the editor used it.
        component.Find(".status-ok").TextContent.ShouldContain("Tłumaczenie zatwierdzono");
    }

    private IRenderedComponent<EditorComponent> RenderEditor() =>
        Render<EditorComponent>(parameters => parameters.Add(component => component.Id, Guid.NewGuid()));

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
