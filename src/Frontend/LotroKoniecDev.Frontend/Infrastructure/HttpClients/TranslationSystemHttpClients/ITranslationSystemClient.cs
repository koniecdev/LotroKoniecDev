using LotroKoniecDev.TranslationSystem.Contracts.Discovery;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;

internal interface ITranslationSystemClient
{
    Task<ApiResult<DiscoveryResponse>> GetDiscoveryAsync(CancellationToken cancellationToken = default);

    Task<ApiResult<T>> GetApiResultAsync<T>(
        string relativeUri,
        CancellationToken cancellationToken = default);

    /// <summary>The caller disposes the stream.</summary>
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
