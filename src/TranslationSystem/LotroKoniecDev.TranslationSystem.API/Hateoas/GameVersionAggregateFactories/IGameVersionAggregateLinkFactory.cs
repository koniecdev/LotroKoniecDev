using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.GameVersionAggregateFactories;

/// <summary>
/// Builds the links for game versions: <c>self</c> on each item plus <c>delete</c> for an admin, and
/// <c>self</c> on the collection plus <c>register</c> for an admin.
/// </summary>
internal interface IGameVersionAggregateLinkFactory
{
    /// <summary>
    /// The links for one game version: <c>self</c>, plus <c>delete</c> when the caller is an admin and
    /// the version can still be deleted, plus <c>import</c> for an admin on any version that is not
    /// <see cref="GameVersionStatus.Superseded"/>.
    /// </summary>
    ValueTask<List<LinkDto>> CreateGameVersionLinksAsync(GameVersionId id, GameVersionStatus status, bool callerIsAdmin);

    /// <summary>
    /// The links for the game-version list: <c>self</c>, plus <c>register</c> for an admin, which is the
    /// fallback for when reading the forum stops working.
    /// </summary>
    ValueTask<List<LinkDto>> CreateCollectionLinksAsync(bool callerIsAdmin);
}
