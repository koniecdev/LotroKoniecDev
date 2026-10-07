using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// The words a refusal warning uses for the step and for the token it carried. The code exchange and the
/// refresh share one set of warnings (#977), so each warning takes these names.
/// </summary>
internal sealed record TokenGrantName(string Step, string Token)
{
    public static TokenGrantName CodeExchange { get; } = new("Code exchange", "authorization code");

    public static TokenGrantName Refresh { get; } = new("Refresh", "refresh token");

    /// <summary>
    /// Null for every other token type: only a code or a refresh token belongs to a user's sign-in.
    /// </summary>
    public static TokenGrantName? ForTokenType(string? tokenType) => tokenType switch
    {
        TokenTypeIdentifiers.Private.AuthorizationCode => CodeExchange,
        TokenTypeIdentifiers.RefreshToken => Refresh,
        _ => null
    };
}
