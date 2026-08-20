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
/// Renders the translation list <see cref="TranslationsComponent"/> through bUnit over a stubbed TMS
/// client, to pin the link-driven actions the loader tests cannot reach (#158): the per-row Edytuj link
/// appears only when the row carries the <c>upsert</c> rel, and which pager buttons work comes from the
/// server's pagination links and not from the page and total numbers.
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
        // The list is the public, read-only face of the catalog (#309). If it ever required a login,
        // every translation would be hidden behind it again.
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
        // A soft-removed row carries only `self` and no upsert rel, so it offers no Edytuj link.
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
        // On the last page the server leaves the next-page rel out, and the control must become a
        // disabled span with no link. That follows from the missing rel and not from the page and total
        // numbers.
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
    /// There are two ways a page can show zero rows, and they are not the same (#634). A page number past
    /// the last page still carries the server's first and last links, so the pager is the only way back.
    /// A filter that matched nothing carries no link and has nowhere to go.
    /// What tells them apart is the set of links, not the number of rows, which is why the pager cannot
    /// sit inside the empty-state branch.
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
        // TotalPages is 0, so the API sends no pagination link at all: there really is nowhere to go.
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
        // The same approvable row, but the collection carries no admin rel, which is what a translator or
        // an anonymous visitor sees: a read-only list with no checkbox column and no bulk button. The
        // server decides that, not a role check here.
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
        // The guard for the SSR binding rule, like the name guard in ImportExport: the row checkbox has
        // to be named SelectedRows[<id>], so Blazor's static SSR puts the ticked rows into the
        // Dictionary<Guid, bool> property. A missing or wrong name binds nothing and bulk approve quietly
        // does nothing.
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
        // The GET half of the redirect-after-post flow (#321, #322): after a bulk approve the counts are
        // in the query string, and the list shows the "Zatwierdzono N (Pominięto M)" message with no
        // per-user state on the server. So the message survives the redirect and a reload is a plain GET.
        // The other half, from ticked rows to POST to redirect, is covered at the loader, because bUnit's
        // SubmitAsync only calls @onsubmit and does not bind SSR form data. Turning the checkboxes into
        // form data and then into the dictionary belongs to the Playwright E2E suite, which does not
        // cover this flow yet.
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
        // A valid bulk approve that published nothing, because every selected row was already approved or
        // changed between the render and the submit, redirects with approved=0. The list must then show
        // the "nothing approved" note and not a success message: approved:0 is a successful response, not
        // an error.
        StubPage(AdminPageOf(Row(canEdit: true, canApprove: true)));
        Navigation().NavigateTo("/translations?approved=0");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll(".status-message.status-success").ShouldBeEmpty();
        component.Find(".status-message.status-warning").TextContent.ShouldContain("Nie zatwierdzono żadnego");
    }

    [Fact]
    public void Render_AlwaysOffersThePageSizeSelectWithEveryOption()
    {
        // #323: the page size is now something the user picks, so the select must offer exactly the
        // allowed sizes, in order, and the UI and the query builder can never disagree.
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
        // The dropdown shows the size from the query string, which is what the user picked, and never a
        // value worked out here, so it always matches the page that was asked for (#323).
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
        // Acceptance criterion 4 (#323): a size typed by hand that we do not support falls back to the
        // default in the UI too. The select has no "7" option and marks the default as selected, and is
        // never left with nothing chosen.
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
    /// A page that shows zero rows. The set of links tells the two reasons apart: a page number past the
    /// last page still carries the first and last links, while a page with no matches carries none.
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
