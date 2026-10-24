using System.Net;
using System.Text.Json;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;

internal sealed partial class TokenEndpointClient : ITokenEndpointClient
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

    public async Task<TokenRefreshResult> RefreshAsync(
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
                return IsTemporaryRefusal(response.StatusCode)
                    ? TokenRefreshResult.TemporaryFailure
                    : TokenRefreshResult.FinalFailure;
            }

            return await ReadJsonAsync<TokenResponse>(response.Content) is { } tokens
                ? TokenRefreshResult.Renewed(tokens)
                : TokenRefreshResult.FinalFailure;
        }
        catch (HttpRequestException ex)
        {
            // PostAsync reads the whole body before it returns, so a body cut off on the way also lands
            // here, as a network error (ADR-0067 decision 3).
            LogRefreshError(_logger, ex);
            return TokenRefreshResult.TemporaryFailure;
        }
        catch (TaskCanceledException ex)
            when (cancellationToken.IsCancellationRequested && ex.CancellationToken == cancellationToken)
        {
            // The browser left. HttpClient puts the caller's token on the exception only when that token was
            // already cancelled, so a timeout stays a warning. The first check covers a token that can never
            // cancel, which matches an exception with no token.
            LogRefreshAbandoned(_logger);
            return TokenRefreshResult.TemporaryFailure;
        }
        catch (TaskCanceledException ex)
        {
            LogRefreshError(_logger, ex);
            return TokenRefreshResult.TemporaryFailure;
        }
        catch (JsonException ex)
        {
            // The whole body arrived and cannot be read, so a retry would get the same answer.
            LogRefreshError(_logger, ex);
            return TokenRefreshResult.FinalFailure;
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
    /// A 429 or any 5xx goes away on its own (#1062). Every other refusal, a redirect and
    /// <c>invalid_grant</c> included, is final.
    /// </summary>
    private static bool IsTemporaryRefusal(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.TooManyRequests || (int)statusCode is >= 500 and <= 599;

    /// <summary>
    /// Only the two OAuth fields are logged, never the raw body (#914).
    /// </summary>
    private async Task LogRefusalAsync(
        HttpResponseMessage response,
        Action<ILogger, int, string?, string?> logRefused,
        Action<ILogger, int> logFailed)
    {
        int statusCode = (int)response.StatusCode;

        if (await TryReadErrorAsync(response.Content) is { } errorResponse
            && !string.IsNullOrWhiteSpace(errorResponse.Error))
        {
            logRefused(_logger, statusCode, ForLog(errorResponse.Error), ForLog(errorResponse.ErrorDescription));
            return;
        }

        logFailed(_logger, statusCode);
    }

    /// <summary>
    /// A log line must never turn a refused refresh into an exception.
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
    /// From the bytes, not a decoded string: decoding throws on a charset .NET does not know, such as
    /// "utf8" (#943). No cancellation token: <c>PostAsync</c> has already buffered the body.
    /// </summary>
    private static async Task<T?> ReadJsonAsync<T>(HttpContent content)
    {
        await using Stream body = await content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<T>(body, JsonOptions);
    }

    /// <summary>
    /// The text comes from another service, so it is cut short. RFC 6749 §5.2 allows only printable ASCII
    /// here, so any other character, such as a line break or a bidi mark, is replaced.
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

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Refresh token grant failed with status {StatusCode}.")]
    private static partial void LogRefreshFailed(ILogger logger, int statusCode);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Refresh token grant failed with status {StatusCode}. Error: {Error}. Description: {ErrorDescription}")]
    private static partial void LogRefreshRefused(ILogger logger, int statusCode, string? error, string? errorDescription);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Refresh token grant threw an exception.")]
    private static partial void LogRefreshError(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Information,
        Message = "Refresh token grant was cancelled because the request was aborted.")]
    private static partial void LogRefreshAbandoned(ILogger logger);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "Refresh token revocation failed with status {StatusCode}.")]
    private static partial void LogRevocationFailed(ILogger logger, int statusCode);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "Refresh token revocation failed with status {StatusCode}. Error: {Error}. Description: {ErrorDescription}")]
    private static partial void LogRevocationRefused(ILogger logger, int statusCode, string? error, string? errorDescription);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "Refresh token revocation threw an exception.")]
    private static partial void LogRevocationError(ILogger logger, Exception exception);
}
