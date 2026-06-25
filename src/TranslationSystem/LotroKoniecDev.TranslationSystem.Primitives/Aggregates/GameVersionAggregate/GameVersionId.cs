using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Json;

namespace LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;

[JsonConverter(typeof(StronglyTypedIdJsonConverter<GameVersionId>))]
public readonly record struct GameVersionId : IStronglyTypedId<GameVersionId>
{
    public static readonly GameVersionId Empty;

    public Guid Value { get; }

    private GameVersionId(Guid value)
    {
        Value = value;
    }

    public static GameVersionId Create() => new(Guid.CreateVersion7());

    public static GameVersionId Create(Guid id)
    {
        Ensure.NotEmpty(id);
        return new GameVersionId(id);
    }

    public static GameVersionId FromValue(Guid id) => new(id);

    public override string ToString() => Value.ToString();
}
