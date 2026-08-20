using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// The struct version of <see cref="FragmentKey"/> for the import's bulk paths (spec 0006). It is
/// used as a map or set key over hundreds of thousands of rows, where the class value object would
/// allocate per row and compare through an enumerator. Rows pass <see cref="FragmentKey"/> validation
/// first, so this type always carries an identity that is already valid.
/// </summary>
public readonly record struct FragmentKeyValue(int FileId, long GossipId)
{
    public static FragmentKeyValue From(FragmentKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return new FragmentKeyValue(key.FileId, key.GossipId);
    }
}
