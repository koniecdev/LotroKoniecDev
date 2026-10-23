namespace LotroKoniecDev.SharedKernel.Constants;

/// <summary>
/// A username is only a display handle; the login identifier is the e-mail (ADR-0022). Leaving '@'
/// out of the allowed characters keeps the two apart, because
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
