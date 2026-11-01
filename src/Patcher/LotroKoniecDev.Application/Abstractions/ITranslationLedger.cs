namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Identifies one fragment across the translation file, the DAT and the ledger.
/// </summary>
public readonly record struct LedgerKey(int FileId, ulong GossipId);

/// <summary>
/// Remembers what this patcher last wrote into each fragment (ADR-0047 §4), in a small file next to
/// the translation file. Without it the write guard could never put a newer translation on a fragment
/// that already holds an older Polish: that text matches neither the row's source digest nor the
/// row's current translation.
/// </summary>
/// <remarks>
/// Entries are <b>added and updated, never rebuilt</b>. An entry stays even when its row left the
/// current artifact (back to Draft, or removed) or was skipped this run. An old entry cannot let too
/// much through: its text is on that fragment only because we put it there. Dropping an entry leaves
/// that fragment stuck on our older Polish forever. A missing or unreadable ledger counts as empty,
/// which patches too little but never hides changed English.
/// </remarks>
public interface ITranslationLedger
{
    /// <summary>
    /// The digests recorded next to <paramref name="translationFilePath"/>, or an empty map when
    /// there is no ledger or it cannot be read.
    /// </summary>
    IReadOnlyDictionary<LedgerKey, string> Read(string translationFilePath);

    /// <summary>
    /// Replaces the ledger with <paramref name="entries"/> in one step (a temp file and a rename), so
    /// a crash leaves the old ledger whole. The caller passes the merged set: everything it read plus
    /// the rows it wrote this run.
    /// </summary>
    Result Save(string translationFilePath, IReadOnlyDictionary<LedgerKey, string> entries);
}
