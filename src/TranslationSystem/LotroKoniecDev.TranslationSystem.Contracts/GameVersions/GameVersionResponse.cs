using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Contracts.GameVersions;

/// <summary>
/// <see cref="GameVersionStatus.Superseded"/> means a newer version was processed first (spec 0001).
/// </summary>
public sealed record GameVersionResponse(
    GameVersionId Id,
    string Version,
    DateTimeOffset DetectedAt,
    GameVersionStatus Status) : ILinksResponse
{
    public IReadOnlyCollection<LinkDto> Links { get; set; } = [];
}
