namespace LotroKoniecDev.SharedKernel.Constants;

/// <summary>
/// Which characters a username may hold (ADR-0022): ASCII letters and digits only. The username is
/// just a display handle. It is unique and has no spaces or special characters, while the login
/// identifier is the e-mail. Leaving '@' out keeps the two sets of identifiers apart, because
/// <see cref="EmailConstants.RegexPattern"/> requires an '@'.
/// </summary>
public static class UsernameConstants
{
    public const int MaxLength = 150;
    public const string AllowedCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    // \A and \z, not ^ and $. In .NET, $ also matches before a trailing \n, so "kasia92\n" would
    // pass the validator. EmailConstants anchors the same way.
    public const string RegexPattern = @"\A[a-zA-Z0-9]+\z";
}
