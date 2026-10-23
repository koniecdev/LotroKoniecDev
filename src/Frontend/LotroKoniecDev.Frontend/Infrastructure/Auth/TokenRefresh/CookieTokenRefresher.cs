using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Frontend.Infrastructure.Auth.SignOut;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;

/// <summary>
/// Runs on every cookie validation. It ends a session an API marked dead or one the renewal cannot manage,
/// and otherwise renews the access token at the time <see cref="AccessTokenRefreshSchedule"/> sets. It
/// never checks the access token itself: the translation API checks every token it receives (ADR-0067
/// decision 2). A renewal that fails for a moment keeps a session whose access token still works (ADR-0067
/// decision 3). Every rejection sets the one-time "session expired" notice and revokes the refresh token
/// (#1027).
/// </summary>
internal sealed partial class CookieTokenRefresher
{
    private const string AccessTokenName = "access_token";
    private const string RefreshTokenName = "refresh_token";
    private const string IdTokenName = "id_token";

    private readonly ITokenEndpointClient _tokenEndpointClient;
    private readonly IDeadSessionRegistry _deadSessionRegistry;
    private readonly ISessionExpiryNotice _sessionExpiryNotice;
    private readonly RefreshTokenRevoker _refreshTokenRevoker;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CookieTokenRefresher> _logger;

    public CookieTokenRefresher(
        ITokenEndpointClient tokenEndpointClient,
        IDeadSessionRegistry deadSessionRegistry,
        ISessionExpiryNotice sessionExpiryNotice,
        RefreshTokenRevoker refreshTokenRevoker,
        TimeProvider timeProvider,
        ILogger<CookieTokenRefresher> logger)
    {
        _tokenEndpointClient = tokenEndpointClient;
        _deadSessionRegistry = deadSessionRegistry;
        _sessionExpiryNotice = sessionExpiryNotice;
        _refreshTokenRevoker = refreshTokenRevoker;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        // Both sign-outs revoke the refresh token themselves (#1027). A refresh here would redeem it first,
        // and OpenIddict accepts a redeemed token for a short reuse window, so the revoke would miss the
        // token a copied cookie holds. No "session expired" notice is set either. A dead-session marker
        // stays: it names this session's access token, which no later sign-in holds (#1061).
        if (IsSignOutRequest(context.HttpContext.Request))
        {
            return;
        }

        CancellationToken cancellationToken = context.HttpContext.RequestAborted;

        // An API refused this token on an earlier request (ADR-0067 decision 2). That response may already
        // have been streaming, so the clean sign-out happens here, before this response starts.
        string? accessToken = context.Properties.GetTokenValue(AccessTokenName);
        if (accessToken is not null
            && await _deadSessionRegistry.ConsumeAsync(accessToken, cancellationToken))
        {
            LogReactiveDeadSession(_logger);
            await RejectAsync(context);
            return;
        }

        // A session the renewal cannot manage is dead (#1026): no expiry, a blank access token or a blank
        // refresh token. The first sign-in refuses all three as well.
        if (!AccessTokenRefreshSchedule.TryGetMoment(
                context.Properties, AccessTokenRefreshSchedule.ExpiresAtName, out DateTimeOffset expiresAt))
        {
            LogNoUsableExpiry(_logger);
            await RejectAsync(context);
            return;
        }

        if (!TokenRules.IsUsable(accessToken))
        {
            LogNoStoredAccessToken(_logger);
            await RejectAsync(context);
            return;
        }

        // Only a stored but blank refresh token is broken. A missing one ends the session at the first
        // renewal, below.
        string? refreshToken = context.Properties.GetTokenValue(RefreshTokenName);
        if (refreshToken is not null && !TokenRules.IsUsable(refreshToken))
        {
            LogBlankStoredRefreshToken(_logger);
            await RejectAsync(context);
            return;
        }

        await TryRefreshIfNearExpiryAsync(context, expiresAt, refreshToken, cancellationToken);
    }

    private async Task TryRefreshIfNearExpiryAsync(
        CookieValidatePrincipalContext context,
        DateTimeOffset expiresAt,
        string? refreshToken,
        CancellationToken cancellationToken)
    {
        AuthenticationProperties properties = context.Properties;

        if (!AccessTokenRefreshSchedule.IsDue(properties, expiresAt, _timeProvider.GetUtcNow()))
        {
            return;
        }

        if (refreshToken is null)
        {
            LogNoRefreshToken(_logger);
            await RejectAsync(context);
            return;
        }

        TokenRefreshResult refreshResult = await _tokenEndpointClient.RefreshAsync(
            refreshToken, cancellationToken);

        // A temporary failure (429, 5xx, network, timeout) keeps a session whose access token still works,
        // and the next request tries again (ADR-0067 decision 3). The clock is read after the call, because a
        // timeout takes 30 seconds.
        if (refreshResult.IsTemporaryFailure && _timeProvider.GetUtcNow() < expiresAt)
        {
            LogRefreshPostponed(_logger);
            return;
        }

        if (refreshResult.Tokens is not { } tokenResponse)
        {
            if (refreshResult.IsTemporaryFailure)
            {
                LogRefreshFailedAfterExpiry(_logger);
            }
            else
            {
                LogRefreshFailed(_logger);
            }

            // A browser that left during the refresh never gets the deleted cookie and sends the same
            // refresh token again, so that token must stay alive.
            await RejectAsync(context, revokeStoredRefreshToken: !cancellationToken.IsCancellationRequested);
            return;
        }

        // Only what is needed to use the answer is read: a token and a lifetime. No token is validated; the
        // ID token is kept only as the sign-out's hint (ADR-0067 decision 2).
        if (!TokenRules.IsUsable(tokenResponse.AccessToken))
        {
            LogNoUsableAccessToken(_logger);
            await RejectAsync(context, receivedRefreshToken: tokenResponse.RefreshToken);
            return;
        }

        if (!TokenRules.IsPositiveLifetime(tokenResponse.ExpiresIn))
        {
            LogNoPositiveLifetime(
                _logger, tokenResponse.ExpiresIn?.ToString(CultureInfo.InvariantCulture) ?? "missing");
            await RejectAsync(context, receivedRefreshToken: tokenResponse.RefreshToken);
            return;
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
    }

    /// <summary>
    /// POST only, like the two sign-out routes, so a GET to the same path (the 405 page) is still checked.
    /// </summary>
    private static bool IsSignOutRequest(HttpRequest request) =>
        HttpMethods.IsPost(request.Method)
        && (IsPath(request.Path, AuthenticationDependencyInjectionExtensions.LogoutPath)
            || IsPath(request.Path, AuthenticationDependencyInjectionExtensions.LocalSignOutPath));

    private static bool IsPath(PathString path, string target) =>
        path.StartsWithSegments(target, out PathString remaining) && (!remaining.HasValue || remaining == "/");

    /// <summary>
    /// Deleting the cookie is not enough: its refresh token stays valid for hours, and a copy of the cookie
    /// could keep renewing with it, so the token is revoked too (#1027).
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

        // Before the sign-out, so the notice is written while the response has not started.
        _sessionExpiryNotice.Raise();
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        await _refreshTokenRevoker.RevokeAsync(storedRefreshToken, receivedRefreshToken);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Cookie has no refresh_token; principal rejected.")]
    private static partial void LogNoRefreshToken(ILogger logger);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Refresh token grant returned no usable answer; principal rejected.")]
    private static partial void LogRefreshFailed(ILogger logger);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Session was marked dead by a prior 401; principal rejected.")]
    private static partial void LogReactiveDeadSession(ILogger logger);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "Refresh token grant returned a missing, empty or blank access_token; principal rejected.")]
    private static partial void LogNoUsableAccessToken(ILogger logger);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "Refresh token grant returned a missing or non-positive expires_in; principal rejected. expires_in: {ExpiresIn}")]
    private static partial void LogNoPositiveLifetime(ILogger logger, string expiresIn);

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Warning,
        Message = "Cookie has a missing or unreadable expires_at; principal rejected.")]
    private static partial void LogNoUsableExpiry(ILogger logger);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Warning,
        Message = "Cookie has a missing, empty or blank access_token; principal rejected.")]
    private static partial void LogNoStoredAccessToken(ILogger logger);

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Warning,
        Message = "Cookie has an empty or blank refresh_token; principal rejected.")]
    private static partial void LogBlankStoredRefreshToken(ILogger logger);

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Information,
        Message = "Refresh token grant failed for a moment; the session keeps its access token and the next request tries again.")]
    private static partial void LogRefreshPostponed(ILogger logger);

    [LoggerMessage(
        EventId = 14,
        Level = LogLevel.Information,
        Message = "Refresh token grant failed for a moment after the access token ran out; principal rejected.")]
    private static partial void LogRefreshFailedAfterExpiry(ILogger logger);
}
