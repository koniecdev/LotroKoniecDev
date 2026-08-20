namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Identifies one fragment across the translation file, the DAT and the ledger.
/// </summary>
public readonly record struct LedgerKey(int FileId, ulong GossipId);

/// <summary>
/// Remembers what this patcher last wrote into each fragment (ADR-0047 §4). It is a small file next
/// to the translation file, like <c>.etag</c> and <c>.endpoint</c>. Without it the write guard could
/// never put a newer translation on a fragment that already holds an older Polish, because that text
/// matches neither the row's source digest nor the row's current translation.
/// </summary>
/// <remarks>
/// Entries are <b>added and updated, never rebuilt from scratch</b>. An entry is kept even when its
/// row is missing from the current artifact, for example because it went back to Draft or was
/// removed, and even when the row was skipped this run.
/// An old entry can never let too much through: its text is on that fragment only because we put it
/// there. Dropping an entry, on the other hand, leaves that fragment stuck on our older Polish
/// forever. A missing or unreadable ledger counts as empty, which patches too little but never hides
/// changed English (ADR-0047 §4).
/// </remarks>
public interface ITranslationLedger
{
    /// <summary>
    /// The digests recorded next to <paramref name="translationFilePath"/>, or an empty map when
    /// there is no ledger or it cannot be read.
    /// </summary>
    IReadOnlyDictionary<LedgerKey, string> Read(string translationFilePath);

    /// <summary>
    /// Replaces the ledger with <paramref name="entries"/> in one step, through a temp file and a
    /// rename, so a crash leaves the old ledger whole instead of a half-written one. The caller passes
    /// the merged set: everything it read plus the rows it wrote this run.
    /// </summary>
    Result Save(string translationFilePath, IReadOnlyDictionary<LedgerKey, string> entries);
}
