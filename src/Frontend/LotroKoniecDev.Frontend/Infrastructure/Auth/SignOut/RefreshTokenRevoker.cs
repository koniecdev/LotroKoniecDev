using LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;
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
    private readonly SignInServerDiscovery _signInServerDiscovery;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RefreshTokenRevoker> _logger;

    /// <summary>
    /// No cancellation token: a browser that drops the sign-out request is the case this revoke exists for.
    /// A missing, blank or repeated token is skipped, and all tokens share one time limit.
    /// </summary>
    public async Task RevokeAsync(params IReadOnlyCollection<string?> refreshTokens)
    {
        string[] tokensToRevoke = RealTokens(refreshTokens);
        if (tokensToRevoke.Length == 0)
        {
            return;
        }

        using CancellationTokenSource timeLimit = new(TimeLimit, _timeProvider);

        try
        {
            SignInServerDiscoveryResult discovery = await _signInServerDiscovery.ReadAsync(timeLimit.Token);
            if (discovery.Configuration is not { } configuration)
            {
                LogNoDiscoveryDocument(discovery.Failure);
                return;
            }

            await RevokeAtDiscoveredEndpointAsync(configuration, tokensToRevoke, timeLimit.Token);
        }
        catch (Exception exception)
        {
            // An exception here would undo the sign-out: the error page drops the header that deletes the
            // cookie.
            LogRevocationFailedUnexpectedly(_logger, exception);
        }
    }

    /// <summary>
    /// For a sign-out that has already read the discovery document: the revoke shares its copy and its time
    /// limit, so a stuck sign-in server holds the sign-out once, not twice.
    /// </summary>
    public async Task RevokeAsync(
        OpenIdConnectConfiguration configuration,
        string? refreshToken,
        CancellationToken timeLimit)
    {
        string[] tokensToRevoke = RealTokens([refreshToken]);
        if (tokensToRevoke.Length == 0)
        {
            return;
        }

        try
        {
            await RevokeAtDiscoveredEndpointAsync(configuration, tokensToRevoke, timeLimit);
        }
        catch (Exception exception)
        {
            LogRevocationFailedUnexpectedly(_logger, exception);
        }
    }

    public RefreshTokenRevoker(
        ITokenEndpointClient tokenEndpointClient,
        SignInServerDiscovery signInServerDiscovery,
        TimeProvider timeProvider,
        ILogger<RefreshTokenRevoker> logger)
    {
        _tokenEndpointClient = tokenEndpointClient;
        _signInServerDiscovery = signInServerDiscovery;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private static string[] RealTokens(IReadOnlyCollection<string?> refreshTokens) => refreshTokens
        .OfType<string>()
        .Where(token => !string.IsNullOrWhiteSpace(token))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private async Task RevokeAtDiscoveredEndpointAsync(
        OpenIdConnectConfiguration configuration,
        string[] tokensToRevoke,
        CancellationToken timeLimit)
    {
        if (SignInServerDiscovery.AsWebAddress(configuration.RevocationEndpoint) is not { } revocationEndpoint)
        {
            LogNoRevocationEndpoint(_logger);
            return;
        }

        // Side by side, so a slow first revoke does not use up the second one's time. The async lambda
        // keeps a client that throws before returning a task from failing the other revoke.
        await Task.WhenAll(tokensToRevoke.Select(async token =>
            await _tokenEndpointClient.RevokeRefreshTokenAsync(revocationEndpoint, token, timeLimit)));
    }

    private void LogNoDiscoveryDocument(Exception? failure)
    {
        if (failure is null)
        {
            LogNoConfigurationManager(_logger);
            return;
        }

        LogDiscoveryUnavailable(_logger, failure);
    }

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
