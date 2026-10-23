namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// The caller owns <see cref="Content"/> and disposes it once the body is sent.
/// </summary>
internal sealed record TranslationFileCopy(Stream Content, string ContentHash);

/// <summary>
/// Serves the translation file from a copy on disk, so a full download never loads the multi-MB content
/// from the database (PERF-09, ADR-0064).
/// </summary>
internal interface ITranslationFileDiskCache
{
    /// <summary>
    /// When the copy is missing, the file stored right now is written first, so the result can carry a
    /// newer hash than the one asked for. <see langword="null"/> when no file was built yet.
    /// </summary>
    Task<TranslationFileCopy?> OpenAsync(string language, string contentHash, CancellationToken cancellationToken);
}
