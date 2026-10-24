namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// A handler that changes the distributed set calls <see cref="Schedule"/> after its commit instead of
/// awaiting the rebuild, which walks every row (PERF-04, ADR-0021). So a client that disconnects cannot
/// leave a committed write with an out-of-date artifact.
/// </summary>
internal interface ITranslationFileRebuildScheduler
{
    /// <summary>
    /// Never blocks: it only adds a signal for the background worker.
    /// </summary>
    void Schedule(string language);
}
