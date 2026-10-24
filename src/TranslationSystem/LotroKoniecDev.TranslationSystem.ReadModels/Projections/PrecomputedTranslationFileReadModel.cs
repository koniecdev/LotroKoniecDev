using LotroKoniecDev.TranslationSystem.Primitives.Projections;
using LotroKoniecDev.TranslationSystem.ReadModels.Core.BuildingBlocks;

namespace LotroKoniecDev.TranslationSystem.ReadModels.Projections;

public sealed record PrecomputedTranslationFileReadModel(
    PrecomputedTranslationFileId Id,
    string Language,
    string Content,
    string ContentHash,
    DateTimeOffset GeneratedAt) : IReadOnlyEntity<PrecomputedTranslationFileId>
{
    // The row is created when the file is first built, so the two times are the same.
    public DateTimeOffset CreatedAt => GeneratedAt;
}
