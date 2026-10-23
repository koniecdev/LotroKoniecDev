namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Write handlers never call this: they signal <see cref="ITranslationFileRebuildScheduler"/>, and the
/// background worker calls it with the host's token (PERF-04, ADR-0021, ADR-0007).
/// </summary>
internal interface IPrecomputedTranslationFileProjector
{
    Task RebuildAsync(string language, CancellationToken cancellationToken);
}
