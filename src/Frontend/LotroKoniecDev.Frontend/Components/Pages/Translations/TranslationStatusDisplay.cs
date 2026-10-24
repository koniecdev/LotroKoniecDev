using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.Frontend.Components.Pages.Translations;

/// <summary>
/// <see cref="TranslationStatus.Unset"/> is never stored; it falls back to the neutral "unknown" badge.
/// </summary>
internal static class TranslationStatusDisplay
{
    /// <summary>In the order they are shown.</summary>
    public static IReadOnlyList<TranslationStatus> FilterableStatuses { get; } =
    [
        TranslationStatus.Untranslated,
        TranslationStatus.Draft,
        TranslationStatus.Approved,
        TranslationStatus.NeedsReview
    ];

    public static string Label(TranslationStatus status) => status switch
    {
        TranslationStatus.Untranslated => "Nieprzetłumaczone",
        TranslationStatus.Draft => "Wersja robocza",
        TranslationStatus.Approved => "Zatwierdzone",
        TranslationStatus.NeedsReview => "Do ponownego sprawdzenia",
        _ => "Nieznany"
    };

    public static string BadgeClass(TranslationStatus status) => status switch
    {
        TranslationStatus.Approved => "badge-success",
        TranslationStatus.Draft => "badge-warning",
        TranslationStatus.NeedsReview => "badge-danger",
        _ => "badge-neutral"
    };
}
