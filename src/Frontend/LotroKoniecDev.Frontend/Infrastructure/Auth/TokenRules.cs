using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

/// <summary>
/// One set of rules for the tokens the sign-in server sends, used by the first sign-in and by the
/// background renewal alike. The two paths used to check on their own, and the first sign-in fell behind
/// (#974, #1026). Our own sign-in server always sends a usable access token and a positive lifetime, so an
/// answer without them means something between us and that server is broken.
/// </summary>
internal static class TokenRules
{
    /// <summary>A blank token counts as no token, like an empty one.</summary>
    public static bool IsUsable([NotNullWhen(true)] string? token) => !string.IsNullOrWhiteSpace(token);

    /// <summary>
    /// A token with a lifetime of zero or less looks expired at once, so every page would renew it again.
    /// </summary>
    public static bool IsPositiveLifetime([NotNullWhen(true)] int? expiresInSeconds) => expiresInSeconds > 0;

    /// <summary>
    /// Reads <c>expires_in</c> exactly the way the OIDC handler does before it stores <c>expires_at</c>. A
    /// value the handler cannot read leaves the session with no expiry, so here it is a missing lifetime.
    /// </summary>
    public static int? ParseLifetime(string? expiresIn) =>
        int.TryParse(expiresIn, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
            ? seconds
            : null;
}
