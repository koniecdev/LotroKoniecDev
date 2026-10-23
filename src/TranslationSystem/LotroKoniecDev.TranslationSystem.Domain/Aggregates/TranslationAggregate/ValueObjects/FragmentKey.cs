using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

/// <summary>
/// The identity of a text fragment that stays the same across game versions: <c>FileId</c> is the DAT
/// subfile and <c>GossipId</c> the patcher's <c>Fragment.FragmentId</c> inside it. The import diff
/// matches on it (spec 0001).
/// </summary>
public sealed class FragmentKey : ValueObject
{
    public int FileId { get; }
    public long GossipId { get; }

    public static Result<FragmentKey> Create(int fileId, long gossipId)
    {
        // A text FileId has 0x25 as its high byte, so zero or negative means corrupt data.
        if (fileId <= 0)
        {
            return Result.Failure<FragmentKey>(DomainErrors.TranslationEntity.FragmentKeyProperty.InvalidFileId);
        }

        // Unsigned in the DAT, so only a negative number is corrupt. Zero is a real id: the patcher
        // parser accepts it too, and it must never fail the whole import.
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
