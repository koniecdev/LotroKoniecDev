using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Keeps the iteration snapshot of the DAT taken right after the last successful patch (spec 0012
/// Tier 0), in a small file next to the translation file.
/// </summary>
public interface ISubFileIterationSnapshotStore
{
    /// <returns>
    /// <c>null</c> when no snapshot was saved yet: on the first run, and on an install patched before
    /// snapshots existed. A failure when the file cannot be read or is damaged. The launch treats both
    /// the same way: it cannot detect anything.
    /// </returns>
    Result<SubFileIterationSnapshot?> Read(string translationFilePath);

    /// <summary>
    /// Replaces the snapshot in one step (a temp file and a rename), so a crash leaves the old snapshot
    /// whole.
    /// </summary>
    Result Save(string translationFilePath, SubFileIterationSnapshot snapshot);
}
