using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using LotroKoniecDev.TranslationSystem.ReadModels.Core.BuildingBlocks;

namespace LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.GameVersionAggregate;

public sealed record GameVersionReadModel(
    GameVersionId Id,
    string Version,
    DateTimeOffset DetectedAt,
    GameVersionStatus Status) : IReadOnlyEntity<GameVersionId>
{
    // A GameVersion row is created the moment the version is detected, so detection time
    // is the row's creation time — kept unmapped to avoid a duplicate column.
    public DateTimeOffset CreatedAt => DetectedAt;
}
