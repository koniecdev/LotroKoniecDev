using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.Frontend.Components.Pages.GameVersions;

/// <summary>
/// Maps a <see cref="GameVersionStatus"/> to its Polish label and badge CSS class for the list view.
/// The Frontend is Polish-only (aligned with the rest of the UI), so labels are inlined rather than
/// localized. <see cref="GameVersionStatus.Unset"/> is a never-persisted sentinel and renders
/// defensively as the neutral "unknown" badge. Mirrors <c>TranslationStatusDisplay</c>.
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
