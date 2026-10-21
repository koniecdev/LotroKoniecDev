using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Answers on every launch: did a game update replace a text subfile since our last patch? It needs
/// one read-only open of the DAT and no content reads (spec 0012 Tier 0, E5).
/// </summary>
public interface ISubFileWipeDetector
{
    /// <returns>
    /// The subfiles the update replaced. The set is empty when nothing moved, and also when nothing
    /// can be compared: no saved snapshot, a damaged one, or a DAT that cannot be read. A missing
    /// answer must never force a repair.
    /// </returns>
    /// <remarks>
    /// An exception from the DAT adapter is not caught. It means a bug of ours or a native fault, and a
    /// native fault such as an access violation ends the process anyway, so a catch here would only
    /// hide our own bugs.
    /// </remarks>
    WipedSubFileSet Detect(string datFilePath, string translationFilePath);
}
