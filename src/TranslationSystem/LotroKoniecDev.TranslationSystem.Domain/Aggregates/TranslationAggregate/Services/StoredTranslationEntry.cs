using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// One stored translation row compacted for the bootstrap Polish seed (PERF-06): its identity,
/// fragment key and the three facts the merge decision needs — status, the current Polish text (for
/// the idempotent already-approved check) and whether it is soft-removed — never a tracked aggregate.
/// The seed streams the whole catalog into an in-memory view once and decides every <c>polish.txt</c>
/// line from memory, replacing the per-line <c>GetByFragmentKeyAsync</c> round-trip.
/// </summary>
public readonly record struct StoredTranslationEntry(
    TranslationId Id,
    FragmentKeyValue Key,
    TranslationStatus Status,
    string? TranslatedText,
    bool IsRemoved);
