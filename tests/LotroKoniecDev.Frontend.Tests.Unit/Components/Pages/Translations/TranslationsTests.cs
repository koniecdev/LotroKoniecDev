using System.Reflection;
using AngleSharp.Dom;
using Bunit.TestDoubles;
using LotroKoniecDev.Frontend.Components.Pages.Translations;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using TranslationsComponent = LotroKoniecDev.Frontend.Components.Pages.Translations.Translations;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Translations;

/// <summary>
/// Renders the translation-list <see cref="TranslationsComponent"/> through bUnit over a stubbed TMS
/// client, locking down the link-driven affordances the loader tests cannot reach (#158): the per-row
/// Edytuj link appears iff the row carries the <c>upsert</c> rel, and the pager controls' availability
/// is driven by the server's pagination rels rather than recomputed from page/total.
/// </summary>
public sealed class TranslationsTests : BunitContext
{
    private readonly ITranslationSystemClient _client = Substitute.For<ITranslationSystemClient>();

    public TranslationsTests()
    {
        Services.AddAntiforgery();
        Services.AddSingleton(_client);
        Services.AddSingleton(StubDiscoveryCache.AdvertisingGet(Rels.Translations));
        Services.AddScoped<TranslationListLoader>();
        AddAuthorization().SetAuthorized("Frodo");
    }

    [Fact]
    public void Translations_AreBrowsableAnonymously_ByContract()
    {
        // The list is the public read-only face of the catalog (#309) — a regression to [Authorize]
        // would hide every translation behind the login wall again.
        typeof(TranslationsComponent).GetCustomAttribute<AllowAnonymousAttribute>().ShouldNotBeNull();
    }

    [Fact]
    public void Render_WhenRowCarriesTheUpsertRel_ShowsTheEditLink()
    {
        StubPage(SinglePageOf(Row(canEdit: true)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("td.col-actions a").ShouldContain(a => a.TextContent.Contains("Edytuj"));
    }

    [Fact]
    public void Render_WhenRowLacksTheUpsertRel_HidesTheEditLink()
    {
        // A soft-removed row advertises `self` only — no upsert rel — so it offers no Edytuj affordance.
        StubPage(SinglePageOf(Row(canEdit: false)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("td.col-actions a").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenNextPageRelPresent_RendersAnEnabledNextControl()
    {
        StubPage(MultiPageOf(page: 1, links: [PageLink(Rels.NextPage), PageLink(Rels.LastPage)]));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("a[rel=next]").Count.ShouldBe(1);
    }

    [Fact]
    public void Render_WhenNextPageRelAbsent_RendersADisabledNextControl()
    {
        // On the last page the server omits the next-page rel; the control must degrade to a disabled
        // span (no anchor), driven by the absent rel — not recomputed from page/total.
        StubPage(MultiPageOf(page: 2, links: [PageLink(Rels.PreviousPage), PageLink(Rels.FirstPage)]));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("a[rel=next]").ShouldBeEmpty();
        component.FindAll("span.is-disabled").ShouldContain(span => span.TextContent.Contains("Następna"));
    }

    [Fact]
    public void Render_WhenFirstPageRelAbsent_RendersADisabledFirstControl()
    {
        // The QA symptom of #545: on page 1 the server omits first-page, so "Pierwsza" must be an
        // inert span rather than a link that reloads the page the user is already on.
        StubPage(MultiPageOf(page: 1, links: [PageLink(Rels.NextPage), PageLink(Rels.LastPage)]));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("a[rel=first]").ShouldBeEmpty();
        component.FindAll("span.is-disabled").ShouldContain(span => span.TextContent.Contains("Pierwsza"));
    }

    [Fact]
    public void Render_WhenLastPageRelAbsent_RendersADisabledLastControl()
    {
        // The other half of #545, on the last page.
        StubPage(MultiPageOf(page: 2, links: [PageLink(Rels.PreviousPage), PageLink(Rels.FirstPage)]));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("a[rel=last]").ShouldBeEmpty();
        component.FindAll("span.is-disabled").ShouldContain(span => span.TextContent.Contains("Ostatnia"));
    }

    [Fact]
    public void Render_WhenBoundaryRelsPresent_RendersThemAsEnabledControls()
    {
        // A middle page carries all four, so none of them degrades to a disabled span.
        StubPage(MultiPageOf(page: 2, links:
        [
            PageLink(Rels.FirstPage), PageLink(Rels.PreviousPage), PageLink(Rels.NextPage), PageLink(Rels.LastPage)
        ]));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("a[rel=first]").Count.ShouldBe(1);
        component.FindAll("a[rel=last]").Count.ShouldBe(1);
        component.FindAll("span.is-disabled").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenNoPaginationRelsPresent_RendersNoPager()
    {
        StubPage(SinglePageOf(Row(canEdit: true)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("nav.pager").ShouldBeEmpty();
    }

    /// <summary>
    /// The two ways a page can render zero rows are not the same state (#634). Overshooting the last
    /// page still carries the server's absolute jumps, so the pager is the only way back; a filter
    /// that matched nothing carries no rel and has nowhere to go. The distinction is the rel set, not
    /// the row count — which is why the pager cannot live inside the empty-state branch.
    /// </summary>
    [Fact]
    public void Render_WhenPageIsOverRange_RendersTheEmptyStateWithAWayBack()
    {
        // ?page=99 of 3: the API omits next-page but still emits the absolute jumps plus previous-page
        StubPage(OverRangePageOf(page: 99, links:
        [
            PageLink(Rels.FirstPage), PageLink(Rels.PreviousPage), PageLink(Rels.LastPage)
        ]));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("div.empty").Count.ShouldBe(1);
        component.FindAll("a[rel=first]").Count.ShouldBe(1);
        component.FindAll("a[rel=last]").Count.ShouldBe(1);
    }

    [Fact]
    public void Render_WhenNothingMatchedTheFilter_RendersTheEmptyStateWithNoPager()
    {
        // TotalPages = 0, so the API emits no pagination rel at all — there is genuinely nowhere to go
        StubPage(OverRangePageOf(page: 1, links: []));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("div.empty").Count.ShouldBe(1);
        component.FindAll("nav.pager").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenCollectionHasBulkApproveRel_ShowsTheCheckboxColumnAndBulkButton()
    {
        // An admin's list carries the bulk-approve collection rel and an approvable row: the checkbox
        // column, a per-row checkbox and the "Zatwierdź zaznaczone" button all appear.
        StubPage(AdminPageOf(Row(canEdit: true, canApprove: true)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("th.col-check").Count.ShouldBe(1);
        component.FindAll("input[type=checkbox]").Count.ShouldBe(1);
        component.FindAll("button[type=submit]").ShouldContain(button => button.TextContent.Contains("Zatwierdź zaznaczone"));
    }

    [Fact]
    public void Render_WhenCollectionLacksBulkApproveRel_HidesTheCheckboxColumnAndButton()
    {
        // The same approvable row, but no admin collection rel (translator / anonymous): the read-only
        // list, no checkbox column and no bulk button — server-gated, never role-recomputed.
        StubPage(SinglePageOf(Row(canEdit: true, canApprove: true)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("th.col-check").ShouldBeEmpty();
        component.FindAll("input[type=checkbox]").ShouldBeEmpty();
        component.FindAll("button[type=submit]").Where(button => button.TextContent.Contains("Zatwierdź")).ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenAdminButNoRowIsApprovable_ShowsTheColumnButNoCheckboxOrButton()
    {
        // Admin, but the only row is not approvable (e.g. already Approved): the column renders, yet the
        // row offers no checkbox and there is nothing to approve, so no bulk button.
        StubPage(AdminPageOf(Row(canEdit: true, canApprove: false)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("th.col-check").Count.ShouldBe(1);
        component.FindAll("input[type=checkbox]").ShouldBeEmpty();
        component.FindAll("button[type=submit]").Where(button => button.TextContent.Contains("Zatwierdź")).ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenRowIsApprovable_NamesItsCheckboxAfterTheDictionaryKeyBinding()
    {
        // Regression guard for the SSR binding contract (mirrors the ImportExport name guard): the row
        // checkbox MUST be named SelectedRows[<id>] so Blazor static-SSR maps the checked rows into the
        // Dictionary<Guid, bool> handler property — a bare or wrong name binds nothing and bulk approve
        // silently no-ops.
        TranslationListItemResponse row = Row(canEdit: true, canApprove: true);
        StubPage(AdminPageOf(row));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.Find("input[type=checkbox]").GetAttribute("name").ShouldBe($"SelectedRows[{row.Id.Value}]");
    }

    [Fact]
    public async Task ApproveSelected_WhenNothingIsChecked_ShowsTheSelectPromptAndDoesNotCallTheApi()
    {
        StubPage(AdminPageOf(Row(canEdit: true, canApprove: true)));
        IRenderedComponent<TranslationsComponent> component = RenderPage();

        await component.Find("form[method=post]").SubmitAsync();

        component.Find(".status-message.status-warning").TextContent.ShouldContain("Zaznacz co najmniej jeden wiersz");
        await _client.DidNotReceive().PostApiResultAsync<BulkApproveTranslationsResponse>(
            Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Render_WhenApprovedCountIsInTheQuery_ShowsTheSuccessFlashWithSkippedCount()
    {
        // The GET side of Post-Redirect-Get (#321/#322): after the bulk-approve redirect the result counts
        // ride in the query, and the list surfaces the "Zatwierdzono N (Pominięto M)" confirmation with no
        // per-user server state, so the flash survives the redirect and a reload stays a safe GET.
        // (The checked-rows → POST → redirect leg itself is exercised at the loader seam — bUnit's
        // SubmitAsync invokes only @onsubmit and does not model SSR form-data binding — while the
        // DOM→form-data→dictionary bind belongs to the Playwright E2E suite, not yet populated for this flow.)
        StubPage(AdminPageOf(Row(canEdit: true, canApprove: true)));
        Navigation().NavigateTo("/translations?approved=2&skipped=1");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        string flash = component.Find(".status-message.status-success").TextContent;
        flash.ShouldContain("Zatwierdzono 2");
        flash.ShouldContain("Pominięto 1");
    }

    [Fact]
    public void Render_WhenApprovedIsZeroInTheQuery_ShowsTheNothingApprovedNoteNotASuccessFlash()
    {
        // A well-formed bulk approve that published nothing (every selected row was already approved or went
        // stale between render and submit) redirects with approved=0: the list must show the "nothing
        // approved" note, never a success flash — approved:0 is a success response, not an error.
        StubPage(AdminPageOf(Row(canEdit: true, canApprove: true)));
        Navigation().NavigateTo("/translations?approved=0");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll(".status-message.status-success").ShouldBeEmpty();
        component.Find(".status-message.status-warning").TextContent.ShouldContain("Nie zatwierdzono żadnego");
    }

    [Fact]
    public void Render_AlwaysOffersThePageSizeSelectWithEveryOption()
    {
        // #323: the fixed page size is now a user-facing control — the select must expose exactly the
        // allowlisted sizes, in order, so the UI and the query builder can never drift.
        StubPage(SinglePageOf(Row(canEdit: true)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("#pageSize option")
            .Select(option => option.GetAttribute("value")!)
            .ShouldBe(TranslationListQuery.PageSizeOptions.Select(size => size.ToString()));
    }

    [Fact]
    public void Render_WhenNoPageSizeInTheQuery_MarksTheDefaultSizeSelected()
    {
        StubPage(SinglePageOf(Row(canEdit: true)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.Find($"#pageSize option[value=\"{TranslationListQuery.DefaultPageSize}\"]")
            .HasAttribute("selected").ShouldBeTrue();
    }

    [Fact]
    public void Render_WhenPageSizeInTheQuery_MarksThatSizeSelected()
    {
        // The dropdown reflects the size requested in the query string (what the user picked), never a
        // locally recomputed value — so it always mirrors the page the caller asked for (#323).
        StubPage(SinglePageOf(Row(canEdit: true)));
        Navigation().NavigateTo("/translations?pageSize=100");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.Find("#pageSize option[value=\"100\"]").HasAttribute("selected").ShouldBeTrue();
        component.Find($"#pageSize option[value=\"{TranslationListQuery.DefaultPageSize}\"]")
            .HasAttribute("selected").ShouldBeFalse();
    }

    [Fact]
    public void Render_WhenPageSizeInTheQueryIsUnsupported_MarksTheDefaultSelectedAndRendersNoBogusOption()
    {
        // AC4 (#323): a hand-typed, unsupported size must snap to the default in the UI too — the select
        // exposes no "7" option and marks the default selected, never left with nothing chosen.
        StubPage(SinglePageOf(Row(canEdit: true)));
        Navigation().NavigateTo("/translations?pageSize=7");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("#pageSize option[value=\"7\"]").ShouldBeEmpty();
        component.Find($"#pageSize option[value=\"{TranslationListQuery.DefaultPageSize}\"]")
            .HasAttribute("selected").ShouldBeTrue();
    }

    private IRenderedComponent<TranslationsComponent> RenderPage() =>
        Render<TranslationsComponent>();

    private BunitNavigationManager Navigation() =>
        (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

    private void StubPage(PaginationResponse<TranslationListItemResponse> page) =>
        _client
            .GetApiResultAsync<PaginationResponse<TranslationListItemResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(page));

    private static TranslationListItemResponse Row(bool canEdit, bool canApprove = false)
    {
        List<LinkDto> links = [];
        if (canEdit)
        {
            links.Add(new LinkDto("https://tms.example/api/v1/translations", Rels.Upsert, "PUT"));
        }

        if (canApprove)
        {
            links.Add(new LinkDto("https://tms.example/api/v1/translations/abc/approve", Rels.Approve, "POST"));
        }

        TranslationListItemResponse row = new(
            TranslationId.Create(),
            FileId: 620756992,
            GossipId: 1001,
            SourceText: "Welcome to Middle-earth!",
            TranslatedText: "Witaj w Śródziemiu!",
            Status: TranslationStatus.Draft,
            Submitter: null,
            UpdatedAt: DateTimeOffset.UnixEpoch)
        {
            Links = links
        };
        return row;
    }

    private static LinkDto PageLink(string rel) =>
        new($"https://tms.example/api/v1/translations?rel={rel}", rel, "GET");

    private static PaginationResponse<TranslationListItemResponse> SinglePageOf(TranslationListItemResponse row) =>
        new()
        {
            Items = [row],
            Page = 1,
            PageSize = 50,
            TotalCount = 1
        };

    /// <summary>A page carrying the admin-only <c>bulk-approve</c> collection rel, as the API emits for a reviewer.</summary>
    private static PaginationResponse<TranslationListItemResponse> AdminPageOf(params TranslationListItemResponse[] rows) =>
        new()
        {
            Items = rows,
            Page = 1,
            PageSize = 50,
            TotalCount = rows.Length,
            Links = [new LinkDto(BulkApproveHref, Rels.BulkApprove, "POST")]
        };

    private const string BulkApproveHref = "https://tms.example/api/v1/translations/approve";

    /// <summary>
    /// A page that renders zero rows. The rel set is what separates the two reasons it can be empty:
    /// an over-range page carries the boundary jumps, a no-matches page carries nothing.
    /// </summary>
    private static PaginationResponse<TranslationListItemResponse> OverRangePageOf(
        int page,
        IReadOnlyCollection<LinkDto> links) =>
        new()
        {
            Items = [],
            Page = page,
            PageSize = 1,
            TotalCount = links.Count == 0 ? 0 : 3,
            Links = links
        };

    private static PaginationResponse<TranslationListItemResponse> MultiPageOf(int page, IReadOnlyCollection<LinkDto> links) =>
        new()
        {
            Items = [Row(canEdit: true)],
            Page = page,
            PageSize = 1,
            TotalCount = 2,
            Links = links
        };
}
