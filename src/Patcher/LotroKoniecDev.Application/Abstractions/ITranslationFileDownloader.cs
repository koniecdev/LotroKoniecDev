namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Fetches the current Polish translation file from the TMS distribution endpoint, honouring a cached
/// ETag so an unchanged file is reported as not-modified instead of being re-downloaded.
/// </summary>
public interface ITranslationFileDownloader
{
    Task<Result<TranslationFileFetchResult>> FetchAsync(string baseUrl, string? currentETag, CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of a conditional translation-file fetch: either the server confirmed the cached copy is
/// still current (<see cref="IsModified"/> is <c>false</c>), or it returned a newer file with its ETag.
/// </summary>
public sealed record TranslationFileFetchResult(bool IsModified, string? Content, string? ETag)
{
    public static TranslationFileFetchResult NotModified() => new(false, null, null);

    public static TranslationFileFetchResult Modified(string content, string eTag) => new(true, content, eTag);
}
