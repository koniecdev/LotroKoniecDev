using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// One stored translation row, made small for the import diff (spec 0006): its identity, fragment
/// key, the 128-bit source hash, the echo hash of its Polish (spec 0012) and the two status facts the
/// outcomes depend on. It never carries the source strings and never a tracked aggregate. The catalog
/// side streams these, so the diff grows with the plan and not with the catalog.
/// </summary>
/// <param name="EchoHash">
/// The hash of the triple a patched DAT sends back for this row: its current Polish text with the
/// source's args columns (<see cref="SourceHash.ComputeEcho"/>). <c>null</c> when the row has no
/// Polish. An incoming row with this hash is our own patch coming back, not a source change.
/// </param>
public readonly record struct StoredSourceDigest(
    TranslationId Id,
    FragmentKeyValue Key,
    SourceHash SourceHash,
    SourceHash? EchoHash,
    TranslationStatus Status,
    bool IsRemoved);
