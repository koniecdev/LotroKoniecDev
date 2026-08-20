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

        // Anyone may browse (#309), but every link here, self included, points at an endpoint that
        // needs a login, so a caller who is not a translator gets none. The link factory already checks
        // each target endpoint's policy (#608). These role checks stay because they also carry the state
        // rules below and save work that would be thrown away.
        if (!callerIsTranslator)
        {
            return links;
        }

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(GetTranslation),
            rel: Rels.Self,
            method: HttpMethods.Get,
            values: new { id = id.Value }));

        // A soft-removed row is out of translation work and out of the distributed file (spec 0001). It
        // can be neither edited nor approved, so it gets only self.
        if (isRemoved)
        {
            return links;
        }

        // An upsert names the row by (FileId, GossipId) in the request body, so this link points at the
        // collection PUT and not at an item URL.
        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(UpsertTranslation),
            rel: Rels.Upsert,
            method: HttpMethods.Put));

        // Only a reviewer can approve, and only while Polish is waiting for review. It makes no sense on
        // an untranslated row, where there is nothing to approve, nor on one that is already approved.
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

        // Only a reviewer may bulk-approve (#322). A translator or an anonymous caller never sees this
        // action, exactly as with the per-row approve.
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
