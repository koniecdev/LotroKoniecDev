namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Fetches the current Polish translation file from the TMS distribution endpoint, honouring a cached
/// ETag so an unchanged file is reported as not-modified instead of being re-downloaded.
/// </summary>
public interface ITranslationFileDownloader
{
    /// <param name="endpoint">
    /// The absolute URI resolved from the service document by <see cref="ITranslationFileEndpointResolver"/>.
    /// The downloader composes no path of its own — there is no route left in the patcher source (#611).
    /// </param>
    Task<Result<TranslationFileFetchResult>> FetchAsync(Uri endpoint, string? currentETag, CancellationToken cancellationToken);
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
