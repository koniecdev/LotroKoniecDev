namespace LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;

/// <summary>
/// One-shot "your session expired" banner state. It is raised at the moment a dead session is
/// signed out (where the response has not started, so a marker cookie can still be written) and is
/// consumed exactly once on the next render — so the soft notice appears a single time after a
/// forced logout and never after a deliberate <c>/auth/logout</c>.
/// </summary>
internal interface ISessionExpiryNotice
{
    /// <summary>Raises the one-shot notice by writing the short-lived marker cookie.</summary>
    void Raise();

    /// <summary>
    /// Returns <see langword="true"/> once if the notice was raised, clearing the marker so the
    /// banner is not shown again on the next navigation.
    /// </summary>
    bool Consume();
}
