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
/// Runs on every cookie validation (<c>OnValidatePrincipal</c>).
/// First it reads any "dead session" marker a previous 401 left behind and signs the cookie out
/// properly. Otherwise it refreshes the access token shortly before it expires, using the stored
/// refresh token. When the token is still valid by the local clock, it also checks the token's
/// signature against the cached OIDC keys, so a key that was rotated upstream signs the user out
/// cleanly instead of letting a token that is already dead reach the API.
/// Every rejection sets the one-time "session expired" notice. A user's own <c>/auth/logout</c> does not
/// come through here, so it never sets it.
/// </summary>
internal sealed class CookieTokenRefresher
{
    private const string AccessTokenName = "access_token";
    private const string RefreshTokenName = "refresh_token";
    private const string IdTokenName = "id_token";
    private const string ExpiresAtName = "expires_at";
    private const string SubjectClaimType = "sub";

    /// <summary>
    /// Refresh a little before the token really expires, so a call already on its way cannot arrive with
    /// a token that still looks valid here but is already refused by the server.
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

        // The fallback path: on an earlier request the TMS delegating handler saw a 401 and marked this
        // subject's session dead. That response may already have been streaming, so the clean sign-out
        // waits until here, where the response has not started. It runs first, so a session we know is
        // dead never reaches the API again.
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
            // A token we just refreshed came from the identity provider over TLS on this very request,
            // so its signing key cannot have changed since. The signature check below would find nothing
            // and could only fail wrongly against local keys that are a moment out of date.
            return;
        }

        // A check we do ourselves: even when the token is still valid by the local clock, its signing key
        // may have been rotated upstream while the frontend still trusts the old keys. We check the
        // signature against the cached OIDC keys, with no call to the API.
        // Lifetime and audience are deliberately not checked here: the window above handles expiry, and
        // this frontend is not the token's audience.
        if (!await IsAccessTokenCryptographicallyValidAsync(context, cancellationToken))
        {
            LogProactiveInvalidToken(_logger, null);
            await RejectAsync(context);
        }
    }

    /// <returns>
    /// <see cref="RefreshOutcome.Stop"/> when validation must stop, because there was no expiry claim or
    /// the principal was already rejected. <see cref="RefreshOutcome.Refreshed"/> when a new token was
    /// just fetched, so the caller skips the signature check. Otherwise
    /// <see cref="RefreshOutcome.Unchanged"/>, and the token that is still valid should be checked.
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
            // There is no token to check. Leave the decision to the refresh path, which already ran.
            return true;
        }

        OpenIdConnectOptions oidcOptions = _openIdConnectOptionsMonitor.Get(
            OpenIdConnectDefaults.AuthenticationScheme);
        if (oidcOptions.ConfigurationManager is null)
        {
            // Without a configuration manager we cannot fetch the keys, so we must not log the user out.
            return true;
        }

        OpenIdConnectConfiguration configuration = await oidcOptions.ConfigurationManager
            .GetConfigurationAsync(cancellationToken);

        if (await TryValidateAsync(
                accessToken, configuration.SigningKeys, configuration.Issuer, cancellationToken))
        {
            return true;
        }

        // A normal key change the frontend has not fetched yet would fail the first check. Force a
        // metadata refresh and check once more before deciding the token is really dead.
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
        // The issuer comes from the discovery document through ConfigurationManager, the same source the
        // provider writes into the token's 'iss'. So the two cannot differ the way a hand-written
        // Authority can, with a trailing slash, an internal URL or an empty value in production.
        // If discovery has not given us an issuer yet, skip the check instead of logging the user out.
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
        // Set the one-time notice before signing out, so it is written while the response has not
        // started. A user's own /auth/logout signs out directly and not through this path, so it never
        // sets the notice. That is the rule: it is not shown to people who logged out themselves.
        _sessionExpiryNotice.Raise();
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// The result of the refresh attempt, which tells <see cref="ValidateAsync"/> whether the signature
    /// check still has anything to look at.
    /// </summary>
    private enum RefreshOutcome
    {
        /// <summary>
        /// Validation must stop: there was no usable <c>expires_at</c> claim, or the refresh attempt has
        /// already rejected the principal.
        /// </summary>
        Stop,

        /// <summary>
        /// The token was not touched and is still valid by the local clock. This is the one case where a
        /// key change upstream is worth checking for.
        /// </summary>
        Unchanged,

        /// <summary>
        /// A new token was fetched from the identity provider on this request, so the signature check is
        /// skipped.
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
