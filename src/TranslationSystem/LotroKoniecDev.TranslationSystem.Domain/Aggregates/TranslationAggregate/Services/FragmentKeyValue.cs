using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// A struct <see cref="FragmentKey"/> for the import's bulk maps and sets (spec 0006), where the class
/// would allocate per row and compare through an enumerator. Rows pass <see cref="FragmentKey"/>
/// validation first, so the identity here is always valid.
/// </summary>
public readonly record struct FragmentKeyValue(int FileId, long GossipId)
{
    public static FragmentKeyValue From(FragmentKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return new FragmentKeyValue(key.FileId, key.GossipId);
    }
}
