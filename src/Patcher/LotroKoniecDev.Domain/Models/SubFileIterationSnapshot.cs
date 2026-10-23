namespace LotroKoniecDev.Domain.Models;

/// <summary>
/// The iteration number of every text subfile in a DAT (spec 0012 Tier 0). The patcher saves one
/// right after each successful patch, and the launch compares it with the DAT as it is now.
/// </summary>
/// <remarks>
/// SSG gives a subfile a new iteration every time it replaces it, even when the new text is
/// identical. Our own patch and the launcher's write on every start keep the iteration. So a
/// subfile whose iteration moved, or which is gone, was replaced by an update after our patch, and
/// its Polish rows are gone. Size is no signal: it misses replacements and moves on every patch of
/// ours. Measured on real updates in E5 and E7 (docs/knowledge-base/README.md). Only text subfiles
/// are kept, because the patcher never writes any other kind.
/// </remarks>
public sealed class SubFileIterationSnapshot
{
    private readonly Dictionary<int, int> _iterations;

    public int Count => _iterations.Count;

    public IReadOnlyDictionary<int, int> Iterations => _iterations;

    /// <summary>
    /// The text subfiles of this snapshot that <paramref name="current"/> holds with another
    /// iteration, or does not hold at all. A subfile that only <paramref name="current"/> holds is
    /// new in the update: it never held a row of ours, so it is not part of the set.
    /// </summary>
    public WipedSubFileSet FindWipedIn(SubFileIterationSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        List<int> iterationMoved = [];
        List<int> removed = [];

        foreach ((int fileId, int iteration) in _iterations)
        {
            if (!current._iterations.TryGetValue(fileId, out int currentIteration))
            {
                removed.Add(fileId);
            }
            else if (currentIteration != iteration)
            {
                iterationMoved.Add(fileId);
            }
        }

        iterationMoved.Sort();
        removed.Sort();

        return new WipedSubFileSet(iterationMoved, removed);
    }

    /// <param name="subfileSizes">What one <c>GetSubfileSizes</c> call returns for the whole DAT.</param>
    public static SubFileIterationSnapshot FromSubfileSizes(
        IReadOnlyDictionary<int, (int Size, int Iteration)> subfileSizes)
    {
        ArgumentNullException.ThrowIfNull(subfileSizes);

        return new SubFileIterationSnapshot(subfileSizes
            .Where(entry => SubFile.IsTextFile(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value.Iteration));
    }

    public static SubFileIterationSnapshot FromIterations(IReadOnlyDictionary<int, int> iterations)
    {
        ArgumentNullException.ThrowIfNull(iterations);

        return new SubFileIterationSnapshot(iterations
            .Where(entry => SubFile.IsTextFile(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value));
    }

    private SubFileIterationSnapshot(Dictionary<int, int> iterations)
    {
        _iterations = iterations;
    }
}
