namespace LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;

/// <summary>
/// The answer to a refresh grant, in the two kinds of failure ADR-0067 decision 3 sets (#1062). A
/// temporary failure goes away on its own, so the session survives it while its access token still works.
/// A final failure ends the session.
/// </summary>
internal sealed class TokenRefreshResult
{
    /// <summary>A 429, any 5xx, a network error or a timeout.</summary>
    public static TokenRefreshResult TemporaryFailure { get; } = new(tokens: null, isTemporaryFailure: true);

    /// <summary>Any other refusal, or a success answer whose body cannot be read.</summary>
    public static TokenRefreshResult FinalFailure { get; } = new(tokens: null, isTemporaryFailure: false);

    /// <summary><see langword="null"/> for both kinds of failure.</summary>
    public TokenResponse? Tokens { get; }

    public bool IsTemporaryFailure { get; }

    public static TokenRefreshResult Renewed(TokenResponse tokens) => new(tokens, isTemporaryFailure: false);

    private TokenRefreshResult(TokenResponse? tokens, bool isTemporaryFailure)
    {
        Tokens = tokens;
        IsTemporaryFailure = isTemporaryFailure;
    }
}
