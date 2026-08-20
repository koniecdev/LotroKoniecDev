namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Rebuilds the ready-made translation file for a language from the rows that are approved right now,
/// so the download endpoint never builds it per request.
/// Writes such as version processing, approve and upsert no longer call this directly. They signal
/// <see cref="ITranslationFileRebuildScheduler"/>, and the background worker calls this with the
/// host's own cancellation token (PERF-04, ADR-0021). See ADR-0007.
/// </summary>
internal interface IPrecomputedTranslationFileProjector
{
    Task RebuildAsync(string language, CancellationToken cancellationToken);
}
