using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using LotroKoniecDev.TranslationSystem.ReadModels.Core.BuildingBlocks;

namespace LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.GameVersionAggregate;

public sealed record GameVersionReadModel(
    GameVersionId Id,
    string LotroNotationVersion,
    DateTimeOffset DetectedAt,
    GameVersionStatus Status) : IReadOnlyEntity<GameVersionId>
{
    // A row is created the moment the version is detected, so the two times are the same.
    public DateTimeOffset CreatedAt => DetectedAt;
}
