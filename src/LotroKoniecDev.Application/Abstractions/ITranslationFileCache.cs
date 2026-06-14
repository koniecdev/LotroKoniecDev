namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Persists the downloaded translation file and the ETag last seen from the server, so the launch
/// sync can issue a conditional request and reuse the latest download.
/// </summary>
public interface ITranslationFileCache
{
    /// <summary>The ETag stored alongside the cached translation file, or <c>null</c> when none is cached.</summary>
    string? ReadETag(string translationFilePath);

    /// <summary>Writes the downloaded translation file and its ETag.</summary>
    Result Save(string translationFilePath, string content, string eTag);
}
