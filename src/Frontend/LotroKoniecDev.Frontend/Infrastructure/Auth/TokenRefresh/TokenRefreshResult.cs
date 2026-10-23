namespace LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;

/// <summary>
/// The two kinds of failure ADR-0067 decision 3 sets: the session survives a temporary one while its access
/// token still works, and a final one ends it.
/// </summary>
internal sealed class TokenRefreshResult
{
    /// <summary>A 429, any 5xx, a network error, a timeout, or a request that ended early.</summary>
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
