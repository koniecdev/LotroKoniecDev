using System.Text.Json;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;

internal sealed class TokenEndpointClient : ITokenEndpointClient
{
    private const string GrantTypeParam = "grant_type";
    private const string RefreshTokenGrantType = "refresh_token";
    private const string RefreshTokenParam = "refresh_token";
    private const string ClientIdParam = "client_id";
    private const int MaxLoggedFieldLength = 200;
    private static readonly Uri TokenRelativeUri = new("connect/token", UriKind.Relative);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IOptions<AuthSystemSettings> _authSystemOptions;
    private readonly ILogger<TokenEndpointClient> _logger;

    public TokenEndpointClient(
        HttpClient httpClient,
        IOptions<AuthSystemSettings> authSystemOptions,
        ILogger<TokenEndpointClient> logger)
    {
        _httpClient = httpClient;
        _authSystemOptions = authSystemOptions;
        _logger = logger;
    }

    public async Task<TokenResponse?> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        using FormUrlEncodedContent content = new(
        [
            new KeyValuePair<string, string>(GrantTypeParam, RefreshTokenGrantType),
            new KeyValuePair<string, string>(RefreshTokenParam, refreshToken),
            new KeyValuePair<string, string>(ClientIdParam, _authSystemOptions.Value.ClientId)
        ]);

        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsync(
                TokenRelativeUri, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                await LogRefusalAsync(response);
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions);
        }
        catch (HttpRequestException ex)
        {
            LogRefreshError(_logger, ex);
            return null;
        }
        catch (TaskCanceledException ex)
        {
            LogRefreshError(_logger, ex);
            return null;
        }
        catch (JsonException ex)
        {
            LogRefreshError(_logger, ex);
            return null;
        }
    }

    /// <summary>
    /// Only the two OAuth fields are logged, never the raw body, so nothing else the answer carries can
    /// reach the log. For a body of any other shape, the warning holds the status code alone (#914).
    /// </summary>
    private async Task LogRefusalAsync(HttpResponseMessage response)
    {
        int statusCode = (int)response.StatusCode;

        if (await TryReadErrorAsync(response.Content) is { } errorResponse
            && !string.IsNullOrWhiteSpace(errorResponse.Error))
        {
            LogRefreshRefused(
                _logger, statusCode, ForLog(errorResponse.Error), ForLog(errorResponse.ErrorDescription), null);
            return;
        }

        LogRefreshFailed(_logger, statusCode, null);
    }

    /// <summary>
    /// Reads the bytes, not a decoded string. Decoding throws on a charset .NET does not know (such as
    /// "utf8"), and a log line must never turn a refused refresh into an exception. For the same reason
    /// it takes no cancellation token: <c>PostAsync</c> has already buffered the body, so there is
    /// nothing to wait for.
    /// </summary>
    private static async Task<TokenErrorResponse?> TryReadErrorAsync(HttpContent content)
    {
        try
        {
            await using Stream body = await content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<TokenErrorResponse>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The text comes from another service, so it is cut short. RFC 6749 §5.2 allows only printable ASCII
    /// in these fields, so any other character is replaced: a line break, a bidi mark or half of a cut
    /// surrogate pair never reaches the log.
    /// </summary>
    private static string? ForLog(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string kept = value.Length > MaxLoggedFieldLength ? value[..MaxLoggedFieldLength] : value;
        string printable = new(kept.Select(character => character is >= ' ' and <= '~' ? character : '?').ToArray());
        return kept.Length < value.Length ? $"{printable}..." : printable;
    }

    private static readonly Action<ILogger, int, Exception?> LogRefreshFailed =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(1, nameof(LogRefreshFailed)),
            "Refresh token grant failed with status {StatusCode}.");

    private static readonly Action<ILogger, int, string?, string?, Exception?> LogRefreshRefused =
        LoggerMessage.Define<int, string?, string?>(
            LogLevel.Warning,
            new EventId(3, nameof(LogRefreshRefused)),
            "Refresh token grant failed with status {StatusCode}. Error: {Error}. Description: {ErrorDescription}");

    private static readonly Action<ILogger, Exception> LogRefreshError =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2, nameof(LogRefreshError)),
            "Refresh token grant threw an exception.");
}
