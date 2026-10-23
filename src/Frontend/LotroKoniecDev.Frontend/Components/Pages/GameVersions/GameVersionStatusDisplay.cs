using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.Frontend.Components.Pages.GameVersions;

/// <summary>
/// <see cref="GameVersionStatus.Unset"/> is never stored; it falls back to the neutral "unknown" badge.
/// </summary>
internal static class GameVersionStatusDisplay
{
    public static string Label(GameVersionStatus status) => status switch
    {
        GameVersionStatus.Unprocessed => "Nieprzetworzona",
        GameVersionStatus.Processed => "Przetworzona",
        GameVersionStatus.Superseded => "Zastąpiona",
        _ => "Nieznany"
    };

    public static string BadgeClass(GameVersionStatus status) => status switch
    {
        GameVersionStatus.Processed => "badge-success",
        GameVersionStatus.Unprocessed => "badge-warning",
        GameVersionStatus.Superseded => "badge-neutral",
        _ => "badge-neutral"
    };
}
