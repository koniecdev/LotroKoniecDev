using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;

namespace LotroKoniecDev.TranslationSystem.Primitives.Projections;

[StronglyTypedId(jsonConverter: StronglyTypedIdJsonConverter.SystemTextJson)]
public partial struct PrecomputedTranslationFileId : IStronglyTypedId<PrecomputedTranslationFileId>
{
    public static PrecomputedTranslationFileId Create() => new(Guid.CreateVersion7());
    public static PrecomputedTranslationFileId Create(Guid id)
    {
        Ensure.NotEmpty(id);
        return new PrecomputedTranslationFileId(id);
    }
}
