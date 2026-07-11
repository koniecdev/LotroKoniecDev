using LotroKoniecDev.AuthSystem.Contracts.Discovery;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;

/// <summary>
/// Typed client over the auth server's account API (<c>AuthSystem.API</c>). It owns the base address
/// and the content-negotiation + bearer-token delegating handler; pages compose relative URIs
/// (preferring HATEOAS links from <see cref="GetDiscoveryAsync"/> and the account export envelope)
/// and call through the verb helpers.
/// </summary>
internal interface IAuthSystemClient
{
    /// <summary>
    /// Fetches the auth HATEOAS discovery root (<c>GET /</c>) — advertises
    /// <c>export-account-data</c> for authenticated callers.
    /// </summary>
    Task<ApiResult<DiscoveryResponse>> GetDiscoveryAsync(CancellationToken cancellationToken = default);

    Task<ApiResult<T>> GetApiResultAsync<T>(
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

    /// <summary>
    /// POST for endpoints whose success payload travels in response headers rather than a body
    /// (<c>204</c> + <c>X-Deletion-Finalizes-At</c> on account-deletion scheduling).
    /// </summary>
    Task<ApiResult<ApiResponseHeaders>> PostForHeadersApiResultAsync(
        string relativeUri,
        object body,
        CancellationToken cancellationToken = default);
}
