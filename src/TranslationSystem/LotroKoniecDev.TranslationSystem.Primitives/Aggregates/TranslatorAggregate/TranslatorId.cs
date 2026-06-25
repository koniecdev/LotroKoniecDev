using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Json;

namespace LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

[JsonConverter(typeof(StronglyTypedIdJsonConverter<TranslatorId>))]
public readonly record struct TranslatorId : IStronglyTypedId<TranslatorId>
{
    public static readonly TranslatorId Empty = default;

    public Guid Value { get; }

    private TranslatorId(Guid value)
    {
        Value = value;
    }

    public static TranslatorId Create() => new(Guid.CreateVersion7());

    public static TranslatorId Create(Guid id)
    {
        Ensure.NotEmpty(id);
        return new TranslatorId(id);
    }

    public static TranslatorId FromValue(Guid id) => new(id);

    public override string ToString() => Value.ToString();
}
