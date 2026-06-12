using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;

namespace LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;

[StronglyTypedId(jsonConverter: StronglyTypedIdJsonConverter.SystemTextJson)]
public partial struct GameVersionId : IStronglyTypedId<GameVersionId>
{
    public static GameVersionId Create() => new(Guid.CreateVersion7());
    public static GameVersionId Create(Guid id)
    {
        Ensure.NotEmpty(id);
        return new GameVersionId(id);
    }
}
