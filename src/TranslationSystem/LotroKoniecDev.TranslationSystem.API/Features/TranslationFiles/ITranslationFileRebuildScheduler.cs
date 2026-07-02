namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// The write-path seam for artifact regeneration (PERF-04, ADR-0021): handlers that change the
/// distributed set (import, approve, upsert of an Approved row) call <see cref="Schedule"/> after
/// their commit instead of awaiting the O(N) rebuild inline, so the request responds immediately
/// and a client disconnect can no longer strand a committed write with a stale artifact.
/// </summary>
internal interface ITranslationFileRebuildScheduler
{
    /// <summary>
    /// Marks the language's precomputed translation file dirty. Non-blocking and synchronous — it
    /// only enqueues a signal; the background worker debounces and performs the actual rebuild.
    /// </summary>
    void Schedule(string language);
}
