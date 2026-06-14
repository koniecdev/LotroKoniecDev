using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;

namespace LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

[StronglyTypedId(jsonConverter: StronglyTypedIdJsonConverter.SystemTextJson)]
public partial struct TranslatorId : IStronglyTypedId<TranslatorId>
{
    public static TranslatorId Create() => new(Guid.CreateVersion7());
    public static TranslatorId Create(Guid id)
    {
        Ensure.NotEmpty(id);
        return new TranslatorId(id);
    }
}
