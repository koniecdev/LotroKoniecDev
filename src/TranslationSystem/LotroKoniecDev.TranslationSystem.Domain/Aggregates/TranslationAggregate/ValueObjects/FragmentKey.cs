using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

/// <summary>
/// The identity of a text fragment that stays the same across game versions: the pair
/// <c>(FileId, GossipId)</c>. <c>FileId</c> points at the DAT subfile and <c>GossipId</c> (the 8-byte
/// <c>Fragment.FragmentId</c>, stored as 64-bit) at the fragment inside it. LOTRO addresses its texts
/// this way, and the import diff matches on it (spec 0001).
/// </summary>
public sealed class FragmentKey : ValueObject
{
    public int FileId { get; }
    public long GossipId { get; }

    public static Result<FragmentKey> Create(int fileId, long gossipId)
    {
        // A text FileId always has 0x25 as its high byte, so it is always a large positive number.
        // Zero or negative means the data is corrupt.
        if (fileId <= 0)
        {
            return Result.Failure<FragmentKey>(DomainErrors.TranslationEntity.FragmentKeyProperty.InvalidFileId);
        }

        // GossipId is an 8-byte unsigned value (Fragment.FragmentId) stored as a long, so only a
        // negative number means corrupt data. The patcher parser has no lower bound either, and we
        // match it: a real id, zero included, must never fail the whole import.
        if (gossipId < 0)
        {
            return Result.Failure<FragmentKey>(DomainErrors.TranslationEntity.FragmentKeyProperty.InvalidGossipId);
        }

        FragmentKey instance = new(fileId, gossipId);

        return Result.Success(instance);
    }

    private FragmentKey(int fileId, long gossipId)
    {
        FileId = fileId;
        GossipId = gossipId;
    }

    public override string ToString() => $"{FileId}/{GossipId}";

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return FileId;
        yield return GossipId;
    }
}
