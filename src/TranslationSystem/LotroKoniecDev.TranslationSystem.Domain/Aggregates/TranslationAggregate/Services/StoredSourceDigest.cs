using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// One stored row, made small for the import diff (spec 0006): no source strings, no tracked
/// aggregate. The catalog side streams these, so the diff grows with the plan, not the catalog.
/// </summary>
/// <param name="EchoHash">
/// <see cref="SourceHash.ComputeEcho"/> of this row (spec 0012). An incoming row with this hash is our
/// own patch coming back, not a source change.
/// </param>
public readonly record struct StoredSourceDigest(
    TranslationId Id,
    FragmentKeyValue Key,
    SourceHash SourceHash,
    SourceHash? EchoHash,
    TranslationStatus Status,
    bool IsRemoved);
