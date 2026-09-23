namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// The inbox an address reaches, as far as a send budget can tell (ADR-0057). Mail providers deliver
/// many spellings to one inbox: a <c>+tag</c> after the name, and at Gmail also dots in the name and the
/// googlemail.com domain. Every mail budget keys on this value, so all of those spellings share one
/// budget.
/// </summary>
/// <remarks>
/// The key only counts sends. The mail still goes to the address as the user typed it.
/// </remarks>
internal sealed record MailboxKey
{
    private const string GmailDomain = "GMAIL.COM";
    private const string GoogleMailDomain = "GOOGLEMAIL.COM";

    public string Value { get; }

    /// <summary>
    /// Builds the key from the address as Identity normalized it: <c>ApplicationUser.NormalizedEmail</c>
    /// where an account exists, <c>UserManager.NormalizeEmail</c> where none does. Starting from Identity's
    /// form keeps every spelling Identity treats as one account inside one budget.
    /// </summary>
    public static MailboxKey FromNormalizedEmail(string? normalizedEmail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedEmail);

        // Identity's output is already upper-case, so this changes nothing there. It only keeps the Gmail
        // rule below blind to letter case.
        string address = normalizedEmail.ToUpperInvariant();

        int at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1)
        {
            return new MailboxKey(address);
        }

        string localPart = address[..at];
        string domain = address[(at + 1)..];

        // The search starts after the first character: a '+' there has no mailbox name in front of it.
        int tagStart = localPart.IndexOf('+', 1);
        if (tagStart > 0)
        {
            localPart = localPart[..tagStart];
        }

        if (domain is GmailDomain or GoogleMailDomain)
        {
            localPart = localPart.Replace(".", string.Empty, StringComparison.Ordinal);
            domain = GmailDomain;
        }

        return new MailboxKey($"{localPart}@{domain}");
    }

    private MailboxKey(string value)
    {
        Value = value;
    }
}
