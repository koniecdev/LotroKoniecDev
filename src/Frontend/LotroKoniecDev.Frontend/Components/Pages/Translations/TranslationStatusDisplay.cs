using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.Frontend.Components.Pages.Translations;

/// <summary>
/// Turns a <see cref="TranslationStatus"/> into its Polish label and badge CSS class for the list. The
/// Frontend is Polish only, like the rest of the UI, so the labels are written here instead of being
/// translated. <see cref="TranslationStatus.Unset"/> is never stored, and it falls back to the neutral
/// "unknown" badge just in case.
/// </summary>
internal static class TranslationStatusDisplay
{
    /// <summary>The statuses a user can filter by, in the order they are shown. <c>Unset</c> is left out.</summary>
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
