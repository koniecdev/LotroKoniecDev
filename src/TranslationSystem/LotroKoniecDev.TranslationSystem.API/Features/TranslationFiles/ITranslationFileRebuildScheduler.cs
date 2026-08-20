namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// How a write asks for the artifact to be rebuilt (PERF-04, ADR-0021). A handler that changes the
/// distributed set, such as an import, an approve or an upsert of an approved row, calls
/// <see cref="Schedule"/> after its commit instead of waiting for the rebuild, which walks every row.
/// The request then answers at once, and a client that disconnects can no longer leave a committed
/// write with an out-of-date artifact.
/// </summary>
internal interface ITranslationFileRebuildScheduler
{
    /// <summary>
    /// Marks the language's file as needing a rebuild. It does not block and does not wait: it only
    /// adds a signal. The background worker collects the signals and does the rebuild.
    /// </summary>
    void Schedule(string language);
}
