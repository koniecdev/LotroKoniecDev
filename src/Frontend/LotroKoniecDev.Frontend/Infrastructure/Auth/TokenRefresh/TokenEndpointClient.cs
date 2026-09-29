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
                await LogRefusalAsync(response, cancellationToken);
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
    private async Task LogRefusalAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        int statusCode = (int)response.StatusCode;
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (TryReadError(body) is { Error: { } error } errorResponse && !string.IsNullOrWhiteSpace(error))
        {
            LogRefreshRefused(_logger, statusCode, ForLog(error), ForLog(errorResponse.ErrorDescription), null);
            return;
        }

        LogRefreshFailed(_logger, statusCode, null);
    }

    private static TokenErrorResponse? TryReadError(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<TokenErrorResponse>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The text comes from another service, so it is cut short and kept on one line.
    /// </summary>
    private static string? ForLog(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string kept = value.Length > MaxLoggedFieldLength ? value[..MaxLoggedFieldLength] : value;
        string oneLine = new(kept.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return kept.Length < value.Length ? $"{oneLine}…" : oneLine;
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
