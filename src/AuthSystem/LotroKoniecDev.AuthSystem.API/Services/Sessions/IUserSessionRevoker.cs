namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// No cancellation token: a browser that goes away mid-revoke must not keep a session alive (#872).
/// </summary>
internal interface IUserSessionRevoker
{
    Task RevokeAllAsync(string userId);

    /// <summary>
    /// Every website sign-in gets its own authorization, so this ends one sign-in and its tokens. An
    /// earlier sign-in on the same device has its own authorization and is not ended.
    /// </summary>
    Task RevokeSessionAsync(string authorizationId);
}
