namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Regenerates the precomputed translation file for a language from the current Approved set. Called
/// from writes (version processing, approve, upsert, bootstrap seed) so the distribution endpoint
/// never builds per-request. See ADR-0007.
/// </summary>
internal interface IPrecomputedTranslationFileProjector
{
    Task RebuildAsync(string language, CancellationToken cancellationToken);
}
