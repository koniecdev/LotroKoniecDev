using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.GameVersionAggregateFactories;

/// <summary>
/// Builds HATEOAS links for the game-version aggregate: a per-item <c>self</c> plus the role-gated
/// <c>delete</c> action, and the collection's <c>self</c> plus the role-gated <c>register</c> action.
/// </summary>
internal interface IGameVersionAggregateLinkFactory
{
    /// <summary>
    /// Per-item links for one game version: <c>self</c>, plus <c>delete</c> when the caller holds the
    /// Admin role and the version is still <see cref="GameVersionStatus.Unprocessed"/> — a processed or
    /// superseded version is woven into the update lifecycle and cannot be removed.
    /// </summary>
    List<LinkDto> CreateGameVersionLinks(GameVersionId id, GameVersionStatus status, bool callerIsAdmin);

    /// <summary>
    /// Collection-level links for the game-version list: <c>self</c>, plus <c>register</c> when the
    /// caller holds the reviewer (Admin) role — the manual fallback used when the forum scrape breaks.
    /// </summary>
    List<LinkDto> CreateCollectionLinks(bool callerIsAdmin);
}
