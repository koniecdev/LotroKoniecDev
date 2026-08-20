using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Contracts.GameVersions;

/// <summary>
/// One LOTRO game version, detected or registered by hand: the dotted version string, when it was
/// detected, and its status. Superseded means a newer version was processed first. This is what the
/// admin update dashboard shows (spec 0001).
/// </summary>
public sealed record GameVersionResponse(
    GameVersionId Id,
    string Version,
    DateTimeOffset DetectedAt,
    GameVersionStatus Status) : ILinksResponse
{
    public IReadOnlyCollection<LinkDto> Links { get; set; } = [];
}
