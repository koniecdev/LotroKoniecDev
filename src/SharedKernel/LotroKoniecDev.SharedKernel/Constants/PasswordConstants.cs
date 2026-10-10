namespace LotroKoniecDev.SharedKernel.Constants;

/// <summary>
/// The password length rule. The Identity options, the Identity validator for the maximum and the API's
/// FluentValidation rules all read it, so every path that sets a password keeps the same limits (#1046).
/// </summary>
public static class PasswordConstants
{
    public const int MinLength = 8;
    public const int MaxLength = 128;
}
