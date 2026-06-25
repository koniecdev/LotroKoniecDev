using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Json;

namespace LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;

[JsonConverter(typeof(StronglyTypedIdJsonConverter<TranslationId>))]
public readonly record struct TranslationId : IStronglyTypedId<TranslationId>
{
    public static readonly TranslationId Empty = default;

    public Guid Value { get; }

    private TranslationId(Guid value)
    {
        Value = value;
    }

    public static TranslationId Create() => new(Guid.CreateVersion7());

    public static TranslationId Create(Guid id)
    {
        Ensure.NotEmpty(id);
        return new TranslationId(id);
    }

    public static TranslationId FromValue(Guid id) => new(id);

    public override string ToString() => Value.ToString();
}
