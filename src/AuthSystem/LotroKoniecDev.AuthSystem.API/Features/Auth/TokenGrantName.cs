using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// The words a refusal log line uses for the step and for the token it carried. The code exchange and the
/// refresh share one set of log lines (#977), so each line takes these names.
/// </summary>
internal sealed record TokenGrantName(string Step, string Token, string TokenType)
{
    public static TokenGrantName CodeExchange { get; } =
        new("Code exchange", "authorization code", TokenTypeIdentifiers.Private.AuthorizationCode);

    public static TokenGrantName Refresh { get; } =
        new("Refresh", "refresh token", TokenTypeIdentifiers.RefreshToken);

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
