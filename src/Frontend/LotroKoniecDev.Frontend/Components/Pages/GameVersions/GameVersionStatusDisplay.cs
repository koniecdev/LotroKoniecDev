using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.Frontend.Components.Pages.GameVersions;

/// <summary>
/// Turns a <see cref="GameVersionStatus"/> into its Polish label and badge CSS class for the list. The
/// Frontend is Polish only, like the rest of the UI, so the labels are written here instead of being
/// translated. <see cref="GameVersionStatus.Unset"/> is never stored, and it falls back to the neutral
/// "unknown" badge just in case. It works like <c>TranslationStatusDisplay</c>.
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
