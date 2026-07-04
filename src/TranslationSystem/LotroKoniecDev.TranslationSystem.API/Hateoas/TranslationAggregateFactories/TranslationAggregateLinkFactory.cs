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

    public List<LinkDto> CreateTranslationLinks(
        TranslationId id,
        TranslationStatus status,
        bool isRemoved,
        bool callerIsTranslator,
        bool callerIsAdmin)
    {
        List<LinkDto> links = [];

        // Anonymous read-only browsing (#309): every transition — including self, whose target GET
        // is translator-only — needs authentication, so a non-translator caller gets no links.
        if (!callerIsTranslator)
        {
            return links;
        }

        links.AddIfPresent(_linkFactory.Create(
            endpoint: nameof(GetTranslation),
            rel: Rels.Self,
            method: HttpMethods.Get,
            values: new { id = id.Value }));

        // A soft-removed row is cut from translation work and the distributed file (spec 0001):
        // it can be neither edited nor approved, so it advertises self only.
        if (isRemoved)
        {
            return links;
        }

        // Upsert is keyed by (FileId, GossipId) in the request body, so the rel targets the
        // collection PUT rather than an item URL.
        links.AddIfPresent(_linkFactory.Create(
            endpoint: nameof(UpsertTranslation),
            rel: Rels.Upsert,
            method: HttpMethods.Put));

        // Approve is reviewer-only and meaningful only while Polish awaits review — never on an
        // untranslated row (nothing to approve) nor an already-approved one (idempotent dead end).
        if (callerIsAdmin && status is TranslationStatus.Draft or TranslationStatus.NeedsReview)
        {
            links.AddIfPresent(_linkFactory.Create(
                endpoint: nameof(ApproveTranslation),
                rel: Rels.Approve,
                method: HttpMethods.Post,
                values: new { id = id.Value }));
        }

        return links;
    }

    public List<LinkDto> CreateCollectionLinks(bool callerIsAdmin)
    {
        List<LinkDto> links = [];

        // The bulk-approve action is reviewer-only (#322): a translator or anonymous caller never
        // sees the collection affordance, mirroring the per-item approve gate.
        if (callerIsAdmin)
        {
            links.AddIfPresent(_linkFactory.Create(
                endpoint: nameof(BulkApproveTranslations),
                rel: Rels.BulkApprove,
                method: HttpMethods.Post));
        }

        return links;
    }
}
