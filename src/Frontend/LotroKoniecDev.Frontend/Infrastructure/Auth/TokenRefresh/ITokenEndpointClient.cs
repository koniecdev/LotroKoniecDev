namespace LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;

internal interface ITokenEndpointClient
{
    /// <summary>
    /// Pass the request's abort token. A cancel through it means the request ended early, mostly because the
    /// browser left with a click on another link. That is no fault of the sign-in server, so it is logged as
    /// information, not as a warning (#977, #1141).
    /// </summary>
    Task<TokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Best effort (RFC 7009): a refusal or a transport failure is logged, never thrown. Pass a time limit,
    /// never the request's abort token: every cancel is logged as a warning, because it means a stuck
    /// sign-in server.
    /// </summary>
    Task RevokeRefreshTokenAsync(
        Uri revocationEndpoint,
        string refreshToken,
        CancellationToken cancellationToken = default);
}
