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
    // A GameVersion row is created the moment the version is detected, so the detection time is the
    // creation time. It stays unmapped so the table does not carry the same value twice.
    public DateTimeOffset CreatedAt => DetectedAt;
}
