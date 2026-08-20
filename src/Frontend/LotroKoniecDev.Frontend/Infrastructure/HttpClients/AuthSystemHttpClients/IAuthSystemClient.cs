using LotroKoniecDev.AuthSystem.Contracts.Discovery;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;

/// <summary>
/// The typed client for the auth server's account API (<c>AuthSystem.API</c>). It holds the base
/// address and the handler that negotiates the content type and adds the token. Pages pass relative
/// URIs, taken where possible from the links in <see cref="GetDiscoveryAsync"/> and in the account
/// export, and call the verb helpers.
/// </summary>
internal interface IAuthSystemClient
{
    /// <summary>
    /// Fetches the auth discovery root (<c>GET /</c>), which offers <c>export-account-data</c> to
    /// logged-in callers.
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
    /// A POST for endpoints that return their data in response headers instead of a body, such as a
    /// <c>204</c> with <c>X-Deletion-Finalizes-At</c> when an account deletion is scheduled.
    /// </summary>
    Task<ApiResult<ApiResponseHeaders>> PostForHeadersApiResultAsync(
        string relativeUri,
        object body,
        CancellationToken cancellationToken = default);
}
