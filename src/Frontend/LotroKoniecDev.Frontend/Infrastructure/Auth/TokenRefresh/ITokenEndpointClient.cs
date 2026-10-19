namespace LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;

internal interface ITokenEndpointClient
{
    /// <summary>
    /// Pass the request's abort token. A cancel through it is logged as a browser that left, not as a
    /// warning (#1141).
    /// </summary>
    Task<TokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Best effort (RFC 7009): a refusal or a transport failure is logged, never thrown. Pass a time limit,
    /// never the request's abort token: a cancel is logged as a stuck sign-in server.
    /// </summary>
    Task RevokeRefreshTokenAsync(
        Uri revocationEndpoint,
        string refreshToken,
        CancellationToken cancellationToken = default);
}
