namespace LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;

internal interface ITokenEndpointClient
{
    Task<TokenResponse?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);
}
