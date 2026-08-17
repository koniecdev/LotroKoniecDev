using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// One stored translation row compacted for the import diff (spec 0006): its identity, fragment
/// key, the 128-bit source hash, the echo hash of its Polish (spec 0012) and the two status facts
/// the outcomes depend on — never the source strings, never a tracked aggregate. The catalog side
/// streams these so the diff's working set scales with the plan, not the catalog.
/// </summary>
/// <param name="EchoHash">
/// The hash of the triple a patched DAT echoes back for this row — its current Polish text with the
/// source's args columns (<see cref="SourceHash.ComputeEcho"/>) — or <c>null</c> when the row has
/// no Polish. An incoming row that hash-matches it is an echo of our own patch, not a source change.
/// </param>
public readonly record struct StoredSourceDigest(
    TranslationId Id,
    FragmentKeyValue Key,
    SourceHash SourceHash,
    SourceHash? EchoHash,
    TranslationStatus Status,
    bool IsRemoved);
