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
    // The row is created when first built, so generation time is its creation time —
    // kept unmapped to avoid a duplicate column (mirrors GameVersionReadModel).
    public DateTimeOffset CreatedAt => GeneratedAt;
}
