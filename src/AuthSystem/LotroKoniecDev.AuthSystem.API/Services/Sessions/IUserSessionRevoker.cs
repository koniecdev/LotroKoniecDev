namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// Revokes every OpenIddict token and authorization a user has, which ends all their sessions.
/// </summary>
/// <remarks>
/// It takes no cancellation token on purpose. A browser that goes away while it runs must not keep the
/// other devices signed in (#872).
/// </remarks>
internal interface IUserSessionRevoker
{
    Task RevokeAllAsync(string userId);
}
