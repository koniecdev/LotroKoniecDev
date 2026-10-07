using LotroKoniecDev.TranslationSystem.Contracts.Discovery;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;

/// <summary>
/// The typed client for the TMS API (<c>TranslationSystem.API</c>). It holds the base address and the
/// handler that negotiates the content type and adds the token. Pages pass relative URIs, taken where
/// possible from the links in <see cref="GetDiscoveryAsync"/>, and call the verb helpers.
/// </summary>
internal interface ITranslationSystemClient
{
    /// <summary>
    /// Fetches the discovery root (<c>GET /</c>), where pages look up their links.
    /// </summary>
    Task<ApiResult<DiscoveryResponse>> GetDiscoveryAsync(CancellationToken cancellationToken = default);

    Task<ApiResult<T>> GetApiResultAsync<T>(
        string relativeUri,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the ready-made translation file as a stream, so the caller can pass it on to the browser
    /// without holding the whole file in memory. The caller disposes the stream.
    /// </summary>
    Task<ApiResult<ApiBodyStream>> GetBodyStreamAsync(
        string relativeUri,
        CancellationToken cancellationToken = default);

    Task<ApiResult> PostApiResultAsync(
        string relativeUri,
        object body,
        CancellationToken cancellationToken = default);

    Task<ApiResult<T>> PostApiResultAsync<T>(
        string relativeUri,
        object body,
        CancellationToken cancellationToken = default);

    Task<ApiResult> PutApiResultAsync(
        string relativeUri,
        object body,
        CancellationToken cancellationToken = default);

    Task<ApiResult<T>> PutApiResultAsync<T>(
        string relativeUri,
        object body,
        CancellationToken cancellationToken = default);

    Task<ApiResult> PutApiResultAsync(
        string relativeUri,
        CancellationToken cancellationToken = default);

    Task<ApiResult> PatchApiResultAsync(
        string relativeUri,
        object body,
        CancellationToken cancellationToken = default);

    Task<ApiResult> DeleteApiResultAsync(
        string relativeUri,
        CancellationToken cancellationToken = default);

    Task<ApiResult> SendMultipartApiResultAsync(
        HttpMethod method,
        string relativeUri,
        MultipartFormDataContent content,
        CancellationToken cancellationToken = default);

    Task<ApiResult<T>> SendMultipartApiResultAsync<T>(
        HttpMethod method,
        string relativeUri,
        MultipartFormDataContent content,
        CancellationToken cancellationToken = default);
}
