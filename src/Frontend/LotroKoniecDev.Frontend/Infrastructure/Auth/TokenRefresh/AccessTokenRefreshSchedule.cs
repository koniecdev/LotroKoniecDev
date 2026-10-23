using System.Globalization;
using Microsoft.AspNetCore.Authentication;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;

/// <summary>
/// The access token is refreshed 60 seconds before it runs out, or after half its lifetime when that is
/// later. A fixed 60 seconds would make a short-lived token look "about to run out" on arrival, so every
/// page would redeem the refresh token again (#1025). The refresh time is stored next to
/// <c>expires_at</c>, because the lifetime is known only when a token arrives.
/// </summary>
internal static class AccessTokenRefreshSchedule
{
    internal const string ExpiresAtName = "expires_at";
    internal const string RefreshAtName = "refresh_at";

    /// <summary>
    /// So a call already on its way never arrives with a token the server has just started to refuse.
    /// </summary>
    private static readonly TimeSpan MaximumLead = TimeSpan.FromSeconds(60);

    internal static void Schedule(AuthenticationProperties properties, DateTimeOffset receivedAt, DateTimeOffset expiresAt)
    {
        StoreTokenValue(
            properties,
            RefreshAtName,
            RefreshAt(receivedAt, expiresAt).ToString("o", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A missing, unreadable or later-than-expiry refresh time falls back to 60 seconds before expiry:
    /// trusting it could let the token run out before it is refreshed.
    /// </summary>
    internal static bool IsDue(AuthenticationProperties properties, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        DateTimeOffset refreshAt =
            TryGetMoment(properties, RefreshAtName, out DateTimeOffset storedRefreshAt) && storedRefreshAt <= expiresAt
                ? storedRefreshAt
                : expiresAt - MaximumLead;

        return now >= refreshAt;
    }

    internal static bool TryGetMoment(AuthenticationProperties properties, string tokenName, out DateTimeOffset moment)
    {
        string? raw = properties.GetTokenValue(tokenName);
        if (string.IsNullOrEmpty(raw))
        {
            moment = default;
            return false;
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out moment);
    }

    /// <summary>
    /// A token already dead on arrival gets no lead, so it is refreshed on the next request.
    /// </summary>
    private static DateTimeOffset RefreshAt(DateTimeOffset receivedAt, DateTimeOffset expiresAt)
    {
        TimeSpan halfLifetime = (expiresAt - receivedAt) / 2;
        TimeSpan lead = TimeSpan.FromTicks(Math.Clamp(halfLifetime.Ticks, 0, MaximumLead.Ticks));

        return expiresAt - lead;
    }

    /// <summary>
    /// <see cref="AuthenticationTokenExtensions.UpdateTokenValue"/> changes only a value that is already
    /// stored, and the first refresh time is not.
    /// </summary>
    private static void StoreTokenValue(AuthenticationProperties properties, string tokenName, string tokenValue)
    {
        if (properties.UpdateTokenValue(tokenName, tokenValue))
        {
            return;
        }

        List<AuthenticationToken> tokens = properties.GetTokens().ToList();
        tokens.Add(new AuthenticationToken { Name = tokenName, Value = tokenValue });
        properties.StoreTokens(tokens);
    }
}
