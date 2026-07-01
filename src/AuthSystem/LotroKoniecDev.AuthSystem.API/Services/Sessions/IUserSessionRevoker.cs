namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// Revokes every OpenIddict token and authorization issued to a user, evicting all active sessions.
/// Used when a credential change (password reset or change) must invalidate outstanding access/refresh
/// tokens and consents.
/// </summary>
internal interface IUserSessionRevoker
{
    Task RevokeAllAsync(string userId, CancellationToken cancellationToken = default);
}
