using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.Hateoas.LinkFactories;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.TranslationAggregateFactories;

internal sealed class TranslationAggregateLinkFactory : ITranslationAggregateLinkFactory
{
    private readonly ILinkFactory _linkFactory;

    public TranslationAggregateLinkFactory(ILinkFactory linkFactory)
    {
        _linkFactory = linkFactory;
    }

    public async ValueTask<List<LinkDto>> CreateTranslationLinksAsync(
        TranslationId id,
        TranslationStatus status,
        bool isRemoved,
        bool callerIsTranslator,
        bool callerIsAdmin)
    {
        List<LinkDto> links = [];

        // ILinkFactory already checks each target's policy (#608). This role check only saves work that
        // would be thrown away.
        if (!callerIsTranslator)
        {
            return links;
        }

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(GetTranslation),
            rel: Rels.Self,
            method: HttpMethods.Get,
            values: new { id = id.Value }));

        // A soft-removed row can be neither edited nor approved (spec 0001).
        if (isRemoved)
        {
            return links;
        }

        // An upsert names the row in the body, so this is the collection PUT, not an item URL.
        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(UpsertTranslation),
            rel: Rels.Upsert,
            method: HttpMethods.Put));

        // Only while Polish is waiting for review.
        if (callerIsAdmin && status is TranslationStatus.Draft or TranslationStatus.NeedsReview)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(ApproveTranslation),
                rel: Rels.Approve,
                method: HttpMethods.Post,
                values: new { id = id.Value }));
        }

        return links;
    }

    public async ValueTask<List<LinkDto>> CreateCollectionLinksAsync(bool callerIsAdmin)
    {
        List<LinkDto> links = [];

        if (callerIsAdmin)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(BulkApproveTranslations),
                rel: Rels.BulkApprove,
                method: HttpMethods.Post));
        }

        return links;
    }
}
