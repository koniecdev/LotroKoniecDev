using LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth.SignOut;

/// <summary>
/// At sign-out the website revokes its own refresh token, server to server, before it sends the browser
/// to the auth server's end-session page (#964). The browser may never get there: the tab closes, the
/// network drops, or the auth server is restarting. Without this call, a copied website cookie could keep
/// renewing itself.
/// The cookie check does the same when the website ends a session on its own (#1027), because there the
/// browser is never sent to the end-session page at all.
/// The endpoint comes from the auth server's discovery document, never from a path of our own (#610).
/// It is best effort: a failure is logged and never stops the sign-out.
/// </summary>
internal sealed class RefreshTokenRevoker
{
    /// <summary>
    /// The request that ends the session waits for this call, so a stuck auth server must not hold it for
    /// long.
    /// </summary>
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
    /// Takes no cancellation token on purpose. A browser that drops the sign-out request, for example by
    /// closing the tab, is exactly the case this revoke exists for, so the request's abort must not stop it.
    /// A caller passes every token it holds: a missing, blank or repeated one is skipped. All of them share
    /// one time limit, so two tokens never hold the request longer than one.
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

            // Side by side, so a slow first revoke does not use up the time of the second one. The lambda is
            // async, so a client that throws before it returns a task fails only its own revoke.
            await Task.WhenAll(tokensToRevoke.Select(async token =>
                await _tokenEndpointClient.RevokeRefreshTokenAsync(revocationEndpoint, token, timeLimit.Token)));
        }
        catch (Exception exception)
        {
            // Every failure we expect is logged further in. This catches the rest, because an exception
            // here would undo the sign-out: the error page drops the header that deletes the cookie.
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
            LogNoConfigurationManager(_logger, null);
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
            LogNoRevocationEndpoint(_logger, null);
            return null;
        }

        return revocationEndpoint;
    }

    private static bool IsWebAddress(Uri address) =>
        address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp;

    private static readonly Action<ILogger, Exception?> LogDiscoveryUnavailable =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1, nameof(LogDiscoveryUnavailable)),
            "The auth server's discovery document could not be read, so no refresh token was revoked.");

    private static readonly Action<ILogger, Exception?> LogNoRevocationEndpoint =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2, nameof(LogNoRevocationEndpoint)),
            "The auth server's discovery document has no usable revocation endpoint, so no refresh token was revoked.");

    private static readonly Action<ILogger, Exception?> LogNoConfigurationManager =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(3, nameof(LogNoConfigurationManager)),
            "The OIDC handler has no configuration manager, so no refresh token was revoked.");

    private static readonly Action<ILogger, Exception?> LogRevocationFailedUnexpectedly =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(4, nameof(LogRevocationFailedUnexpectedly)),
            "Refresh token revocation failed with an unexpected exception.");
}
