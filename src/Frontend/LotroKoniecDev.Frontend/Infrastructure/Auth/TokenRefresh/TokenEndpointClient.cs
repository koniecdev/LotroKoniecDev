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
    private const string TokenParam = "token";
    private const string TokenTypeHintParam = "token_type_hint";
    private const string RefreshTokenTypeHint = "refresh_token";
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
                await LogRefusalAsync(response, LogRefreshRefused, LogRefreshFailed);
                return null;
            }

            return await ReadJsonAsync<TokenResponse>(response.Content);
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

    public async Task RevokeRefreshTokenAsync(
        Uri revocationEndpoint,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        using FormUrlEncodedContent content = new(
        [
            new KeyValuePair<string, string>(TokenParam, refreshToken),
            new KeyValuePair<string, string>(TokenTypeHintParam, RefreshTokenTypeHint),
            new KeyValuePair<string, string>(ClientIdParam, _authSystemOptions.Value.ClientId)
        ]);

        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsync(
                revocationEndpoint, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                await LogRefusalAsync(response, LogRevocationRefused, LogRevocationFailed);
            }
        }
        catch (HttpRequestException ex)
        {
            LogRevocationError(_logger, ex);
        }
        catch (OperationCanceledException ex)
        {
            LogRevocationError(_logger, ex);
        }
    }

    /// <summary>
    /// Only the two OAuth fields are logged, never the raw body, so nothing else the answer carries can
    /// reach the log. For a body of any other shape, the warning holds the status code alone (#914).
    /// </summary>
    private async Task LogRefusalAsync(
        HttpResponseMessage response,
        Action<ILogger, int, string?, string?, Exception?> logRefused,
        Action<ILogger, int, Exception?> logFailed)
    {
        int statusCode = (int)response.StatusCode;

        if (await TryReadErrorAsync(response.Content) is { } errorResponse
            && !string.IsNullOrWhiteSpace(errorResponse.Error))
        {
            logRefused(
                _logger, statusCode, ForLog(errorResponse.Error), ForLog(errorResponse.ErrorDescription), null);
            return;
        }

        logFailed(_logger, statusCode, null);
    }

    /// <summary>
    /// The body is only read for the log, and a log line must never turn a refused refresh into an
    /// exception.
    /// </summary>
    private static async Task<TokenErrorResponse?> TryReadErrorAsync(HttpContent content)
    {
        try
        {
            return await ReadJsonAsync<TokenErrorResponse>(content);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the JSON from the bytes, not from a decoded string. Decoding throws on a charset .NET does
    /// not know, such as "utf8" (#914, #943). A body that is not UTF-8 JSON fails with a
    /// <see cref="JsonException"/>, which both callers handle. It takes no cancellation token:
    /// <c>PostAsync</c> has already buffered the body, so there is nothing to wait for.
    /// </summary>
    private static async Task<T?> ReadJsonAsync<T>(HttpContent content)
    {
        await using Stream body = await content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<T>(body, JsonOptions);
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

    private static readonly Action<ILogger, int, Exception?> LogRevocationFailed =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(4, nameof(LogRevocationFailed)),
            "Refresh token revocation at sign-out failed with status {StatusCode}.");

    private static readonly Action<ILogger, int, string?, string?, Exception?> LogRevocationRefused =
        LoggerMessage.Define<int, string?, string?>(
            LogLevel.Warning,
            new EventId(5, nameof(LogRevocationRefused)),
            "Refresh token revocation at sign-out failed with status {StatusCode}. Error: {Error}. Description: {ErrorDescription}");

    private static readonly Action<ILogger, Exception> LogRevocationError =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(6, nameof(LogRevocationError)),
            "Refresh token revocation at sign-out threw an exception.");
}
