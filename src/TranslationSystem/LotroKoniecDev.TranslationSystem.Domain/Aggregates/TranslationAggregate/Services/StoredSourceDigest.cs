using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// One stored translation row compacted for the import diff (spec 0006): its identity, fragment
/// key, the 128-bit source hash and the two status facts the five outcomes depend on — never the
/// source strings, never a tracked aggregate. The catalog side streams these so the diff's working
/// set scales with the plan, not the catalog.
/// </summary>
public readonly record struct StoredSourceDigest(
    TranslationId Id,
    FragmentKeyValue Key,
    SourceHash SourceHash,
    TranslationStatus Status,
    bool IsRemoved);
