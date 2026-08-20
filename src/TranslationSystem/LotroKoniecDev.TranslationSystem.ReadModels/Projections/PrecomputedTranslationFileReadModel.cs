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
    // The row is created when the file is first built, so the generation time is the creation time.
    // It stays unmapped so the table does not carry the same value twice, as in GameVersionReadModel.
    public DateTimeOffset CreatedAt => GeneratedAt;
}
