using LotroKoniecDev.TranslationSystem.Contracts.Discovery;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;

/// <summary>
/// Typed client over the TMS API (<c>TranslationSystem.API</c>). It owns the base address and the
/// content-negotiation + bearer-token delegating handler; pages compose relative URIs (preferring
/// HATEOAS links from <see cref="GetDiscoveryAsync"/>) and call through the verb helpers.
/// </summary>
internal interface ITranslationSystemClient
{
    /// <summary>
    /// Fetches the HATEOAS discovery root (<c>GET /</c>) — the entry point pages use to resolve links.
    /// </summary>
    Task<ApiResult<DiscoveryResponse>> GetDiscoveryAsync(CancellationToken cancellationToken = default);

    Task<ApiResult<T>> GetApiResultAsync<T>(
        string relativeUri,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches an endpoint that returns a raw <c>text/plain</c> body (the pre-built translation file)
    /// rather than JSON, so the caller can stream it to the browser as a download.
    /// </summary>
    Task<ApiResult<string>> GetTextAsync(
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
