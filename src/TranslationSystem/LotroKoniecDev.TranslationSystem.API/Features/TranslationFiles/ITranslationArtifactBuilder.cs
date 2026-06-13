namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Regenerates the pre-built translation file for a language from the current Approved set. Called
/// from writes (version processing now; approve/upsert later) so the distribution endpoint never
/// builds per-request.
/// </summary>
internal interface ITranslationArtifactBuilder
{
    Task RebuildAsync(string language, CancellationToken cancellationToken);
}
