using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.TranslationAggregateFactories;

/// <summary>
/// Builds the state- and role-aware HATEOAS link set for a single translation resource: <c>self</c>
/// plus the edit/approve transitions the caller is actually allowed to take given the row's status
/// and removal state (dead transitions are never advertised). The list is publicly readable (#309),
/// so a caller without the translator capability — an anonymous visitor — gets no links at all:
/// every advertised transition (including <c>self</c>, which targets the protected detail GET)
/// requires authentication.
/// </summary>
internal interface ITranslationAggregateLinkFactory
{
    /// <param name="id">The translation's identity (drives the <c>self</c> and <c>approve</c> hrefs).</param>
    /// <param name="status">The workflow status (gates whether <c>approve</c> is meaningful).</param>
    /// <param name="isRemoved">Whether the row is soft-removed (a removed row exposes <c>self</c> only).</param>
    /// <param name="callerIsTranslator">Whether the caller holds the Translator (or Admin) role — anonymous readers get no links.</param>
    /// <param name="callerIsAdmin">Whether the caller holds the reviewer (Admin) role (gates <c>approve</c>).</param>
    List<LinkDto> CreateTranslationLinks(
        TranslationId id,
        TranslationStatus status,
        bool isRemoved,
        bool callerIsTranslator,
        bool callerIsAdmin);

    /// <summary>
    /// Builds the collection-level links for the translation list: today only the admin-only
    /// <c>bulk-approve</c> action (#322), so a non-admin caller gets an empty list.
    /// </summary>
    /// <param name="callerIsAdmin">Whether the caller holds the reviewer (Admin) role (gates <c>bulk-approve</c>).</param>
    List<LinkDto> CreateCollectionLinks(bool callerIsAdmin);
}
