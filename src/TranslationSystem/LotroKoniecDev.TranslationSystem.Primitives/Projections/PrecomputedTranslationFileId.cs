using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Json;

namespace LotroKoniecDev.TranslationSystem.Primitives.Projections;

[JsonConverter(typeof(StronglyTypedIdJsonConverter<PrecomputedTranslationFileId>))]
public readonly record struct PrecomputedTranslationFileId : IStronglyTypedId<PrecomputedTranslationFileId>
{
    public static readonly PrecomputedTranslationFileId Empty;

    public Guid Value { get; }

    private PrecomputedTranslationFileId(Guid value)
    {
        Value = value;
    }

    public static PrecomputedTranslationFileId Create() => new(Guid.CreateVersion7());

    public static PrecomputedTranslationFileId Create(Guid id)
    {
        Ensure.NotEmpty(id);
        return new PrecomputedTranslationFileId(id);
    }

    public static PrecomputedTranslationFileId FromValue(Guid id) => new(id);

    public override string ToString() => Value.ToString();
}
