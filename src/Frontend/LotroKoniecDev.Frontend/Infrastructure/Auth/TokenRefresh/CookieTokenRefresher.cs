using System.Buffers;
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
using LotroKoniecDev.Frontend.Infrastructure.Auth.SignOut;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;

/// <summary>
/// Runs on every cookie validation (<c>OnValidatePrincipal</c>).
/// First it reads the "dead session" marker that an earlier refused API call may have left on this
/// cookie's access token, and signs the cookie out properly. A cookie with no expiry, no usable access
/// token or a blank refresh token is signed out too.
/// Otherwise it refreshes the access token shortly before it expires, at the time
/// <see cref="AccessTokenRefreshSchedule"/> sets, using the stored refresh token. When the token is still
/// valid by the local clock, it also checks the token's signature against the cached OIDC keys, so a key
/// that was rotated upstream signs the user out cleanly instead of letting a token that is already dead
/// reach the API.
/// A renewal that fails for a moment (a 429, a 5xx, a network error, a timeout) keeps the session while its
/// access token still works, and the next request tries again (ADR-0067 decision 3, #1062).
/// Every rejection sets the one-time "session expired" notice and revokes the session's refresh token
/// (#1027). The one exception is a browser that left during the renewal, which comes back with the same
/// token. On the user's own sign-out (<c>/auth/logout</c> and <c>/auth/local-signout</c>) it checks nothing,
/// so it never sets the notice there, and the sign-out revokes the token itself (#964, #1027).
/// </summary>
internal sealed class CookieTokenRefresher
{
    private const string AccessTokenName = "access_token";
    private const string RefreshTokenName = "refresh_token";
    private const string IdTokenName = "id_token";

    private static readonly SearchValues<char> Base64UrlCharacters = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    private static readonly Action<ILogger, Exception?> LogNoRefreshToken =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(LogNoRefreshToken)),
            "Cookie has no refresh_token; principal rejected.");

    private static readonly Action<ILogger, Exception?> LogRefreshFailed =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(2, nameof(LogRefreshFailed)),
            "Refresh token grant returned no usable answer; principal rejected.");

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

    private static readonly Action<ILogger, Exception?> LogNoUsableAccessToken =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(5, nameof(LogNoUsableAccessToken)),
            "Refresh token grant returned a missing, empty or blank access_token; principal rejected.");

    private static readonly Action<ILogger, string, Exception?> LogNoPositiveLifetime =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(6, nameof(LogNoPositiveLifetime)),
            "Refresh token grant returned a missing or non-positive expires_in; principal rejected. expires_in: {ExpiresIn}");

    private static readonly Action<ILogger, Exception?> LogMalformedAccessToken =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(7, nameof(LogMalformedAccessToken)),
            "Refresh token grant returned an access_token that is not a compact JWS; principal rejected.");

    private static readonly Action<ILogger, Exception?> LogMalformedRefreshToken =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(8, nameof(LogMalformedRefreshToken)),
            "Refresh token grant returned a refresh_token with characters outside printable ASCII; principal rejected.");

    private static readonly Action<ILogger, Exception?> LogMalformedIdToken =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(9, nameof(LogMalformedIdToken)),
            "Refresh token grant returned an id_token that is not a compact JWS; principal rejected.");

    private static readonly Action<ILogger, Exception?> LogNoUsableExpiry =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(10, nameof(LogNoUsableExpiry)),
            "Cookie has a missing or unreadable expires_at; principal rejected.");

    private static readonly Action<ILogger, Exception?> LogNoStoredAccessToken =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(11, nameof(LogNoStoredAccessToken)),
            "Cookie has a missing, empty or blank access_token; principal rejected.");

    private static readonly Action<ILogger, Exception?> LogBlankStoredRefreshToken =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(12, nameof(LogBlankStoredRefreshToken)),
            "Cookie has an empty or blank refresh_token; principal rejected.");

    private static readonly Action<ILogger, Exception?> LogRefreshPostponed =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(13, nameof(LogRefreshPostponed)),
            "Refresh token grant failed for a moment; the session keeps its access token and the next request tries again.");

    private static readonly Action<ILogger, Exception?> LogRefreshFailedAfterExpiry =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(14, nameof(LogRefreshFailedAfterExpiry)),
            "Refresh token grant failed for a moment after the access token ran out; principal rejected.");

    private readonly ITokenEndpointClient _tokenEndpointClient;
    private readonly IOptionsMonitor<OpenIdConnectOptions> _openIdConnectOptionsMonitor;
    private readonly IDeadSessionRegistry _deadSessionRegistry;
    private readonly ISessionExpiryNotice _sessionExpiryNotice;
    private readonly RefreshTokenRevoker _refreshTokenRevoker;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CookieTokenRefresher> _logger;

    public CookieTokenRefresher(
        ITokenEndpointClient tokenEndpointClient,
        IOptionsMonitor<OpenIdConnectOptions> openIdConnectOptionsMonitor,
        IDeadSessionRegistry deadSessionRegistry,
        ISessionExpiryNotice sessionExpiryNotice,
        RefreshTokenRevoker refreshTokenRevoker,
        TimeProvider timeProvider,
        ILogger<CookieTokenRefresher> logger)
    {
        _tokenEndpointClient = tokenEndpointClient;
        _openIdConnectOptionsMonitor = openIdConnectOptionsMonitor;
        _deadSessionRegistry = deadSessionRegistry;
        _sessionExpiryNotice = sessionExpiryNotice;
        _refreshTokenRevoker = refreshTokenRevoker;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        // Both sign-outs end the session themselves and revoke the stored refresh token (#964, #1027). A
        // refresh here would redeem that token first, and OpenIddict still accepts a redeemed token for a
        // short reuse window, so the revoke would miss the token a copied cookie holds. A failed refresh
        // would even throw the token away. So a sign-out request is not checked at all. A dead-session marker
        // stays where it is: it names this session's access token, which no later sign-in holds (#1061).
        if (IsSignOutRequest(context.HttpContext.Request))
        {
            return;
        }

        CancellationToken cancellationToken = context.HttpContext.RequestAborted;

        // The fallback path: on an earlier request an API refused this cookie's access token, and the call
        // marked that token dead. That response may already have been streaming, so the clean sign-out
        // waits until here, where the response has not started. It runs first, so a session we know is
        // dead never reaches the API again. The marker names this cookie's token, not the user (#1061).
        string? accessToken = context.Properties.GetTokenValue(AccessTokenName);
        if (accessToken is not null
            && await _deadSessionRegistry.ConsumeAsync(accessToken, cancellationToken))
        {
            LogReactiveDeadSession(_logger, null);
            await RejectAsync(context);
            return;
        }

        // A session the renewal cannot manage is dead (#1026). With no expiry it is never renewed or checked
        // again, the API refuses a blank access token on every call, and a blank refresh token cannot renew
        // anything. The first sign-in refuses all three as well. These checks end a session that got past
        // the sign-in, such as one that started before those rules.
        if (!AccessTokenRefreshSchedule.TryGetMoment(
                context.Properties, AccessTokenRefreshSchedule.ExpiresAtName, out DateTimeOffset expiresAt))
        {
            LogNoUsableExpiry(_logger, null);
            await RejectAsync(context);
            return;
        }

        if (!TokenRules.IsUsable(accessToken))
        {
            LogNoStoredAccessToken(_logger, null);
            await RejectAsync(context);
            return;
        }

        // Only a stored but blank refresh token is broken. A missing one ends the session at the first
        // renewal, below.
        string? refreshToken = context.Properties.GetTokenValue(RefreshTokenName);
        if (refreshToken is not null && !TokenRules.IsUsable(refreshToken))
        {
            LogBlankStoredRefreshToken(_logger, null);
            await RejectAsync(context);
            return;
        }

        RefreshOutcome refreshOutcome = await TryRefreshIfNearExpiryAsync(
            context, expiresAt, refreshToken, cancellationToken);

        // Only a token this request left alone is checked below. Each other outcome says why it skips the
        // check, so an outcome added later skips it too unless it is meant to run.
        if (refreshOutcome is not RefreshOutcome.Unchanged)
        {
            return;
        }

        // A check we do ourselves: even when the token is still valid by the local clock, its signing key
        // may have been rotated upstream while the frontend still trusts the old keys. We check the
        // signature against the cached OIDC keys, with no call to the API.
        // Lifetime and audience are deliberately not checked here: the window above handles expiry, and
        // this frontend is not the token's audience.
        if (!await IsAccessTokenCryptographicallyValidAsync(accessToken, cancellationToken))
        {
            LogProactiveInvalidToken(_logger, null);
            await RejectAsync(context);
        }
    }

    /// <returns>
    /// <see cref="RefreshOutcome.Stop"/> when the principal was already rejected.
    /// <see cref="RefreshOutcome.Refreshed"/> when a new token was just fetched, so the caller skips the
    /// signature check. <see cref="RefreshOutcome.Postponed"/> when the refresh failed for a moment and the
    /// session keeps its token, so the caller skips the check too. Otherwise
    /// <see cref="RefreshOutcome.Unchanged"/>, and the token that is still valid should be checked.
    /// </returns>
    private async Task<RefreshOutcome> TryRefreshIfNearExpiryAsync(
        CookieValidatePrincipalContext context,
        DateTimeOffset expiresAt,
        string? refreshToken,
        CancellationToken cancellationToken)
    {
        AuthenticationProperties properties = context.Properties;

        if (!AccessTokenRefreshSchedule.IsDue(properties, expiresAt, _timeProvider.GetUtcNow()))
        {
            return RefreshOutcome.Unchanged;
        }

        if (refreshToken is null)
        {
            LogNoRefreshToken(_logger, null);
            await RejectAsync(context);
            return RefreshOutcome.Stop;
        }

        TokenRefreshResult refreshResult = await _tokenEndpointClient.RefreshAsync(
            refreshToken, cancellationToken);

        // A failure that goes away on its own (a restart, a busy server, the per-address limit that many
        // users behind one address share) does not end a session whose access token still works. Nothing is
        // revoked and the refresh stays due, so the next request tries again (ADR-0067 decision 3, #1062).
        // The clock is read after the call, because a timeout takes 30 seconds.
        if (refreshResult.IsTemporaryFailure && _timeProvider.GetUtcNow() < expiresAt)
        {
            LogRefreshPostponed(_logger, null);
            return RefreshOutcome.Postponed;
        }

        if (refreshResult.Tokens is not { } tokenResponse)
        {
            if (refreshResult.IsTemporaryFailure)
            {
                LogRefreshFailedAfterExpiry(_logger, null);
            }
            else
            {
                LogRefreshFailed(_logger, null);
            }

            // When the browser left during the refresh, the failure says nothing about the token. The browser
            // never gets the deleted cookie and sends the same refresh token on its next request, so that
            // token must stay alive.
            await RejectAsync(context, revokeStoredRefreshToken: !cancellationToken.IsCancellationRequested);
            return RefreshOutcome.Stop;
        }

        if (!TokenRules.IsUsable(tokenResponse.AccessToken))
        {
            LogNoUsableAccessToken(_logger, null);
            await RejectAsync(context, receivedRefreshToken: tokenResponse.RefreshToken);
            return RefreshOutcome.Stop;
        }

        // A renewed token skips the signature check (see ValidateAsync), so its shape is the only check
        // before this page sends it to the API (#1028). Our sign-in server signs access tokens and does not
        // encrypt them, so a good one is always a compact JWS.
        if (!IsCompactJws(tokenResponse.AccessToken))
        {
            LogMalformedAccessToken(_logger, null);
            await RejectAsync(context, receivedRefreshToken: tokenResponse.RefreshToken);
            return RefreshOutcome.Stop;
        }

        if (!TokenRules.IsPositiveLifetime(tokenResponse.ExpiresIn))
        {
            LogNoPositiveLifetime(
                _logger,
                tokenResponse.ExpiresIn?.ToString(CultureInfo.InvariantCulture) ?? "missing",
                null);
            await RejectAsync(context, receivedRefreshToken: tokenResponse.RefreshToken);
            return RefreshOutcome.Stop;
        }

        // A broken refresh or ID token means the answer was damaged on the way, so none of it is trusted
        // (#1028). The refresh token is opaque to us (RFC 6749 §1.5), so only its characters are checked:
        // printable ASCII, as RFC 6749 Appendix A.17 allows.
        if (!string.IsNullOrWhiteSpace(tokenResponse.RefreshToken)
            && tokenResponse.RefreshToken.AsSpan().ContainsAnyExceptInRange(' ', '~'))
        {
            LogMalformedRefreshToken(_logger, null);
            await RejectAsync(context, receivedRefreshToken: tokenResponse.RefreshToken);
            return RefreshOutcome.Stop;
        }

        // Our sign-in server signs ID tokens and never encrypts them, so a good one is always a compact JWS.
        // A broken one stored here would be the sign-out's hint. The sign-in server finds no session behind
        // a broken hint, so this device's session would not end there (#931).
        if (!string.IsNullOrWhiteSpace(tokenResponse.IdToken) && !IsCompactJws(tokenResponse.IdToken))
        {
            LogMalformedIdToken(_logger, null);
            await RejectAsync(context, receivedRefreshToken: tokenResponse.RefreshToken);
            return RefreshOutcome.Stop;
        }

        properties.UpdateTokenValue(AccessTokenName, tokenResponse.AccessToken);

        // An answer with no usable new token keeps the stored one (#974).
        if (TokenRules.IsUsable(tokenResponse.RefreshToken))
        {
            properties.UpdateTokenValue(RefreshTokenName, tokenResponse.RefreshToken);
        }

        if (TokenRules.IsUsable(tokenResponse.IdToken))
        {
            properties.UpdateTokenValue(IdTokenName, tokenResponse.IdToken);
        }

        DateTimeOffset receivedAt = _timeProvider.GetUtcNow();
        DateTimeOffset newExpiresAt = receivedAt.AddSeconds(tokenResponse.ExpiresIn.Value);
        properties.UpdateTokenValue(
            AccessTokenRefreshSchedule.ExpiresAtName,
            newExpiresAt.ToString("o", CultureInfo.InvariantCulture));
        AccessTokenRefreshSchedule.Schedule(properties, receivedAt, newExpiresAt);

        context.ShouldRenew = true;
        return RefreshOutcome.Refreshed;
    }

    private async Task<bool> IsAccessTokenCryptographicallyValidAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Three non-empty base64url parts joined by dots, and nothing else: no padding, no whitespace, no line
    /// break. <see cref="JsonWebTokenHandler.CanReadToken"/> is not enough, because its pattern also accepts a
    /// trailing line feed, and a request header cannot carry one. The header and the payload must also read
    /// as JSON, and the header must name an algorithm (RFC 7515 §4.1.1). The signature is not checked here.
    /// </summary>
    private static bool IsCompactJws(string token)
    {
        string[] parts = token.Split('.', 4);
        if (parts.Length != 3
            || !parts.All(part => part.Length > 0 && !part.AsSpan().ContainsAnyExcept(Base64UrlCharacters)))
        {
            return false;
        }

        try
        {
            return !string.IsNullOrEmpty(new JsonWebToken(token).Alg);
        }
        catch (ArgumentException)
        {
            // The constructor throws when the header or the payload is not base64url-encoded JSON, or when
            // alg is not a string.
            return false;
        }
    }

    /// <summary>
    /// The same requests routing sends to the two sign-out handlers: POST only, so a GET to the same path,
    /// which ends on the 405 page, is still checked like any other request.
    /// </summary>
    private static bool IsSignOutRequest(HttpRequest request) =>
        HttpMethods.IsPost(request.Method)
        && (IsPath(request.Path, AuthenticationDependencyInjectionExtensions.LogoutPath)
            || IsPath(request.Path, AuthenticationDependencyInjectionExtensions.LocalSignOutPath));

    private static bool IsPath(PathString path, string target) =>
        path.StartsWithSegments(target, out PathString remaining) && (!remaining.HasValue || remaining == "/");

    /// <summary>
    /// Deleting the cookie is not enough: the refresh token it carries stays valid at the auth server for
    /// hours, and a copy of the cookie could keep renewing with it (#1027). So the token is revoked too,
    /// within the sign-out's time limit.
    /// </summary>
    /// <param name="receivedRefreshToken">
    /// The refresh token of a renewal answer the website throws away. The auth server has already swapped
    /// the stored token for this one, so both are revoked.
    /// </param>
    /// <param name="revokeStoredRefreshToken">
    /// <see langword="false"/> only when the browser left during the renewal: it never gets the deleted
    /// cookie and sends the stored token again.
    /// </param>
    private async Task RejectAsync(
        CookieValidatePrincipalContext context,
        string? receivedRefreshToken = null,
        bool revokeStoredRefreshToken = true)
    {
        string? storedRefreshToken = revokeStoredRefreshToken
            ? context.Properties.GetTokenValue(RefreshTokenName)
            : null;

        // Set the one-time notice before signing out, so it is written while the response has not
        // started. A user's own /auth/logout signs out directly and not through this path, so it never
        // sets the notice. That is the rule: it is not shown to people who logged out themselves.
        _sessionExpiryNotice.Raise();
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        await _refreshTokenRevoker.RevokeAsync(storedRefreshToken, receivedRefreshToken);
    }

    /// <summary>
    /// The result of the refresh attempt, which tells <see cref="ValidateAsync"/> whether the signature
    /// check still has anything to look at.
    /// </summary>
    private enum RefreshOutcome
    {
        /// <summary>
        /// Validation must stop: the refresh attempt has already rejected the principal.
        /// </summary>
        Stop,

        /// <summary>
        /// The token was not touched and is still valid by the local clock. This is the one case where a
        /// key change upstream is worth checking for.
        /// </summary>
        Unchanged,

        /// <summary>
        /// A new token was fetched from the identity provider over TLS on this very request, so its signing
        /// key cannot have changed since. The signature check would find nothing and could only fail wrongly
        /// against local keys that are a moment out of date. The refresh has already checked the token's
        /// shape instead (#1028).
        /// </summary>
        Refreshed,

        /// <summary>
        /// The refresh failed for a moment while the token still works. The session keeps the token, and the
        /// next request tries the refresh again. The signature check is skipped: it may have to fetch keys
        /// from the sign-in server that has just failed, so a page would fail where the session should
        /// survive. The translation API checks the kept token on every call (ADR-0067 decision 2).
        /// </summary>
        Postponed
    }
}
