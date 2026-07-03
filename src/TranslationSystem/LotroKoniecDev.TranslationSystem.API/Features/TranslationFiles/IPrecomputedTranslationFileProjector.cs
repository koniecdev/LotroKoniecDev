namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Regenerates the precomputed translation file for a language from the current Approved set, so the
/// distribution endpoint never builds per-request. Writes (version processing, approve, upsert) no
/// longer call it directly — they signal <see cref="ITranslationFileRebuildScheduler"/> and the
/// background worker invokes this with the host-lifetime token (PERF-04, ADR-0021). See ADR-0007.
/// </summary>
internal interface IPrecomputedTranslationFileProjector
{
    Task RebuildAsync(string language, CancellationToken cancellationToken);
}
