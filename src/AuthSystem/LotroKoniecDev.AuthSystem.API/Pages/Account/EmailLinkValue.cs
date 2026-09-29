using System.Text.RegularExpressions;
using LotroKoniecDev.SharedKernel.Constants;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

/// <summary>
/// Shape check for an e-mail address that arrived in a link's query string, before a page prints it
/// or acts on it.
/// </summary>
internal static partial class EmailLinkValue
{
    public static bool LooksLikeAnAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= EmailConstants.MaxLength
        && AddressRegex().IsMatch(value);

    [GeneratedRegex(EmailConstants.RegexPattern, RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex AddressRegex();
}
