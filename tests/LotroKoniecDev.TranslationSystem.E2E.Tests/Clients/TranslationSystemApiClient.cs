using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.E2E.Tests.Extensions;

namespace LotroKoniecDev.TranslationSystem.E2E.Tests.Clients;

/// <summary>
/// Talks to the running tms-api over real HTTP with an optional bearer token. Happy-path methods unwrap the
/// typed response (and surface the API's problem details on failure); the <c>Raw</c> variants return the
/// <see cref="HttpResponseMessage"/> so tests can assert status codes (204, 304, 401) and headers (ETag).
/// </summary>
public sealed class TranslationSystemApiClient : IDisposable
{
    private const string TranslationsRoute = "/api/v1/translations";
    private const string GameVersionsRoute = "/api/v1/game-versions";

    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions;

    public TranslationSystemApiClient(string baseUrl, JsonSerializerOptions jsonOptions, string? bearerToken)
    {
        _client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _jsonOptions = jsonOptions;

        if (!string.IsNullOrEmpty(bearerToken))
        {
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
    }

    public async Task<GameVersionResponse> RegisterGameVersionAsync(string version)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            GameVersionsRoute, new RegisterGameVersionRequest(version), _jsonOptions);
        string content = await response.EnsureSuccessWithDetailsAsync();
        return Deserialize<GameVersionResponse>(content);
    }

    public async Task<ImportSummary> ImportAsync(Guid gameVersionId, params string[] lines)
    {
        using ByteArrayContent fileContent = new(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        using MultipartFormDataContent form = new() { { fileContent, "file", "exported.txt" } };

        HttpResponseMessage response = await _client.PostAsync(
            new Uri($"{GameVersionsRoute}/{gameVersionId}/import", UriKind.Relative), form);
        string content = await response.EnsureSuccessWithDetailsAsync();
        return Deserialize<ImportSummary>(content);
    }

    public async Task<PaginationResponse<TranslationListItemResponse>> ListTranslationsAsync()
    {
        HttpResponseMessage response = await _client.GetAsync(new Uri(TranslationsRoute, UriKind.Relative));
        string content = await response.EnsureSuccessWithDetailsAsync();
        return Deserialize<PaginationResponse<TranslationListItemResponse>>(content);
    }

    public async Task<HttpResponseMessage> ListTranslationsRawAsync() =>
        await _client.GetAsync(new Uri(TranslationsRoute, UriKind.Relative));

    public async Task<TranslationDetailResponse> GetTranslationAsync(Guid id)
    {
        HttpResponseMessage response = await _client.GetAsync(new Uri($"{TranslationsRoute}/{id}", UriKind.Relative));
        string content = await response.EnsureSuccessWithDetailsAsync();
        return Deserialize<TranslationDetailResponse>(content);
    }

    public async Task<TranslationDetailResponse> UpsertAsync(int fileId, long gossipId, string translatedText)
    {
        HttpResponseMessage response = await _client.PutAsJsonAsync(
            TranslationsRoute, new UpsertTranslationRequest(fileId, gossipId, translatedText), _jsonOptions);
        string content = await response.EnsureSuccessWithDetailsAsync();
        return Deserialize<TranslationDetailResponse>(content);
    }

    public async Task<HttpResponseMessage> ApproveRawAsync(Guid id) =>
        await _client.PostAsync(new Uri($"{TranslationsRoute}/{id}/approve", UriKind.Relative), content: null);

    public async Task<HttpResponseMessage> DownloadFileRawAsync(string language, EntityTagHeaderValue? ifNoneMatch = null)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get, new Uri($"/api/v1/translation-files/{language}", UriKind.Relative));

        if (ifNoneMatch is not null)
        {
            request.Headers.IfNoneMatch.Add(ifNoneMatch);
        }

        return await _client.SendAsync(request);
    }

    private T Deserialize<T>(string content) =>
        JsonSerializer.Deserialize<T>(content, _jsonOptions)
        ?? throw new InvalidOperationException($"Null response deserializing {typeof(T).Name}.");

    public void Dispose() => _client.Dispose();
}
