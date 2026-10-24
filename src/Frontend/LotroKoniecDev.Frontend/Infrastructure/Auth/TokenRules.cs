using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

/// <summary>
/// One set of rules for the first sign-in and the renewal alike (#1026). They only ask whether the website
/// can use the answer: a token is there, with a lifetime the renewal schedule needs. They never check what
/// a token holds (ADR-0067 decision 2).
/// </summary>
internal static class TokenRules
{
    public static bool IsUsable([NotNullWhen(true)] string? token) => !string.IsNullOrWhiteSpace(token);

    /// <summary>
    /// A token with a lifetime of zero or less looks expired at once, so every page would renew it again.
    /// </summary>
    public static bool IsPositiveLifetime([NotNullWhen(true)] int? expiresInSeconds) => expiresInSeconds is > 0;

    /// <summary>
    /// Reads <c>expires_in</c> the way the OIDC handler does before it stores <c>expires_at</c>.
    /// </summary>
    public static int? ParseLifetime(string? expiresIn) =>
        int.TryParse(expiresIn, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
            ? seconds
            : null;
}
