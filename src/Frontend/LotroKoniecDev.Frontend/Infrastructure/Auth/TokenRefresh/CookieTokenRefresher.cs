using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;

/// <summary>
/// Runs on every cookie validation (<c>OnValidatePrincipal</c>). It first consumes any reactive
/// "dead session" marker raised by a prior 401 and signs the cookie out cleanly; otherwise it
/// refreshes the access token shortly before expiry using the stored refresh token, and — when the
/// token is still locally alive — proactively re-validates its signature against the cached OIDC JWKS
/// so a key rotated upstream signs the user out cleanly instead of letting a doomed token reach the
/// API. Any rejection raises a one-shot "session expired" notice (a deliberate <c>/auth/logout</c>
/// does not pass through here, so it never raises the notice).
/// </summary>
internal sealed class CookieTokenRefresher
{
    private const string AccessTokenName = "access_token";
    private const string RefreshTokenName = "refresh_token";
    private const string IdTokenName = "id_token";
    private const string ExpiresAtName = "expires_at";
    private const string SubjectClaimType = "sub";

    /// <summary>
    /// Refresh slightly before actual expiry so an in-flight backend call cannot land with a token that
    /// is technically alive locally but already rejected upstream.
    /// </summary>
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);

    private readonly ITokenEndpointClient _tokenEndpointClient;
    private readonly IOptionsMonitor<OpenIdConnectOptions> _openIdConnectOptionsMonitor;
    private readonly IDeadSessionRegistry _deadSessionRegistry;
    private readonly ISessionExpiryNotice _sessionExpiryNotice;
    private readonly ILogger<CookieTokenRefresher> _logger;

    public CookieTokenRefresher(
        ITokenEndpointClient tokenEndpointClient,
        IOptionsMonitor<OpenIdConnectOptions> openIdConnectOptionsMonitor,
        IDeadSessionRegistry deadSessionRegistry,
        ISessionExpiryNotice sessionExpiryNotice,
        ILogger<CookieTokenRefresher> logger)
    {
        _tokenEndpointClient = tokenEndpointClient;
        _openIdConnectOptionsMonitor = openIdConnectOptionsMonitor;
        _deadSessionRegistry = deadSessionRegistry;
        _sessionExpiryNotice = sessionExpiryNotice;
        _logger = logger;
    }

    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        CancellationToken cancellationToken = context.HttpContext.RequestAborted;

        // Reactive backstop: a 401 observed by the TMS delegating handler on a prior request marked
        // this subject's session dead. The response there may already have been streaming, so the clean
        // sign-out is deferred to here, where the response has not started. This runs first so a
        // known-dead session never reaches the API again.
        string? subject = GetSubject(context);
        if (subject is not null
            && await _deadSessionRegistry.ConsumeAsync(subject, cancellationToken))
        {
            LogReactiveDeadSession(_logger, null);
            await RejectAsync(context);
            return;
        }

        RefreshOutcome refreshOutcome = await TryRefreshIfNearExpiryAsync(context, cancellationToken);
        if (refreshOutcome is RefreshOutcome.Stop)
        {
            return;
        }

        if (refreshOutcome is RefreshOutcome.Refreshed)
        {
            // A token we just refreshed was minted by the IdP over TLS on this very request, so its
            // signing key cannot have rotated out from under it yet: the proactive JWKS probe below
            // would have nothing to catch and could only misfire against momentarily-stale local JWKS.
            return;
        }

        // Proactive local validation: even when the token is alive on the local clock, its signing key
        // may have rotated upstream (the FE still trusts the previous JWKS). Validate the signature
        // against the cached OIDC keys — no API round-trip. Lifetime/audience are intentionally NOT
        // checked here: the skew window above owns expiry, and the FE is not the token's audience.
        if (!await IsAccessTokenCryptographicallyValidAsync(context, cancellationToken))
        {
            LogProactiveInvalidToken(_logger, null);
            await RejectAsync(context);
        }
    }

    /// <returns>
    /// <see cref="RefreshOutcome.Stop"/> when validation must stop (no expiry claim, or the principal
    /// was already rejected); <see cref="RefreshOutcome.Refreshed"/> when a fresh token was just
    /// obtained from the IdP, so the caller must skip the proactive signature probe; otherwise
    /// <see cref="RefreshOutcome.Unchanged"/> when the still-alive token should be probed.
    /// </returns>
    private async Task<RefreshOutcome> TryRefreshIfNearExpiryAsync(
        CookieValidatePrincipalContext context,
        CancellationToken cancellationToken)
    {
        AuthenticationProperties properties = context.Properties;

        string? expiresAtRaw = properties.GetTokenValue(ExpiresAtName);
        if (string.IsNullOrEmpty(expiresAtRaw)
            || !DateTimeOffset.TryParse(
                expiresAtRaw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset expiresAt))
        {
            return RefreshOutcome.Stop;
        }

        if (DateTimeOffset.UtcNow + RefreshSkew < expiresAt)
        {
            return RefreshOutcome.Unchanged;
        }

        string? refreshToken = properties.GetTokenValue(RefreshTokenName);
        if (string.IsNullOrEmpty(refreshToken))
        {
            LogNoRefreshToken(_logger, null);
            await RejectAsync(context);
            return RefreshOutcome.Stop;
        }

        TokenResponse? tokenResponse = await _tokenEndpointClient.RefreshAsync(
            refreshToken, cancellationToken);

        if (tokenResponse?.AccessToken is null)
        {
            LogRefreshFailed(_logger, null);
            await RejectAsync(context);
            return RefreshOutcome.Stop;
        }

        properties.UpdateTokenValue(AccessTokenName, tokenResponse.AccessToken);

        if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
        {
            properties.UpdateTokenValue(RefreshTokenName, tokenResponse.RefreshToken);
        }

        if (!string.IsNullOrEmpty(tokenResponse.IdToken))
        {
            properties.UpdateTokenValue(IdTokenName, tokenResponse.IdToken);
        }

        DateTimeOffset newExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn);
        properties.UpdateTokenValue(
            ExpiresAtName,
            newExpiresAt.ToString("o", CultureInfo.InvariantCulture));

        context.ShouldRenew = true;
        return RefreshOutcome.Refreshed;
    }

    private async Task<bool> IsAccessTokenCryptographicallyValidAsync(
        CookieValidatePrincipalContext context,
        CancellationToken cancellationToken)
    {
        string? accessToken = context.Properties.GetTokenValue(AccessTokenName);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            // No token to validate — leave the decision to the skew/refresh path, which already ran.
            return true;
        }

        OpenIdConnectOptions oidcOptions = _openIdConnectOptionsMonitor.Get(
            OpenIdConnectDefaults.AuthenticationScheme);
        if (oidcOptions.ConfigurationManager is null)
        {
            // Without a configuration manager we cannot fetch JWKS; do not falsely log the user out.
            return true;
        }

        OpenIdConnectConfiguration configuration = await oidcOptions.ConfigurationManager
            .GetConfigurationAsync(cancellationToken);

        if (await TryValidateAsync(
                accessToken, configuration.SigningKeys, configuration.Issuer, cancellationToken))
        {
            return true;
        }

        // A benign key roll the FE has not refetched yet would fail the first pass. Force a metadata
        // refresh and re-validate exactly once before concluding the token is truly dead.
        oidcOptions.ConfigurationManager.RequestRefresh();
        OpenIdConnectConfiguration refreshedConfiguration = await oidcOptions.ConfigurationManager
            .GetConfigurationAsync(cancellationToken);

        return await TryValidateAsync(
            accessToken, refreshedConfiguration.SigningKeys, refreshedConfiguration.Issuer, cancellationToken);
    }

    [SuppressMessage(
        "Security",
        "CA5404:Do not disable token validation checks",
        Justification =
            "Intentional: this is a cryptographic-validity / key-rotation probe, not a full access-token "
            + "validation. Audience is disabled because the FE is not the token's audience (the API is), "
            + "and lifetime is disabled because the skew/refresh window above already owns expiry — "
            + "re-checking it here would double-handle and could falsely reject.")]
    private static async Task<bool> TryValidateAsync(
        string accessToken,
        IEnumerable<SecurityKey> signingKeys,
        string? issuer,
        CancellationToken cancellationToken)
    {
        // Issuer is anchored on the discovery document (ConfigurationManager), the same source the
        // provider stamps into the token 'iss' — so they cannot drift the way a hand-configured Authority
        // can (trailing slash, internal URL, empty-by-default in prod). If discovery has not yielded an
        // issuer yet, skip rather than falsely log the user out.
        if (string.IsNullOrWhiteSpace(issuer))
        {
            return true;
        }

        TokenValidationParameters validationParameters = new()
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = false,
            ValidateLifetime = false
        };

        TokenValidationResult result = await new JsonWebTokenHandler()
            .ValidateTokenAsync(accessToken, validationParameters);

        cancellationToken.ThrowIfCancellationRequested();
        return result.IsValid;
    }

    private static string? GetSubject(CookieValidatePrincipalContext context)
    {
        string? subject = context.Principal?.FindFirst(SubjectClaimType)?.Value;
        return string.IsNullOrWhiteSpace(subject) ? null : subject;
    }

    private async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        // Raise the one-shot soft notice BEFORE sign-out so it is written while the response has not
        // started. A deliberate /auth/logout signs out directly (not through this rejection path), so
        // it never raises the notice — exactly the "not shown to users who logged out" rule.
        _sessionExpiryNotice.Raise();
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Result of the near-expiry refresh attempt, telling <see cref="ValidateAsync"/> whether the
    /// proactive JWKS signature probe still has anything to validate.
    /// </summary>
    private enum RefreshOutcome
    {
        /// <summary>
        /// Validation must stop: there was no usable <c>expires_at</c> claim, or the principal was
        /// already rejected inside the refresh attempt.
        /// </summary>
        Stop,

        /// <summary>
        /// The token was left untouched and is still alive on the local clock — the one case worth
        /// probing for an upstream key roll.
        /// </summary>
        Unchanged,

        /// <summary>
        /// A fresh token was just obtained from the IdP this request, so the proactive signature probe
        /// is skipped.
        /// </summary>
        Refreshed
    }

    private static readonly Action<ILogger, Exception?> LogNoRefreshToken =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(LogNoRefreshToken)),
            "Cookie has no refresh_token; principal rejected.");

    private static readonly Action<ILogger, Exception?> LogRefreshFailed =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(2, nameof(LogRefreshFailed)),
            "Refresh token grant returned no access_token; principal rejected.");

    private static readonly Action<ILogger, Exception?> LogProactiveInvalidToken =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(3, nameof(LogProactiveInvalidToken)),
            "Access token failed local JWKS signature validation after a metadata refresh; principal rejected.");

    private static readonly Action<ILogger, Exception?> LogReactiveDeadSession =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(4, nameof(LogReactiveDeadSession)),
            "Session was marked dead by a prior 401; principal rejected.");
}
