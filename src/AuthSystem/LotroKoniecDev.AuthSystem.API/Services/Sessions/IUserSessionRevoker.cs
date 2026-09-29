namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// Ends sessions by revoking OpenIddict authorizations and their tokens: all sessions of a user, or the
/// one session behind a single authorization.
/// </summary>
/// <remarks>
/// It takes no cancellation token on purpose. A browser that goes away while it runs must not keep a
/// session alive (#872).
/// </remarks>
internal interface IUserSessionRevoker
{
    Task RevokeAllAsync(string userId);

    /// <summary>
    /// Every sign-in through the website gets its own authorization, so one authorization is the session
    /// of one device.
    /// </summary>
    Task RevokeSessionAsync(string authorizationId);
}
