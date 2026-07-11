using LotroKoniecDev.AuthSystem.Contracts.Discovery;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;

internal sealed class AuthSystemClient : IAuthSystemClient
{
    private const string DiscoveryRelativeUri = "";

    private readonly HttpClient _httpClient;

    public AuthSystemClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public Task<ApiResult<DiscoveryResponse>> GetDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        return _httpClient.GetApiResultAsync<DiscoveryResponse>(DiscoveryRelativeUri, cancellationToken);
    }

    public Task<ApiResult<T>> GetApiResultAsync<T>(
        string relativeUri,
        CancellationToken cancellationToken = default)
    {
        return _httpClient.GetApiResultAsync<T>(relativeUri, cancellationToken);
    }

    public Task<ApiResult> PostApiResultAsync(
        string relativeUri,
        object body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return _httpClient.PostApiResultAsync(relativeUri, body, cancellationToken);
    }

    public Task<ApiResult<T>> PostApiResultAsync<T>(
        string relativeUri,
        object body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return _httpClient.PostApiResultAsync<T>(relativeUri, body, cancellationToken);
    }

    public Task<ApiResult<ApiResponseHeaders>> PostForHeadersApiResultAsync(
        string relativeUri,
        object body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return _httpClient.PostForHeadersApiResultAsync(relativeUri, body, cancellationToken);
    }
}
