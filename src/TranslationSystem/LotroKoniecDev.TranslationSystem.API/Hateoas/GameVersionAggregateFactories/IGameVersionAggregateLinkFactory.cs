using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.GameVersionAggregateFactories;

internal interface IGameVersionAggregateLinkFactory
{
    /// <summary>
    /// <c>self</c>, plus for an admin <c>delete</c> while the version can still be deleted and
    /// <c>import</c> on any version that is not <see cref="GameVersionStatus.Superseded"/>.
    /// </summary>
    ValueTask<List<LinkDto>> CreateGameVersionLinksAsync(GameVersionId id, GameVersionStatus status, bool callerIsAdmin);

    /// <summary>
    /// <c>self</c>, plus <c>register</c> for an admin.
    /// </summary>
    ValueTask<List<LinkDto>> CreateCollectionLinksAsync(bool callerIsAdmin);
}
