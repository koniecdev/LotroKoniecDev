namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// Revokes every OpenIddict token and authorization a user has, which ends all their sessions. It is
/// used when a password reset or change, an e-mail change, a scheduled deletion or a sign-out has to
/// invalidate the access and refresh tokens and the consents they already hold.
/// </summary>
/// <remarks>
/// It takes no cancellation token on purpose. A caller runs it right after a committed save or while it
/// signs the user out, and a browser that goes away at that moment must not keep the other devices
/// signed in (#872).
/// </remarks>
internal interface IUserSessionRevoker
{
    Task RevokeAllAsync(string userId);
}
