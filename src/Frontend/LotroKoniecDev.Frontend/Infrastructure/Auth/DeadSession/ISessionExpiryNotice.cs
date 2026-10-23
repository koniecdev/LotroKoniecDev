namespace LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;

/// <summary>
/// The one-time "your session expired" banner. It is set when a dead session is signed out, before the
/// response starts, so a marker cookie can still be written. It never shows after the user's own sign-out.
/// </summary>
internal interface ISessionExpiryNotice
{
    void Raise();

    /// <summary>Clears the marker, so the banner does not appear again on the next page.</summary>
    bool Consume();
}
