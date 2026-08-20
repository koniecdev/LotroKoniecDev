namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Keeps the downloaded translation file and the last ETag the server sent, so the launch sync can
/// ask "has it changed?" and reuse the file it already has.
/// </summary>
public interface ITranslationFileCache
{
    /// <summary>The ETag stored alongside the cached translation file, or <c>null</c> when none is cached.</summary>
    string? ReadETag(string translationFilePath);

    /// <summary>
    /// The download endpoint the last successful sync found through discovery, or <c>null</c> when
    /// nothing is cached. It is the fallback for when the server is down (#611). It is checked again
    /// before use: being on disk does not make it trusted.
    /// </summary>
    string? ReadEndpointHref(string translationFilePath);

    /// <summary>Writes the downloaded translation file and its ETag.</summary>
    Result Save(string translationFilePath, string content, string eTag);

    /// <summary>Records the endpoint that just served the file, as a fallback for when it is down.</summary>
    Result SaveEndpointHref(string translationFilePath, string endpointHref);
}
