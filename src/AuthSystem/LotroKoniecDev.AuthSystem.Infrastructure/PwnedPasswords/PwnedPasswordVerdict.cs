namespace LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

public enum PwnedPasswordVerdict
{
    NotFound,
    Breached,

    /// <summary>
    /// The service did not answer, answered too slowly or answered with an error, so nothing is known
    /// about the password. What happens next is the caller's policy, not this adapter's.
    /// </summary>
    Unavailable
}
