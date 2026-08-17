namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Identifies one fragment across the translation file, the DAT and the ledger.
/// </summary>
public readonly record struct LedgerKey(int FileId, ulong GossipId);

/// <summary>
/// Remembers what this patcher last wrote into each fragment (ADR-0047 §4) — a sidecar next to the
/// translation file, the <c>.etag</c>/<c>.endpoint</c> pattern. Without it the write guard could
/// never let a NEWER translation land on a fragment that already holds an older Polish: that text
/// matches neither the row's source digest nor the row's own translation.
/// </summary>
/// <remarks>
/// The ledger is <b>upserted, never rebuilt</b>. An entry for a row absent from the current
/// artifact (edited back to Draft, soft-removed) or skipped this run is kept: a stale entry can
/// never over-admit — its text sits on a fragment only because we put it there — while dropping one
/// strands that fragment on our older Polish forever. A missing or unreadable ledger is treated as
/// empty, which under-patches but can never mask (ADR-0047 §4).
/// </remarks>
public interface ITranslationLedger
{
    /// <summary>
    /// The digests recorded next to <paramref name="translationFilePath"/>, or an empty map when
    /// there is no ledger or it cannot be read.
    /// </summary>
    IReadOnlyDictionary<LedgerKey, string> Read(string translationFilePath);

    /// <summary>
    /// Replaces the ledger with <paramref name="entries"/>, atomically (temp file + rename) so a
    /// crash mid-write leaves the previous ledger intact rather than a half-written one. The caller
    /// passes the merged set — everything read plus the rows it wrote this run.
    /// </summary>
    Result Save(string translationFilePath, IReadOnlyDictionary<LedgerKey, string> entries);
}
