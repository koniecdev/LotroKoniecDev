using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.Frontend.Components.Pages.Translations;

/// <summary>
/// Maps a <see cref="TranslationStatus"/> to its Polish label and badge CSS class for the list view.
/// The Frontend is Polish-only (ADR-aligned with the rest of the UI), so labels are inlined rather
/// than localized. <see cref="TranslationStatus.Unset"/> is a never-persisted sentinel and renders
/// defensively as the neutral "unknown" badge.
/// </summary>
internal static class TranslationStatusDisplay
{
    /// <summary>The statuses a user can filter by, in display order (excludes the <c>Unset</c> sentinel).</summary>
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
