using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Keeps the iteration snapshot of the DAT taken right after the last successful patch (spec 0012
/// Tier 0). It is a small file next to the translation file, like the ledger, <c>.etag</c> and
/// <c>.endpoint</c>.
/// </summary>
public interface ISubFileIterationSnapshotStore
{
    /// <returns>
    /// <c>null</c> when no snapshot was saved yet, which is the case on the first run and on an
    /// install patched before this existed. A failure when the file cannot be read or is damaged.
    /// The launch treats both the same way: it cannot detect anything, so it does what it did before.
    /// </returns>
    Result<SubFileIterationSnapshot?> Read(string translationFilePath);

    /// <summary>
    /// Replaces the snapshot in one step, through a temp file and a rename, so a crash leaves the old
    /// snapshot whole instead of a half-written one.
    /// </summary>
    Result Save(string translationFilePath, SubFileIterationSnapshot snapshot);
}
