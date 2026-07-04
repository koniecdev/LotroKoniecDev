namespace LotroKoniecDev.SharedKernel.Constants;

/// <summary>
/// The username charset rule (ADR-0022): ASCII letters + digits only. The username is a
/// display-only handle — unique, spaceless, special-char-free — while the login identifier is
/// the e-mail. Excluding '@' keeps the two identifier spaces provably disjoint
/// (<see cref="EmailConstants.RegexPattern"/> requires '@').
/// </summary>
public static class UsernameConstants
{
    public const int MaxLength = 150;
    public const string AllowedCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    // \A…\z, not ^…$ — in .NET, $ also matches before a trailing \n, which would let
    // "kasia92\n" through the validator (same anchoring style as EmailConstants).
    public const string RegexPattern = @"\A[a-zA-Z0-9]+\z";
}
