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
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using TranslationsComponent = LotroKoniecDev.Frontend.Components.Pages.Translations.Translations;

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
        Services.AddSingleton(_client);
        Services.AddScoped<TranslationListLoader>();
        AddAuthorization().SetAuthorized("Frodo");
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
    public void Render_WhenNoPaginationRelsPresent_RendersNoPager()
    {
        StubPage(SinglePageOf(Row(canEdit: true)));

        IRenderedComponent<TranslationsComponent> component = RenderPage();

        component.FindAll("nav.pager").ShouldBeEmpty();
    }

    private IRenderedComponent<TranslationsComponent> RenderPage() =>
        Render<TranslationsComponent>();

    private void StubPage(PaginationResponse<TranslationListItemResponse> page) =>
        _client
            .GetApiResultAsync<PaginationResponse<TranslationListItemResponse>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(page));

    private static TranslationListItemResponse Row(bool canEdit)
    {
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
            Links = canEdit ? [new LinkDto("https://tms.example/api/v1/translations", Rels.Upsert, "PUT")] : []
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
