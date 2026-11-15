using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth.SignOut;

/// <summary>
/// Ends the session at the sign-in server after the cookie is gone. The end-session redirect works only if
/// the browser reaches the sign-in server, so the refresh token is revoked server to server first (#964),
/// and the redirect stays for the sign-in server's own cookie. Both addresses come from the discovery
/// document (#1114). This is not the OIDC handler's own sign-out (ADR-0067 decision 5): that one sends a
/// return address the sign-in server does not list for this client, adds a state and telemetry parameters,
/// and reads the document with no time limit.
/// </summary>
internal sealed partial class SignInServerSignOut
{
    private const string HomePage = "/";

    private readonly RefreshTokenRevoker _refreshTokenRevoker;
    private readonly SignInServerDiscovery _signInServerDiscovery;
    private readonly IOptions<AuthSystemSettings> _authSystemOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SignInServerSignOut> _logger;

    /// <summary>
    /// No cancellation token: a browser that drops the request is the case the revoke exists for. When the
    /// discovery document cannot be read or names no usable end-session address, the sign-out ends on the
    /// home page (ADR-0067 decision 3). The sign-in server's own cookie then stays in this browser.
    /// </summary>
    public async Task<string> EndSessionAsync(string? idToken, string? refreshToken, string postLogoutRedirectUri)
    {
        try
        {
            // One limit for the read and the revoke, so a stuck sign-in server holds the sign-out only once.
            using CancellationTokenSource timeLimit = new(RefreshTokenRevoker.TimeLimit, _timeProvider);

            SignInServerDiscoveryResult discovery = await _signInServerDiscovery.ReadAsync(timeLimit.Token);
            if (discovery.Configuration is not { } configuration)
            {
                LogNoDiscoveryDocument(discovery.Failure);
                return HomePage;
            }

            await _refreshTokenRevoker.RevokeAsync(configuration, refreshToken, timeLimit.Token);

            return EndSessionUrl(configuration, idToken, postLogoutRedirectUri) ?? HomePage;
        }
        catch (Exception exception)
        {
            // An exception here would undo the sign-out: the error page drops the header that deletes the
            // cookie.
            LogSignOutFailedUnexpectedly(_logger, exception);
            return HomePage;
        }
    }

    public SignInServerSignOut(
        RefreshTokenRevoker refreshTokenRevoker,
        SignInServerDiscovery signInServerDiscovery,
        IOptions<AuthSystemSettings> authSystemOptions,
        TimeProvider timeProvider,
        ILogger<SignInServerSignOut> logger)
    {
        _refreshTokenRevoker = refreshTokenRevoker;
        _signInServerDiscovery = signInServerDiscovery;
        _authSystemOptions = authSystemOptions;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// The address must be on the authority's origin: the ID token in the hint names the user, and the
    /// website's own CSP lets the sign-out form go nowhere else. The hint is sent whenever there is one
    /// (#931), and no telemetry parameters are added.
    /// </summary>
    private string? EndSessionUrl(OpenIdConnectConfiguration configuration, string? idToken, string postLogoutRedirectUri)
    {
        if (SignInServerDiscovery.AsWebAddress(configuration.EndSessionEndpoint) is not { } endSessionEndpoint
            || !UriOrigin.AreSame(endSessionEndpoint, new Uri(_authSystemOptions.Value.Authority)))
        {
            LogNoEndSessionEndpoint(_logger);
            return null;
        }

        return new OpenIdConnectMessage
        {
            IssuerAddress = endSessionEndpoint.AbsoluteUri,
            PostLogoutRedirectUri = postLogoutRedirectUri,
            IdTokenHint = string.IsNullOrWhiteSpace(idToken) ? null : idToken,
            EnableTelemetryParameters = false
        }.CreateLogoutRequestUrl();
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
        Message = "The auth server's discovery document could not be read, so the sign-out skipped the auth server and ends on the home page.")]
    private static partial void LogDiscoveryUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "The auth server's discovery document has no usable end-session endpoint on the authority's origin, so the sign-out ends on the home page.")]
    private static partial void LogNoEndSessionEndpoint(ILogger logger);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "The OIDC handler has no configuration manager, so the sign-out skipped the auth server and ends on the home page.")]
    private static partial void LogNoConfigurationManager(ILogger logger);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "The sign-out at the auth server failed with an unexpected exception, so it ends on the home page.")]
    private static partial void LogSignOutFailedUnexpectedly(ILogger logger, Exception exception);
}
