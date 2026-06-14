using LotroKoniecDev.TranslationSystem.Contracts.Discovery;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;

internal sealed class TranslationSystemClient : ITranslationSystemClient
{
    private const string HealthRelativeUri = "health";
    private const string DiscoveryRelativeUri = "";

    private readonly HttpClient _httpClient;

    public TranslationSystemClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public Task<ApiResult<HealthStatusResponse>> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        return _httpClient.GetApiResultAsync<HealthStatusResponse>(HealthRelativeUri, cancellationToken);
    }

    public Task<ApiResult<DiscoveryResponse>> GetDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        return _httpClient.GetApiResultAsync<DiscoveryResponse>(DiscoveryRelativeUri, cancellationToken);
    }

    public Task<ApiResult<T>> GetApiResultAsync<T>(string relativeUri, CancellationToken cancellationToken = default)
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

    public Task<ApiResult> PutApiResultAsync(
        string relativeUri,
        object body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return _httpClient.PutApiResultAsync(relativeUri, body, cancellationToken);
    }

    public Task<ApiResult<T>> PutApiResultAsync<T>(
        string relativeUri,
        object body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return _httpClient.PutApiResultAsync<T>(relativeUri, body, cancellationToken);
    }

    public Task<ApiResult> PutApiResultAsync(
        string relativeUri,
        CancellationToken cancellationToken = default)
    {
        return _httpClient.PutApiResultAsync(relativeUri, cancellationToken);
    }

    public Task<ApiResult> PatchApiResultAsync(
        string relativeUri,
        object body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return _httpClient.PatchApiResultAsync(relativeUri, body, cancellationToken);
    }

    public Task<ApiResult> DeleteApiResultAsync(
        string relativeUri,
        CancellationToken cancellationToken = default)
    {
        return _httpClient.DeleteApiResultAsync(relativeUri, cancellationToken);
    }

    public Task<ApiResult> SendMultipartApiResultAsync(
        HttpMethod method,
        string relativeUri,
        MultipartFormDataContent content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return _httpClient.SendMultipartApiResultAsync(method, relativeUri, content, cancellationToken);
    }

    public Task<ApiResult<T>> SendMultipartApiResultAsync<T>(
        HttpMethod method,
        string relativeUri,
        MultipartFormDataContent content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return _httpClient.SendMultipartApiResultAsync<T>(method, relativeUri, content, cancellationToken);
    }
}
