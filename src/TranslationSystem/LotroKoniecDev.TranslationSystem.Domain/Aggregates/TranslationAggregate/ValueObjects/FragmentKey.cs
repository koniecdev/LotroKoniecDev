using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

/// <summary>
/// The stable identity of a text fragment across game versions: the pair
/// <c>(FileId, GossipId)</c> — <c>FileId</c> addresses the DAT subfile, <c>GossipId</c>
/// (the 8-byte <c>Fragment.FragmentId</c>, stored as 64-bit) the fragment within it. This is
/// how LOTRO itself addresses texts and the key the import diff matches on (spec 0001).
/// </summary>
public sealed class FragmentKey : ValueObject
{
    public int FileId { get; }
    public long GossipId { get; }

    public static Result<FragmentKey> Create(int fileId, long gossipId)
    {
        // A text FileId always has the 0x25 high byte, so it is structurally large-positive;
        // 0 or negative signals corruption.
        if (fileId <= 0)
        {
            return Result.Failure<FragmentKey>(DomainErrors.TranslationEntity.FragmentKeyProperty.InvalidFileId);
        }

        // GossipId is an 8-byte unsigned value (Fragment.FragmentId) stored as long; only a
        // negative literal is corruption. The patcher parser applies no lower bound, so we match
        // it — a legitimate id (including 0) must never fail the whole import.
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
