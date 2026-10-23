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
/// Pins the link-driven actions the loader tests cannot reach (#158): the Edytuj link, the pager buttons
/// and the bulk approve all follow the server's links.
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
        // The list is the public face of the catalog (#309).
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
        StubPage(MultiPageOf(page: 2, links: [PageLink(Rels.PreviousPage), PageLink(Rels.FirstPage)]));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("a[rel=next]").ShouldBeEmpty();
        component.FindAll("span.is-disabled").ShouldContain(span => span.TextContent.Contains("Następna"));
    }

    [Fact]
    public void Render_WhenFirstPageRelAbsent_RendersADisabledFirstControl()
    {
        // #545: a "Pierwsza" link on page 1 would only reload the same page.
        StubPage(MultiPageOf(page: 1, links: [PageLink(Rels.NextPage), PageLink(Rels.LastPage)]));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("a[rel=first]").ShouldBeEmpty();
        component.FindAll("span.is-disabled").ShouldContain(span => span.TextContent.Contains("Pierwsza"));
    }

    [Fact]
    public void Render_WhenLastPageRelAbsent_RendersADisabledLastControl()
    {
        StubPage(MultiPageOf(page: 2, links: [PageLink(Rels.PreviousPage), PageLink(Rels.FirstPage)]));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("a[rel=last]").ShouldBeEmpty();
        component.FindAll("span.is-disabled").ShouldContain(span => span.TextContent.Contains("Ostatnia"));
    }

    [Fact]
    public void Render_WhenBoundaryRelsPresent_RendersThemAsEnabledControls()
    {
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
    /// #634: a page past the end still carries the first and last links, so the pager is the only way
    /// back. The links, not the row count, tell it apart from a filter that matched nothing.
    /// </summary>
    [Fact]
    public void Render_WhenPageIsOverRange_RendersTheEmptyStateWithAWayBack()
    {
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
        StubPage(OverRangePageOf(page: 1, links: []));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("div.empty").Count.ShouldBe(1);
        component.FindAll("nav.pager").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenCollectionHasBulkApproveRel_ShowsTheCheckboxColumnAndBulkButton()
    {
        StubPage(AdminPageOf(Row(canEdit: true, canApprove: true)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("th.col-check").Count.ShouldBe(1);
        component.FindAll("input[type=checkbox]").Count.ShouldBe(1);
        component.FindAll("button[type=submit]").ShouldContain(button => button.TextContent.Contains("Zatwierdź zaznaczone"));
    }

    [Fact]
    public void Render_WhenCollectionLacksBulkApproveRel_HidesTheCheckboxColumnAndButton()
    {
        // What a translator or an anonymous visitor gets: the collection has no bulk-approve rel.
        StubPage(SinglePageOf(Row(canEdit: true, canApprove: true)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("th.col-check").ShouldBeEmpty();
        component.FindAll("input[type=checkbox]").ShouldBeEmpty();
        component.FindAll("button[type=submit]").Where(button => button.TextContent.Contains("Zatwierdź")).ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenAdminButNoRowIsApprovable_ShowsTheColumnButNoCheckboxOrButton()
    {
        StubPage(AdminPageOf(Row(canEdit: true, canApprove: false)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("th.col-check").Count.ShouldBe(1);
        component.FindAll("input[type=checkbox]").ShouldBeEmpty();
        component.FindAll("button[type=submit]").Where(button => button.TextContent.Contains("Zatwierdź")).ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenRowIsApprovable_NamesItsCheckboxAfterTheDictionaryKeyBinding()
    {
        // Static SSR binds the ticked rows only under the name SelectedRows[<id>]. A wrong name binds
        // nothing, and bulk approve quietly does nothing.
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
        // The GET half of the redirect after post (#321). The POST half is covered at the loader, because
        // bUnit's SubmitAsync does not bind SSR form data.
        StubPage(AdminPageOf(Row(canEdit: true, canApprove: true)));
        Navigation().NavigateTo("/translations?approved=2&skipped=1");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        string flash = component.Find(".status-message.status-success").TextContent;
        flash.ShouldContain("Zatwierdzono 2");
        flash.ShouldContain("Pominięto 1");
    }

    [Theory]
    [InlineData(1, "1 tłumaczenie")]
    [InlineData(2, "2 tłumaczenia")]
    [InlineData(3, "3 tłumaczenia")]
    [InlineData(4, "4 tłumaczenia")]
    [InlineData(5, "5 tłumaczeń")]
    [InlineData(11, "11 tłumaczeń")]
    [InlineData(12, "12 tłumaczeń")]
    [InlineData(13, "13 tłumaczeń")]
    [InlineData(14, "14 tłumaczeń")]
    [InlineData(21, "21 tłumaczeń")]
    [InlineData(22, "22 tłumaczenia")]
    [InlineData(23, "23 tłumaczenia")]
    [InlineData(24, "24 tłumaczenia")]
    [InlineData(25, "25 tłumaczeń")]
    [InlineData(101, "101 tłumaczeń")]
    [InlineData(112, "112 tłumaczeń")]
    [InlineData(122, "122 tłumaczenia")]
    public void Render_WhenApprovedCountIsInTheQuery_UsesThePolishPluralForTheCount(int approved, string expected)
    {
        // The auth API has its own copy of this rule (PolishPlural, #1032), so both are pinned on the
        // numbers a short "2 to 4" rule gets wrong.
        StubPage(AdminPageOf(Row(canEdit: true, canApprove: true)));
        Navigation().NavigateTo($"/translations?approved={approved}");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.Find(".status-message.status-success").TextContent.ShouldContain($"Zatwierdzono {expected}.");
    }

    [Fact]
    public void Render_WhenApprovedIsZeroInTheQuery_ShowsTheNothingApprovedNoteNotASuccessFlash()
    {
        // approved=0 is a successful answer, for rows that changed between the render and the submit.
        StubPage(AdminPageOf(Row(canEdit: true, canApprove: true)));
        Navigation().NavigateTo("/translations?approved=0");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll(".status-message.status-success").ShouldBeEmpty();
        component.Find(".status-message.status-warning").TextContent.ShouldContain("Nie zatwierdzono żadnego");
    }

    [Fact]
    public void Render_AlwaysOffersThePageSizeSelectWithEveryOption()
    {
        // #323: the UI and the query builder must never disagree.
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
        // #323: the select is never left with nothing chosen.
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

    /// <summary>A page carrying the admin-only <c>bulk-approve</c> rel.</summary>
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
