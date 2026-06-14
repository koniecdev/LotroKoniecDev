using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Contracts.GameVersions;

/// <summary>
/// One detected (or manually registered) LOTRO game version: the dotted version string, when it was
/// detected and its lifecycle status (Unprocessed, Processed, or Superseded when a newer version was
/// processed first). Backs the admin's update dashboard (spec 0001).
/// </summary>
public sealed record GameVersionResponse(
    GameVersionId Id,
    string Version,
    DateTimeOffset DetectedAt,
    GameVersionStatus Status);
