namespace LotroKoniecDev.TranslationSystem.Projections;

/// <summary>
/// Write-side persistence port for the <see cref="PrecomputedTranslationFile"/> projection.
/// Deliberately not an <c>IRepository</c> (those are constrained to aggregate roots): the precomputed
/// file is a derived read projection, upserted by its natural key — one row per language. See ADR-0007.
/// </summary>
public interface IPrecomputedTranslationFileStore
{
    /// <summary>
    /// Set-based, in-place refresh of the language's projection row (PERF-04): a single
    /// <c>UPDATE</c> that never loads the previous multi-MB content just to overwrite it. Executes
    /// immediately — no unit-of-work save is involved. Returns <see langword="false"/> when no row
    /// exists yet, in which case the caller inserts via <see cref="Insert"/>.
    /// </summary>
    Task<bool> TryRefreshAsync(
        string language,
        string content,
        string contentHash,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken);

    void Insert(PrecomputedTranslationFile file);
}
