namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// An open copy of the translation file and the hash it was written under. The caller owns
/// <see cref="Content"/> and disposes it once the body is sent.
/// </summary>
internal sealed record TranslationFileCopy(Stream Content, string ContentHash);

/// <summary>
/// Hands out the ready-made translation file from a copy on the API's own disk, so a full download
/// never loads the multi-MB content from the database (PERF-09, #715, ADR-0064).
/// </summary>
internal interface ITranslationFileDiskCache
{
    /// <summary>
    /// Opens the copy for <paramref name="contentHash"/>. When it is missing, the copy of the file
    /// stored right now is written first, so the result can carry a newer hash than the one asked for.
    /// It returns <see langword="null"/> when no file has been built for the language yet.
    /// </summary>
    Task<TranslationFileCopy?> OpenAsync(string language, string contentHash, CancellationToken cancellationToken);
}
