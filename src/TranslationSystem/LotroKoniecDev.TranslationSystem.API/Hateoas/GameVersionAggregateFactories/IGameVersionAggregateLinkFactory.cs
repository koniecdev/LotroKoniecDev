using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.GameVersionAggregateFactories;

/// <summary>
/// Builds HATEOAS links for the game-version aggregate: a per-item <c>self</c> and the collection's
/// <c>self</c> plus the role-gated <c>register</c> action.
/// </summary>
internal interface IGameVersionAggregateLinkFactory
{
    /// <summary>Per-item links for one game version (currently <c>self</c>).</summary>
    List<LinkDto> CreateGameVersionLinks(GameVersionId id);

    /// <summary>
    /// Collection-level links for the game-version list: <c>self</c>, plus <c>register</c> when the
    /// caller holds the reviewer (Admin) role — the manual fallback used when the forum scrape breaks.
    /// </summary>
    List<LinkDto> CreateCollectionLinks(bool callerIsAdmin);
}
