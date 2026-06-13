using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;

namespace LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationArtifactAggregate;

[StronglyTypedId(jsonConverter: StronglyTypedIdJsonConverter.SystemTextJson)]
public partial struct TranslationArtifactId : IStronglyTypedId<TranslationArtifactId>
{
    public static TranslationArtifactId Create() => new(Guid.CreateVersion7());
    public static TranslationArtifactId Create(Guid id)
    {
        Ensure.NotEmpty(id);
        return new TranslationArtifactId(id);
    }
}
