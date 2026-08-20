namespace LotroKoniecDev.TranslationSystem.Projections;

/// <summary>
/// The write port for the <see cref="PrecomputedTranslationFile"/> projection. It is not an
/// <c>IRepository</c> on purpose: repositories are for aggregate roots, and this file is a derived
/// projection that is upserted by its natural key, one row per language. See ADR-0007.
/// </summary>
public interface IPrecomputedTranslationFileStore
{
    /// <summary>
    /// Refreshes the language's row in place with one <c>UPDATE</c> (PERF-04), so the previous
    /// multi-MB content is never loaded just to be overwritten. It runs at once and does not wait for
    /// a unit-of-work save. It returns <see langword="false"/> when there is no row yet, and the
    /// caller then adds one with <see cref="Insert"/>.
    /// </summary>
    Task<bool> TryRefreshAsync(
        string language,
        string content,
        string contentHash,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken);

    void Insert(PrecomputedTranslationFile file);
}
