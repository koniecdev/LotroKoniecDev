namespace LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;

/// <summary>
/// The state behind the one-time "your session expired" banner. It is set when a dead session is
/// signed out, at a point where the response has not started yet so a marker cookie can still be
/// written, and it is read once on the next render. So the notice appears exactly once after a forced
/// logout and never after a user's own <c>/auth/logout</c>.
/// </summary>
internal interface ISessionExpiryNotice
{
    /// <summary>Sets the notice by writing the short-lived marker cookie.</summary>
    void Raise();

    /// <summary>
    /// Returns <see langword="true"/> once when the notice was set, and clears the marker so the banner
    /// does not appear again on the next page.
    /// </summary>
    bool Consume();
}
