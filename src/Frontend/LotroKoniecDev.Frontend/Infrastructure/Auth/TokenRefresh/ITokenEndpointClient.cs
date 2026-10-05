namespace LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;

internal interface ITokenEndpointClient
{
    Task<TokenResponse?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Best effort (RFC 7009): a refusal or a transport failure is logged, never thrown.
    /// </summary>
    Task RevokeRefreshTokenAsync(
        Uri revocationEndpoint,
        string refreshToken,
        CancellationToken cancellationToken = default);
}
