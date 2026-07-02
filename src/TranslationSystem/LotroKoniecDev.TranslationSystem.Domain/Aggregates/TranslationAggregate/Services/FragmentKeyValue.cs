using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// The value-type mirror of <see cref="FragmentKey"/> for the import's bulk paths (spec 0006):
/// a map/set key over hundreds of thousands of rows, where the class VO's per-row allocation and
/// enumerator-based equality would dominate the working set. Rows enter the diff through
/// <see cref="FragmentKey"/> validation first, so a <see cref="FragmentKeyValue"/> always carries
/// an already-validated identity.
/// </summary>
public readonly record struct FragmentKeyValue(int FileId, long GossipId)
{
    public static FragmentKeyValue From(FragmentKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return new FragmentKeyValue(key.FileId, key.GossipId);
    }
}
