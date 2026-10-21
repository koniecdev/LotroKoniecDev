namespace LotroKoniecDev.Domain.Models;

/// <summary>
/// The text subfiles a game update replaced since our last patch (spec 0012 Tier 0). It is both
/// what the launch detects and the set a repair has to cover.
/// </summary>
/// <param name="IterationMoved">Subfiles that are still there, with a new iteration.</param>
/// <param name="Removed">Subfiles the update took out of the DAT.</param>
public sealed record WipedSubFileSet(IReadOnlyList<int> IterationMoved, IReadOnlyList<int> Removed)
{
    public static WipedSubFileSet None { get; } = new([], []);

    public int Count => IterationMoved.Count + Removed.Count;

    public bool IsEmpty => Count == 0;
}
