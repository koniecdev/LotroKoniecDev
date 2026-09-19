namespace LotroKoniecDev.Frontend.Infrastructure.Formatting;

/// <summary>
/// Masks an address before it goes into a log line, so an audit trail can name the account without
/// storing the address itself (#690).
/// The auth API masks the same way in its own <c>EmailMaskingExtensions</c>. The two contexts share no
/// code, so the rule is written twice on purpose — keep them in step by hand.
/// </summary>
internal static class EmailMasking
{
    internal const string Unknown = "***";

    extension(string? email)
    {
        public string MaskEmail()
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return Unknown;
            }

            int atIndex = email.IndexOf('@', StringComparison.Ordinal);
            return atIndex <= 0 ? Unknown : string.Concat(email[0].ToString(), Unknown, email[atIndex..]);
        }
    }
}
