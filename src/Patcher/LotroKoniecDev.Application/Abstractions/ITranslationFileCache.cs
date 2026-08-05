namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Persists the downloaded translation file and the ETag last seen from the server, so the launch
/// sync can issue a conditional request and reuse the latest download.
/// </summary>
public interface ITranslationFileCache
{
    /// <summary>The ETag stored alongside the cached translation file, or <c>null</c> when none is cached.</summary>
    string? ReadETag(string translationFilePath);

    /// <summary>
    /// The download endpoint the last successful sync resolved from discovery, or <c>null</c> when none
    /// is cached. This is the outage safety net (#611) — it is re-validated before use, never trusted
    /// because it is on disk.
    /// </summary>
    string? ReadEndpointHref(string translationFilePath);

    /// <summary>Writes the downloaded translation file and its ETag.</summary>
    Result Save(string translationFilePath, string content, string eTag);

    /// <summary>Records the endpoint that just served the file, so a later outage has something to fall back to.</summary>
    Result SaveEndpointHref(string translationFilePath, string endpointHref);
}
