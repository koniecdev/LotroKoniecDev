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
                LogRefreshFailed(_logger, (int)response.StatusCode, null);
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

    private static readonly Action<ILogger, int, Exception?> LogRefreshFailed =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(1, nameof(LogRefreshFailed)),
            "Refresh token grant failed with status {StatusCode}.");

    private static readonly Action<ILogger, Exception> LogRefreshError =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2, nameof(LogRefreshError)),
            "Refresh token grant threw an exception.");
}
