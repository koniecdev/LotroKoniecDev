using System.Reflection;
using AngleSharp.Dom;
using Bunit.TestDoubles;
using LotroKoniecDev.Frontend.Components.Pages.Translations;
using LotroKoniecDev.Frontend.Infrastructure.Auth;
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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
    public void Translations_AreReadableByVisitorsAndTakeAPostOnlyWhenSignedIn_ByContract()
    {
        // The list is the public face of the catalog (#309); its forms are for a signed-in user (#1493).
        typeof(TranslationsComponent).GetCustomAttribute<AllowAnonymousAttribute>().ShouldBeNull();
        typeof(TranslationsComponent).GetCustomAttribute<AuthorizeAttribute>().ShouldNotBeNull()
            .Policy.ShouldBe(ReadOnlyForVisitorsPolicy.Name);
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
        component.FindAll("td.col-check input[type=checkbox]").Count.ShouldBe(1);
        component.FindAll("button[type=submit]").ShouldContain(button => button.TextContent.Contains("Zatwierdź zaznaczone"));
    }

    [Fact]
    public void Render_WhenCollectionLacksBulkApproveRel_HidesTheCheckboxColumnAndButton()
    {
        // What a translator or an anonymous visitor gets: the collection has no bulk-approve rel.
        StubPage(SinglePageOf(Row(canEdit: true, canApprove: true)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("th.col-check").ShouldBeEmpty();
        component.FindAll("td.col-check input[type=checkbox]").ShouldBeEmpty();
        component.FindAll("button[type=submit]").Where(button => button.TextContent.Contains("Zatwierdź")).ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenAdminButNoRowIsApprovable_ShowsTheColumnButNoCheckboxOrButton()
    {
        StubPage(AdminPageOf(Row(canEdit: true, canApprove: false)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("th.col-check").Count.ShouldBe(1);
        component.FindAll("td.col-check input[type=checkbox]").ShouldBeEmpty();
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

        component.Find("td.col-check input[type=checkbox]").GetAttribute("name").ShouldBe($"SelectedRows[{row.Id.Value}]");
    }

    [Fact]
    public async Task ApproveSelected_WhenNothingIsChecked_ShowsTheSelectPromptAndDoesNotCallTheApi()
    {
        StubPage(AdminPageOf(Row(canEdit: true, canApprove: true)));
        IRenderedComponent<TranslationsComponent> component = RenderPage();

        await component.Find("form#bulk-approve").SubmitAsync();

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

    [Fact]
    public void Render_SearchBox_SaysItAlsoFindsFileIdAndGossipId()
    {
        // #1406: the list shows both ids next to the box, and the search matches them.
        StubPage(SinglePageOf(Row(canEdit: true)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.Find("form.filter input#search").GetAttribute("placeholder").ShouldBe("Tekst, FileId lub GossipId…");
    }

    [Fact]
    public void Render_ConflictsOnlyCheckbox_SendsTrueSoTheFilterTurnsOn()
    {
        StubPage(SinglePageOf(Row(canEdit: true)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        IElement checkbox = component.Find("form.filter input#conflictsOnly");
        checkbox.GetAttribute("type").ShouldBe("checkbox");
        checkbox.GetAttribute("name").ShouldBe("conflictsOnly");
        checkbox.GetAttribute("value").ShouldBe("true");
        checkbox.HasAttribute("checked").ShouldBeFalse();
    }

    [Fact]
    public void Render_WhenConflictsOnlyIsInTheQuery_ChecksTheBoxAndAsksTheApiForConflicts()
    {
        // #1347: the admin's view of identical English texts with different Polish. Only a call that
        // carries the filter gets the page, so a rendered row proves the filter reached the API.
        _client
            .GetApiResultAsync<PaginationResponse<TranslationListItemResponse>>(
                Arg.Is<string>(uri => uri.Contains("conflictsOnly=true")),
                Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(SinglePageOf(Row(canEdit: true))));
        Navigation().NavigateTo("/translations?conflictsOnly=true");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.Find("form.filter input#conflictsOnly").HasAttribute("checked").ShouldBeTrue();
        component.FindAll("table.data-table tbody tr").Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("conflictsOnly=true", true)]
    [InlineData("conflictsOnly=True", true)]
    [InlineData("conflictsOnly=false", false)]
    [InlineData("conflictsOnly=on", false)]
    [InlineData("conflictsOnly=1", false)]
    [InlineData("conflictsOnly=", false)]
    [InlineData("conflictsOnly", false)]
    [InlineData("conflictsOnly=abc", false)]
    public void Render_WithAnyConflictsOnlyValueInTheUrl_ShowsTheListAndReadsOnlyTrueAsOn(string query, bool expectedOn)
    {
        // A hand-typed or old link must never break the page; any value but "true" means off.
        StubPage(SinglePageOf(Row(canEdit: true)));
        Navigation().NavigateTo($"/translations?{query}");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("table.data-table tbody tr").Count.ShouldBe(1);
        component.Find("form.filter input#conflictsOnly").HasAttribute("checked").ShouldBe(expectedOn);
    }

    [Fact]
    public void Render_EditLink_CarriesTheListStateToTheEditor()
    {
        // #1452: the editor builds its way back from these fields.
        TranslationListItemResponse row = Row(canEdit: true);
        StubPage(SinglePageOf(row));
        Navigation().NavigateTo(
            "/translations?search=Frodo&status=Draft&page=2&pageSize=25&conflictsOnly=true&match=WholeWord&mine=true");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.Find("td.col-actions a").GetAttribute("href")
            .ShouldBe($"/editor/{row.Id.Value}?page=2&search=Frodo&match=WholeWord&status=Draft&conflictsOnly=true&mine=true&pageSize=25");
    }

    [Fact]
    public void Render_EditLinkOnTheDefaultList_IsTheBareEditorAddress()
    {
        TranslationListItemResponse row = Row(canEdit: true);
        StubPage(SinglePageOf(row));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.Find("td.col-actions a").GetAttribute("href").ShouldBe($"/editor/{row.Id.Value}");
    }

    [Fact]
    public void Render_EditLinkAfterABulkApprove_LeavesTheApprovalCountsOut()
    {
        // The editor reads "approved" as its own flag, so the list's count must never reach it.
        TranslationListItemResponse row = Row(canEdit: true, canApprove: true);
        StubPage(AdminPageOf(row));
        Navigation().NavigateTo("/translations?search=Frodo&approved=2&skipped=1");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.Find("td.col-actions a").GetAttribute("href").ShouldBe($"/editor/{row.Id.Value}?search=Frodo");
    }

    [Fact]
    public void Render_AtTheEditorsBackLink_ShowsTheSameListAgain()
    {
        // The back link and the list page must agree on every name. Only the API call that carries the
        // whole state gets the page, so a rendered row proves the list read every field.
        TranslationListQuery state = TranslationListQuery.From(
            search: "Frodo & Sam", status: "Draft", page: 2, pageSize: 25, conflictsOnly: true,
            match: TranslationSearchMatch.WholeText, mine: true);
        _client
            .GetApiResultAsync<PaginationResponse<TranslationListItemResponse>>(
                state.ToApiUri(StubDiscoveryCache.HrefFor(Rels.Translations)),
                Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(SinglePageOf(Row(canEdit: true))));
        Navigation().NavigateTo(state.ToListRelativeUri());

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("table.data-table tbody tr").Count.ShouldBe(1);
        component.Find("form.filter input#search").GetAttribute("value").ShouldBe("Frodo & Sam");
        component.Find("#status option[value=\"Draft\"]").HasAttribute("selected").ShouldBeTrue();
        component.Find("#pageSize option[value=\"25\"]").HasAttribute("selected").ShouldBeTrue();
        component.Find("form.filter input#conflictsOnly").HasAttribute("checked").ShouldBeTrue();
        component.Find("form.filter select#match option[value=\"WholeText\"]").HasAttribute("selected").ShouldBeTrue();
        component.Find("form.filter input#mine").HasAttribute("checked").ShouldBeTrue();
    }

    [Fact]
    public void Render_AlwaysOffersTheMatchSelectRightAfterTheSearchBox()
    {
        StubPage(SinglePageOf(Row(canEdit: true)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        IElement select = component.Find("form.filter select#match");
        select.GetAttribute("name").ShouldBe("match");
        component.Find("label[for=match]").TextContent.ShouldBe("Dopasowanie");
        select.QuerySelectorAll("option")
            .Select(option => (option.GetAttribute("value"), option.TextContent.Trim()))
            .ShouldBe([("", "Zawiera"), ("WholeWord", "Całe słowo"), ("WholeText", "Cały tekst")]);
        select.QuerySelector("option[value=\"\"]").ShouldNotBeNull().HasAttribute("selected").ShouldBeTrue();
        component.FindAll("form.filter > .field")
            .Select(field => field.QuerySelector("input, select")?.Id)
            .Take(2)
            .ShouldBe(["search", "match"]);
    }

    [Theory]
    [InlineData("WholeWord")]
    [InlineData("WholeText")]
    public void Render_WhenAMatchModeIsInTheQuery_SelectsItAndAsksTheApiForIt(string mode)
    {
        // Only a call that carries the mode gets the page, so a rendered row proves the mode reached the API.
        _client
            .GetApiResultAsync<PaginationResponse<TranslationListItemResponse>>(
                Arg.Is<string>(uri => uri.Contains($"match={mode}")),
                Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(SinglePageOf(Row(canEdit: true))));
        Navigation().NavigateTo($"/translations?search=start&match={mode}");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("form.filter select#match option[selected]")
            .Select(option => option.GetAttribute("value"))
            .ShouldBe([mode]);
        component.FindAll("table.data-table tbody tr").Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("match=7")]
    [InlineData("match=1")]
    [InlineData("match=banana")]
    [InlineData("match=")]
    [InlineData("match")]
    public void Render_WithAnUnknownMatchInTheUrl_ShowsTheListAndFallsBackToContains(string query)
    {
        // A hand-typed link must never break the page, and the API must never see a mode it would refuse.
        _client
            .GetApiResultAsync<PaginationResponse<TranslationListItemResponse>>(
                Arg.Is<string>(uri => !uri.Contains("match=")),
                Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(SinglePageOf(Row(canEdit: true))));
        Navigation().NavigateTo($"/translations?search=start&{query}");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("table.data-table tbody tr").Count.ShouldBe(1);
        component.Find("form.filter select#match option[value=\"\"]").HasAttribute("selected").ShouldBeTrue();
    }

    [Fact]
    public void Render_ForASignedInUser_OffersTheMineCheckboxRightAfterTheConflictsOne()
    {
        // #1491: "Tylko moje" sends "true", the only value that turns it on, as the conflicts box does.
        StubPage(SinglePageOf(Row(canEdit: true)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        IElement checkbox = component.Find("form.filter input#mine");
        checkbox.GetAttribute("type").ShouldBe("checkbox");
        checkbox.GetAttribute("name").ShouldBe("mine");
        checkbox.GetAttribute("value").ShouldBe("true");
        checkbox.HasAttribute("checked").ShouldBeFalse();
        component.Find("form.filter .field-mine label.checkbox").TextContent.Trim().ShouldBe("Tylko moje");
        component.Find("form.filter > .field-conflicts").NextElementSibling.ShouldNotBeNull()
            .ClassList.ShouldContain("field-mine");
    }

    [Fact]
    public void Render_ForAVisitor_HidesTheMineCheckbox()
    {
        AddAuthorization().SetNotAuthorized();
        StubPage(SinglePageOf(Row(canEdit: true)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("form.filter input#mine").ShouldBeEmpty();
        component.FindAll("form.filter input#conflictsOnly").Count.ShouldBe(1);
    }

    [Fact]
    public void Render_WhenMineIsInTheQuery_ChecksTheBoxAndAsksTheApiForTheCallersRows()
    {
        // Only a call that carries the option gets the page, so a rendered row proves it reached the API.
        _client
            .GetApiResultAsync<PaginationResponse<TranslationListItemResponse>>(
                Arg.Is<string>(uri => uri.Contains("mine=true")),
                Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(SinglePageOf(Row(canEdit: true))));
        Navigation().NavigateTo("/translations?mine=true");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.Find("form.filter input#mine").HasAttribute("checked").ShouldBeTrue();
        component.FindAll("table.data-table tbody tr").Count.ShouldBe(1);
    }

    [Fact]
    public void Render_ForAVisitorWithMineInTheQuery_ShowsTheWholeListAndLeavesMineOutOfEveryLink()
    {
        // #1491: a visitor saved nothing, so the page drops the option and shows the full list.
        AddAuthorization().SetNotAuthorized();
        TranslationListItemResponse row = Row(canEdit: true);
        _client
            .GetApiResultAsync<PaginationResponse<TranslationListItemResponse>>(
                Arg.Is<string>(uri => !uri.Contains("mine")),
                Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(new PaginationResponse<TranslationListItemResponse>
            {
                Items = [row],
                Page = 1,
                PageSize = 1,
                TotalCount = 2,
                Links = [PageLink(Rels.NextPage), PageLink(Rels.LastPage)]
            }));
        Navigation().NavigateTo("/translations?search=axe&mine=true");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("table.data-table tbody tr").Count.ShouldBe(1);
        component.Find("a[rel=next]").GetAttribute("href").ShouldBe("/translations?page=2&search=axe");
        component.Find("td.col-actions a").GetAttribute("href").ShouldBe($"/editor/{row.Id.Value}?search=axe");
    }

    [Fact]
    public void Render_WhenMineIsInTheQuery_ThePagerLinksKeepIt()
    {
        StubPage(MultiPageOf(page: 1, links: [PageLink(Rels.NextPage), PageLink(Rels.LastPage)]));
        Navigation().NavigateTo("/translations?mine=true");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.Find("a[rel=next]").GetAttribute("href").ShouldBe("/translations?page=2&mine=true");
        component.Find("a[rel=last]").GetAttribute("href").ShouldBe("/translations?page=2&mine=true");
    }

    [Theory]
    [InlineData("mine=true", true)]
    [InlineData("mine=True", true)]
    [InlineData("mine=false", false)]
    [InlineData("mine=on", false)]
    [InlineData("mine=1", false)]
    [InlineData("mine=", false)]
    [InlineData("mine", false)]
    public void Render_WithAnyMineValueInTheUrl_ShowsTheListAndReadsOnlyTrueAsOn(string query, bool expectedOn)
    {
        StubPage(SinglePageOf(Row(canEdit: true)));
        Navigation().NavigateTo($"/translations?{query}");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("table.data-table tbody tr").Count.ShouldBe(1);
        component.Find("form.filter input#mine").HasAttribute("checked").ShouldBe(expectedOn);
    }

    [Fact]
    public void Render_ForAnAdmin_KeepsTheTableOutsideTheBulkApproveFormAndJoinsTheCheckboxesByItsId()
    {
        // #1493: each row holds its own save form, and the HTML parser drops a form inside another form.
        StubPage(AdminPageOf(Row(canEdit: true, canApprove: true)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.Find("form#bulk-approve button[type=submit]").TextContent.ShouldContain("Zatwierdź zaznaczone");
        component.Find("table.data-table").Closest("form").ShouldBeNull();
        component.Find("td.col-check input[type=checkbox]").GetAttribute("form").ShouldBe("bulk-approve");
    }

    [Fact]
    public void Render_BulkApproveFormAction_LeavesTheNotesOfTheAddressOut()
    {
        // A post after an ended session comes back from the sign-in to this address, so an old note there
        // would read as the answer to a click that never ran (#1493).
        StubPage(AdminPageOf(Row(canEdit: true, canApprove: true)));
        Navigation().NavigateTo($"/translations?search=Frodo&page=2&approved=2&skipped=1&savedRow={Guid.NewGuid()}&savedSlips=true");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.Find("form#bulk-approve").GetAttribute("action").ShouldBe("/translations?page=2&search=Frodo");
    }

    [Fact]
    public void Render_WhenRowCarriesTheUpsertRel_OffersItsOwnSaveFormUnderThePolishText()
    {
        TranslationListItemResponse row = Row(canEdit: true);
        StubPage(SinglePageOf(row));
        Navigation().NavigateTo("/translations?search=Frodo&page=2&pageSize=25");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        IElement details = component.Find($"tr#row-{row.Id.Value} details.inline-edit");
        details.HasAttribute("open").ShouldBeFalse();
        details.QuerySelector("summary").ShouldNotBeNull().TextContent.ShouldBe("Edytuj tutaj");
        IElement form = details.QuerySelector("form").ShouldNotBeNull();
        form.GetAttribute("method").ShouldBe("post");
        form.GetAttribute("action").ShouldBe($"/translations?page=2&search=Frodo&pageSize=25#row-{row.Id.Value}");
        form.QuerySelector("input[name=InlineFileId]").ShouldNotBeNull().GetAttribute("value").ShouldBe("620756992");
        form.QuerySelector("input[name=InlineGossipId]").ShouldNotBeNull().GetAttribute("value").ShouldBe("1001");
        IElement text = form.QuerySelector("textarea[name=InlineText]").ShouldNotBeNull();
        text.TextContent.ShouldBe("Witaj w Śródziemiu!");
        form.QuerySelector($"label[for='{text.Id}']").ShouldNotBeNull()
            .TextContent.ShouldBe("Tłumaczenie polskie, FileId 620756992, GossipId 1001");
        form.QuerySelector("button[type=submit]").ShouldNotBeNull().TextContent.ShouldBe("Zapisz");
    }

    [Fact]
    public void Render_WhenRowLacksTheUpsertRel_OffersNoSaveForm()
    {
        // The server gives `upsert` only to a caller who may save the row (ADR-0040), so a visitor's row and a
        // removed row come without it.
        TranslationListItemResponse row = Row(canEdit: false);
        StubPage(SinglePageOf(row));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll($"tr#row-{row.Id.Value}").Count.ShouldBe(1);
        component.FindAll("details.inline-edit").ShouldBeEmpty();
        component.FindAll("form[method=post]").ShouldBeEmpty();
    }

    [Fact]
    public void Render_SaveForms_CarryTheRowAboveAndAFieldIdOfTheirOwn()
    {
        TranslationListItemResponse first = Row(canEdit: true);
        TranslationListItemResponse second = Row(canEdit: true);
        StubPage(PageOf(first, second));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        IElement firstForm = component.Find($"tr#row-{first.Id.Value} form");
        IElement secondForm = component.Find($"tr#row-{second.Id.Value} form");
        firstForm.QuerySelector("input[name=InlineSavedAfter]").ShouldBeNull();
        secondForm.QuerySelector("input[name=InlineSavedAfter]").ShouldNotBeNull()
            .GetAttribute("value").ShouldBe(first.Id.Value.ToString());
        firstForm.QuerySelector("textarea").ShouldNotBeNull().Id
            .ShouldNotBe(secondForm.QuerySelector("textarea").ShouldNotBeNull().Id);
    }

    [Fact]
    public void Render_SaveFormAction_LeavesTheNotesOfTheAddressOut()
    {
        TranslationListItemResponse row = Row(canEdit: true);
        StubPage(SinglePageOf(row));
        Navigation().NavigateTo($"/translations?search=Frodo&savedRow={Guid.NewGuid()}&savedSlips=true&approved=2&skipped=1");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.Find("details.inline-edit form").GetAttribute("action")
            .ShouldBe($"/translations?page=1&search=Frodo#row-{row.Id.Value}");
    }

    [Fact]
    public void Render_AfterTheSaveOfARowStillOnThePage_ConfirmsItAndShowsItsSlipsUnderIt()
    {
        // The GET half of the redirect after an inline save (#1493); the host suite covers the post.
        TranslationListItemResponse saved = Row(canEdit: true, translatedText: "Witaj  w Śródziemiu!");
        TranslationListItemResponse other = Row(canEdit: true);
        StubPage(PageOf(saved, other));
        Navigation().NavigateTo($"/translations?savedRow={saved.Id.Value}");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        IElement note = component.Find($"tr#row-{saved.Id.Value}").NextElementSibling.ShouldNotBeNull();
        note.ClassList.ShouldContain("row-note");
        note.QuerySelector(".status-message.status-success").ShouldNotBeNull().TextContent.Trim()
            .ShouldBe("Tłumaczenie zapisano.");
        note.QuerySelector(".translation-slips").ShouldNotBeNull().TextContent
            .ShouldContain("W tłumaczeniu są co najmniej dwie spacje z rzędu, a w angielskim nie ma.");
        component.FindAll("tr.row-note").Count.ShouldBe(1);
    }

    [Fact]
    public void Render_AfterTheSaveOfARowWithNoSlips_ConfirmsItWithNoWarning()
    {
        TranslationListItemResponse saved = Row(canEdit: true);
        StubPage(SinglePageOf(saved));
        Navigation().NavigateTo($"/translations?savedRow={saved.Id.Value}&savedAfter={Guid.NewGuid()}");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.Find("tr.row-note").TextContent.ShouldContain("Tłumaczenie zapisano.");
        component.FindAll(".translation-slips").ShouldBeEmpty();
    }

    [Fact]
    public void Render_AfterASaveThatTookTheRowOffThePage_ShowsTheNoteInItsPlace()
    {
        // A save makes the row a draft, so a status filter drops it from the list (wiki Mechanizm-diff).
        TranslationListItemResponse above = Row(canEdit: true);
        TranslationListItemResponse below = Row(canEdit: true);
        Guid saved = Guid.NewGuid();
        StubPage(PageOf(above, below));
        Navigation().NavigateTo($"/translations?status=Untranslated&savedRow={saved}&savedAfter={above.Id.Value}");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        IElement note = component.Find($"tr#row-{above.Id.Value}").NextElementSibling.ShouldNotBeNull();
        note.Id.ShouldBe($"row-{saved}");
        note.TextContent.ShouldContain("Tłumaczenie zapisano. Tego wiersza nie ma już na tej stronie listy.");
        note.QuerySelectorAll("a").ShouldBeEmpty();
        note.NextElementSibling.ShouldNotBeNull().Id.ShouldBe($"row-{below.Id.Value}");
    }

    [Fact]
    public void Render_AfterASaveThatTookTheRowOffThePageWithNoRowAboveLeft_ShowsTheNoteFirst()
    {
        TranslationListItemResponse row = Row(canEdit: true);
        Guid saved = Guid.NewGuid();
        StubPage(SinglePageOf(row));
        Navigation().NavigateTo($"/translations?savedRow={saved}&savedAfter={Guid.NewGuid()}");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("table.data-table tbody tr")[0].Id.ShouldBe($"row-{saved}");
        component.FindAll("tr.row-note").Count.ShouldBe(1);
    }

    [Fact]
    public void Render_AfterASaveWithSlipsThatTookTheRowOffThePage_PointsToTheEditorWithTheListState()
    {
        Guid saved = Guid.NewGuid();
        StubPage(SinglePageOf(Row(canEdit: true)));
        Navigation().NavigateTo($"/translations?search=Frodo&savedRow={saved}&savedSlips=true");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        IElement note = component.Find($"tr#row-{saved}");
        note.QuerySelector(".status-message.status-warning").ShouldNotBeNull().TextContent
            .ShouldContain("Edytor pokazuje uwagi do tego tekstu.");
        IElement link = note.QuerySelector("a").ShouldNotBeNull();
        link.TextContent.ShouldBe("Otwórz w edytorze");
        link.GetAttribute("href").ShouldBe($"/editor/{saved}?search=Frodo");
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("")]
    public void Render_WithASavedSlipsValueThatIsNotTrue_ShowsTheNoteWithNoEditorPointer(string value)
    {
        // Only "true" turns it on, as for the list's own flags; anything else still shows the list.
        Guid saved = Guid.NewGuid();
        StubPage(SinglePageOf(Row(canEdit: true)));
        Navigation().NavigateTo($"/translations?savedRow={saved}&savedSlips={value}");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        IElement note = component.Find($"tr#row-{saved}");
        note.TextContent.ShouldContain("Tłumaczenie zapisano.");
        note.QuerySelectorAll("a").ShouldBeEmpty();
    }

    [Fact]
    public void Render_AfterASaveThatLeftThePageEmpty_ShowsTheNoteAboveTheEmptyState()
    {
        Guid saved = Guid.NewGuid();
        StubPage(OverRangePageOf(page: 1, links: []));
        Navigation().NavigateTo($"/translations?savedRow={saved}");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        IElement note = component.Find($"#row-{saved}");
        note.TextContent.ShouldContain("Tłumaczenie zapisano.");
        note.NextElementSibling.ShouldNotBeNull().ClassList.ShouldContain("empty");
    }

    [Theory]
    [InlineData("savedRow=abc")]
    [InlineData("savedRow=")]
    [InlineData("savedRow=12")]
    public void Render_WithASavedRowThatIsNoRowId_ShowsTheListWithNoNote(string query)
    {
        StubPage(SinglePageOf(Row(canEdit: true)));
        Navigation().NavigateTo($"/translations?{query}");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("table.data-table tbody tr").Count.ShouldBe(1);
        component.FindAll("tr.row-note").ShouldBeEmpty();
    }

    [Fact]
    public void Render_AfterASaveWhenTheListFailsToLoad_StillConfirmsTheSave()
    {
        // #1122: the save happened, even when the reload after its redirect fails.
        _client
            .GetApiResultAsync<PaginationResponse<TranslationListItemResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<PaginationResponse<TranslationListItemResponse>>(
                new ProblemDetails { Status = StatusCodes.Status503ServiceUnavailable }));
        Navigation().NavigateTo($"/translations?savedRow={Guid.NewGuid()}");

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.Find(".status-message.status-success").TextContent.Trim().ShouldBe("Tłumaczenie zapisano.");
        component.FindAll(".error-message").Count.ShouldBe(1);
    }

    private IRenderedComponent<TranslationsComponent> RenderPage() =>
        Render<TranslationsComponent>();

    private BunitNavigationManager Navigation() =>
        (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

    private void StubPage(PaginationResponse<TranslationListItemResponse> page) =>
        _client
            .GetApiResultAsync<PaginationResponse<TranslationListItemResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(page));

    private static TranslationListItemResponse Row(
        bool canEdit,
        bool canApprove = false,
        string? translatedText = "Witaj w Śródziemiu!")
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
            TranslatedText: translatedText,
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

    private static PaginationResponse<TranslationListItemResponse> PageOf(params TranslationListItemResponse[] rows) =>
        new()
        {
            Items = rows,
            Page = 1,
            PageSize = 50,
            TotalCount = rows.Length
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
