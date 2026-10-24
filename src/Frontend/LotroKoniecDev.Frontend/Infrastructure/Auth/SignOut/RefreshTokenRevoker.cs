using LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth.SignOut;

/// <summary>
/// Revokes the website's refresh token server to server, because the browser may never reach the auth
/// server's end-session page, and a copied cookie could keep renewing itself (#964, #1027). The endpoint
/// comes from the discovery document (#610). Best effort: a failure is logged and never stops the
/// sign-out.
/// </summary>
internal sealed partial class RefreshTokenRevoker
{
    /// <summary>The request that ends the session waits for this call.</summary>
    internal static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(5);

    private readonly ITokenEndpointClient _tokenEndpointClient;
    private readonly IOptionsMonitor<OpenIdConnectOptions> _openIdConnectOptionsMonitor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RefreshTokenRevoker> _logger;

    public RefreshTokenRevoker(
        ITokenEndpointClient tokenEndpointClient,
        IOptionsMonitor<OpenIdConnectOptions> openIdConnectOptionsMonitor,
        TimeProvider timeProvider,
        ILogger<RefreshTokenRevoker> logger)
    {
        _tokenEndpointClient = tokenEndpointClient;
        _openIdConnectOptionsMonitor = openIdConnectOptionsMonitor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// No cancellation token: a browser that drops the sign-out request is the case this revoke exists for.
    /// A missing, blank or repeated token is skipped, and all tokens share one time limit.
    /// </summary>
    public async Task RevokeAsync(params IReadOnlyCollection<string?> refreshTokens)
    {
        string[] tokensToRevoke = refreshTokens
            .OfType<string>()
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (tokensToRevoke.Length == 0)
        {
            return;
        }

        using CancellationTokenSource timeLimit = new(TimeLimit, _timeProvider);

        try
        {
            Uri? revocationEndpoint = await ResolveRevocationEndpointAsync(timeLimit.Token);
            if (revocationEndpoint is null)
            {
                return;
            }

            // Side by side, so a slow first revoke does not use up the second one's time. The async lambda
            // keeps a client that throws before returning a task from failing the other revoke.
            await Task.WhenAll(tokensToRevoke.Select(async token =>
                await _tokenEndpointClient.RevokeRefreshTokenAsync(revocationEndpoint, token, timeLimit.Token)));
        }
        catch (Exception exception)
        {
            // An exception here would undo the sign-out: the error page drops the header that deletes the
            // cookie.
            LogRevocationFailedUnexpectedly(_logger, exception);
        }
    }

    private async Task<Uri?> ResolveRevocationEndpointAsync(CancellationToken cancellationToken)
    {
        IConfigurationManager<OpenIdConnectConfiguration>? configurationManager = _openIdConnectOptionsMonitor
            .Get(OpenIdConnectDefaults.AuthenticationScheme)
            .ConfigurationManager;
        if (configurationManager is null)
        {
            LogNoConfigurationManager(_logger);
            return null;
        }

        OpenIdConnectConfiguration configuration;
        try
        {
            // WaitAsync keeps the time limit even if the manager does not watch the token on its own.
            configuration = await configurationManager
                .GetConfigurationAsync(cancellationToken)
                .WaitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            LogDiscoveryUnavailable(_logger, exception);
            return null;
        }

        // On Linux and macOS a path such as "/connect/revoke" also parses as absolute, as a file:// address.
        if (!Uri.TryCreate(configuration.RevocationEndpoint, UriKind.Absolute, out Uri? revocationEndpoint)
            || !IsWebAddress(revocationEndpoint))
        {
            LogNoRevocationEndpoint(_logger);
            return null;
        }

        return revocationEndpoint;
    }

    private static bool IsWebAddress(Uri address) =>
        address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp;

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "The auth server's discovery document could not be read, so no refresh token was revoked.")]
    private static partial void LogDiscoveryUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "The auth server's discovery document has no usable revocation endpoint, so no refresh token was revoked.")]
    private static partial void LogNoRevocationEndpoint(ILogger logger);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "The OIDC handler has no configuration manager, so no refresh token was revoked.")]
    private static partial void LogNoConfigurationManager(ILogger logger);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "Refresh token revocation failed with an unexpected exception.")]
    private static partial void LogRevocationFailedUnexpectedly(ILogger logger, Exception exception);
}
