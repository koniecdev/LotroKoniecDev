using System.Globalization;
using Microsoft.AspNetCore.Authentication;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;

/// <summary>
/// Decides when the website refreshes the access token: 60 seconds before it runs out, or after half of
/// its lifetime when that comes later. A fixed 60 seconds would make a token that lives a minute or less
/// look "about to run out" the moment it arrives, so every page would redeem the refresh token again
/// (#1025). The lifetime is known only when a token arrives, so the refresh time is worked out then and
/// stored in the cookie next to <c>expires_at</c>.
/// </summary>
internal static class AccessTokenRefreshSchedule
{
    internal const string ExpiresAtName = "expires_at";
    internal const string RefreshAtName = "refresh_at";

    /// <summary>
    /// Refresh a little before the token really expires, so a call already on its way cannot arrive with
    /// a token that still looks valid here but is already refused by the server.
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
    /// A cookie with no readable refresh time was written before #1025. A refresh time later than the
    /// expiry belongs to an older token, for example one that an older version of the website refreshed
    /// without moving the refresh time. Both keep the old rule, 60 seconds before expiry, so a token is
    /// never left to run out before it is refreshed.
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
    /// A token that was already dead when it arrived gets no lead at all, so it is refreshed on the next
    /// request and never later than its own expiry.
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
