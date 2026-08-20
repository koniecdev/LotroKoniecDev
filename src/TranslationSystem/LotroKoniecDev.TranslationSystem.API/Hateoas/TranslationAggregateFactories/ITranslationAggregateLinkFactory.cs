using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.TranslationAggregateFactories;

/// <summary>
/// Builds the links for one translation. It emits <c>self</c> plus the edit and approve actions the
/// caller may really take, given the row's status and whether it was removed. Actions that would lead
/// nowhere are never offered.
/// Anyone may read the list (#309), so a caller who is not a translator, such as an anonymous visitor,
/// gets no links at all. Every link here, <c>self</c> included, points at an endpoint that needs a
/// login.
/// </summary>
internal interface ITranslationAggregateLinkFactory
{
    /// <param name="id">The translation's id, used in the <c>self</c> and <c>approve</c> hrefs.</param>
    /// <param name="status">The status, which decides whether <c>approve</c> makes sense.</param>
    /// <param name="isRemoved">Whether the row is soft-removed. A removed row gets only <c>self</c>.</param>
    /// <param name="callerIsTranslator">Whether the caller is a translator or an admin. Anonymous readers get no links.</param>
    /// <param name="callerIsAdmin">Whether the caller is an admin, which decides <c>approve</c>.</param>
    ValueTask<List<LinkDto>> CreateTranslationLinksAsync(
        TranslationId id,
        TranslationStatus status,
        bool isRemoved,
        bool callerIsTranslator,
        bool callerIsAdmin);

    /// <summary>
    /// Builds the links for the translation list itself. Today that is only the admin action
    /// <c>bulk-approve</c> (#322), so anyone else gets an empty list.
    /// </summary>
    /// <param name="callerIsAdmin">Whether the caller is an admin, which decides <c>bulk-approve</c>.</param>
    ValueTask<List<LinkDto>> CreateCollectionLinksAsync(bool callerIsAdmin);
}
