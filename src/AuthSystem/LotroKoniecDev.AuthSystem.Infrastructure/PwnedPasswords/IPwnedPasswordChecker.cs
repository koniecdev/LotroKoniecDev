namespace LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

/// <summary>
/// Asks the free Pwned Passwords range API of Have I Been Pwned whether a password is in a known data
/// breach. The password never leaves the process: only the first five characters of its SHA-1 hash are
/// sent, and the match against the answer happens here (k-anonymity, ADR-0065).
/// </summary>
public interface IPwnedPasswordChecker
{
    Task<PwnedPasswordVerdict> CheckAsync(string password, CancellationToken cancellationToken);
}
