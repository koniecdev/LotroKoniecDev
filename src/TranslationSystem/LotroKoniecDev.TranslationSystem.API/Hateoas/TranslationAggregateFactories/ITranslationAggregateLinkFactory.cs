using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.TranslationAggregateFactories;

/// <summary>
/// Never offers an action that would lead nowhere. Every link here, <c>self</c> included, needs a login,
/// so a caller who is not a translator gets none (#309).
/// </summary>
internal interface ITranslationAggregateLinkFactory
{
    /// <param name="id">Used in the <c>self</c> and <c>approve</c> hrefs.</param>
    /// <param name="status">Decides whether <c>approve</c> makes sense.</param>
    /// <param name="isRemoved">A removed row gets only <c>self</c>.</param>
    /// <param name="callerIsTranslator">A translator or an admin. Anonymous readers get no links.</param>
    /// <param name="callerIsAdmin">Decides <c>approve</c>.</param>
    ValueTask<List<LinkDto>> CreateTranslationLinksAsync(
        TranslationId id,
        TranslationStatus status,
        bool isRemoved,
        bool callerIsTranslator,
        bool callerIsAdmin);

    /// <summary>
    /// Today only the admin action <c>bulk-approve</c> (#322).
    /// </summary>
    ValueTask<List<LinkDto>> CreateCollectionLinksAsync(bool callerIsAdmin);
}
