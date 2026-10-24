namespace LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

public enum PwnedPasswordVerdict
{
    NotFound,
    Breached,

    /// <summary>
    /// No answer, a slow answer or an error, so nothing is known about the password. What happens next
    /// is the caller's policy.
    /// </summary>
    Unavailable
}
