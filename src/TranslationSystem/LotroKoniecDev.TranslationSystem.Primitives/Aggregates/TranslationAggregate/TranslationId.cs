using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;

namespace LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;

[StronglyTypedId(jsonConverter: StronglyTypedIdJsonConverter.SystemTextJson)]
public partial struct TranslationId : IStronglyTypedId<TranslationId>
{
    public static TranslationId Create() => new(Guid.CreateVersion7());
    public static TranslationId Create(Guid id)
    {
        Ensure.NotEmpty(id);
        return new TranslationId(id);
    }
}
