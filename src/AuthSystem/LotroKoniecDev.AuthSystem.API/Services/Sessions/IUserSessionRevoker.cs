namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// Revokes every OpenIddict token and authorization a user has, which ends all their sessions. It is
/// used when a password reset or change has to invalidate the access and refresh tokens and the
/// consents they already hold.
/// </summary>
internal interface IUserSessionRevoker
{
    Task RevokeAllAsync(string userId, CancellationToken cancellationToken = default);
}
