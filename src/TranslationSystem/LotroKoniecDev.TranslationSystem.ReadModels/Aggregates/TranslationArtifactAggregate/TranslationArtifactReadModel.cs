using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationArtifactAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Core.BuildingBlocks;

namespace LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationArtifactAggregate;

public sealed record TranslationArtifactReadModel(
    TranslationArtifactId Id,
    string Language,
    string Content,
    string ContentHash,
    DateTimeOffset GeneratedAt) : IReadOnlyEntity<TranslationArtifactId>
{
    // The artifact row is created when first built, so generation time is its creation time —
    // kept unmapped to avoid a duplicate column (mirrors GameVersionReadModel).
    public DateTimeOffset CreatedAt => GeneratedAt;
}
