namespace LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;

internal interface ITokenEndpointClient
{
    /// <summary>
    /// Pass the request's abort token. A cancel through it means the browser left, which is no fault of the
    /// sign-in server, so it is logged as information (#977).
    /// </summary>
    Task<TokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Best effort (RFC 7009): a failure is logged, never thrown. Pass a time limit, never the request's
    /// abort token: every cancel is logged as a warning, because it means a stuck sign-in server.
    /// </summary>
    Task RevokeRefreshTokenAsync(
        Uri revocationEndpoint,
        string refreshToken,
        CancellationToken cancellationToken = default);
}
