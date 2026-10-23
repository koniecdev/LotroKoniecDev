using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// The words a refusal log line uses, because the code exchange and the refresh share one set of log
/// lines (#977).
/// </summary>
internal sealed record TokenGrantName(string Step, string Token, string TokenType)
{
    public static TokenGrantName CodeExchange { get; } =
        new("Code exchange", "authorization code", TokenTypeIdentifiers.Private.AuthorizationCode);

    public static TokenGrantName Refresh { get; } =
        new("Refresh", "refresh token", TokenTypeIdentifiers.RefreshToken);

    /// <summary>
    /// Null for every other token type.
    /// </summary>
    public static TokenGrantName? ForTokenType(string? tokenType) => tokenType switch
    {
        TokenTypeIdentifiers.Private.AuthorizationCode => CodeExchange,
        TokenTypeIdentifiers.RefreshToken => Refresh,
        _ => null
    };
}
