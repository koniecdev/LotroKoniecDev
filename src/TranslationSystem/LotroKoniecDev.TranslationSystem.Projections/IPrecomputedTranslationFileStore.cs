namespace LotroKoniecDev.TranslationSystem.Projections;

/// <summary>
/// Not an <c>IRepository</c>: repositories are for aggregate roots, and this is a derived projection
/// upserted by its natural key, one row per language (ADR-0007).
/// </summary>
public interface IPrecomputedTranslationFileStore
{
    /// <summary>
    /// One <c>UPDATE</c>, so the old multi-MB content is never loaded (PERF-04). It runs at once, not at
    /// the unit-of-work save. Returns <see langword="false"/> when there is no row yet; the caller then
    /// calls <see cref="Insert"/>.
    /// </summary>
    Task<bool> TryRefreshAsync(
        string language,
        string content,
        string contentHash,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken);

    void Insert(PrecomputedTranslationFile file);
}
