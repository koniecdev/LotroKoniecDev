using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Persistence.SaveBudget;

/// <summary>
/// One save a non-admin made inside the budget window (ADR-0072). It holds no business rule and no
/// history: rows older than the window are deleted on the next budgeted save.
/// </summary>
internal sealed class TranslationSave
{
    public Guid Id { get; init; }
    public TranslatorId TranslatorId { get; init; }
    public DateTimeOffset SavedAt { get; init; }

    /// <summary>
    /// The save replaced Polish text that was already on the row.
    /// </summary>
    public bool ReplacedPolish { get; init; }
}
