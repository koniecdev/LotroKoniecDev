using LotroKoniecDev.AuthSystem.Contracts.Discovery;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;

internal interface IAuthSystemClient
{
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

    Task<ApiResult<ApiResponseHeaders>> PostForHeadersApiResultAsync(
        string relativeUri,
        object body,
        CancellationToken cancellationToken = default);
}
