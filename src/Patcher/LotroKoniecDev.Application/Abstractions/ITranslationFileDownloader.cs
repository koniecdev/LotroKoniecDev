namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Fetches the current Polish translation file from the TMS. It sends the cached ETag, so an
/// unchanged file comes back as "not modified" instead of being downloaded again.
/// </summary>
public interface ITranslationFileDownloader
{
    /// <param name="endpoint">
    /// The absolute URI that <see cref="ITranslationFileEndpointResolver"/> read from the service
    /// document. The downloader builds no path of its own: no route is left in the patcher source
    /// (#611).
    /// </param>
    Task<Result<TranslationFileFetchResult>> FetchAsync(Uri endpoint, string? currentETag, CancellationToken cancellationToken);
}

/// <summary>
/// The result of a conditional fetch. Either the server said the cached copy is still current, and
/// <see cref="IsModified"/> is <c>false</c>, or it sent a newer file together with its ETag.
/// </summary>
public sealed record TranslationFileFetchResult(bool IsModified, string? Content, string? ETag)
{
    public static TranslationFileFetchResult NotModified() => new(false, null, null);

    public static TranslationFileFetchResult Modified(string content, string eTag) => new(true, content, eTag);
}
