using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace LotroKoniecDev.Logging.Redaction;

/// <summary>
/// Keeps replayable tokens and personal data out of request logs (audit 0001). Credential query values
/// are replaced whole, and an e-mail in any other value has its local part masked. No method throws,
/// because a logging path must never fail.
/// <para>
/// It works on the raw query string, so it finds the separator both as <c>@</c> and as the <c>%40</c>
/// that <c>Uri.EscapeDataString</c> writes (ADR-0046 §3). An address escaped twice (<c>%2540</c>, as in
/// a <c>returnUrl</c> that carries a link with an address) stays unmasked. No such link exists today,
/// and the fix would be one more alternative in the regex, not a decode step on a hot path.
/// </para>
/// </summary>
public static partial class SensitiveDataRedactor
{
    private const string RedactedValue = "***";

    private const string PercentEncodedAt = "%40";

    /// <summary>
    /// Matched without case on the decoded name. The list is exact: a new parameter that carries a
    /// credential (a new OAuth or OIDC grant, hint or assertion) must be added here, or its value is
    /// logged in clear text.
    /// </summary>
    private static readonly FrozenSet<string> SensitiveQueryKeys = new[]
    {
        "code",
        "token",
        "access_token",
        "refresh_token",
        "id_token",
        "id_token_hint",
        "client_assertion",
        "password",
        "pwd",
        "secret",
        "client_secret"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Takes the query with or without its leading <c>?</c> and keeps that form in the result. Returns
    /// an empty string when there is no query.
    /// </summary>
    public static string RedactQueryString(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString))
        {
            return string.Empty;
        }

        bool hasLeadingQuestionMark = queryString[0] == '?';
        string body = hasLeadingQuestionMark ? queryString[1..] : queryString;

        if (body.Length == 0)
        {
            return string.Empty;
        }

        string[] pairs = body.Split('&');
        for (int i = 0; i < pairs.Length; i++)
        {
            pairs[i] = RedactPair(pairs[i]);
        }

        string redactedBody = string.Join('&', pairs);
        return hasLeadingQuestionMark ? "?" + redactedBody : redactedBody;
    }

    /// <summary>
    /// Masks the local part of an e-mail and keeps only its first character, so
    /// <c>alice@example.com</c> becomes <c>a***@example.com</c>. A value without a separator or with
    /// an empty local part is treated as fully sensitive and replaced with <c>***</c>.
    /// </summary>
    public static string MaskEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RedactedValue;
        }

        int atIndex = value.IndexOf('@', StringComparison.Ordinal);
        if (atIndex < 0)
        {
            atIndex = value.IndexOf(PercentEncodedAt, StringComparison.Ordinal);
        }

        return atIndex <= 0 ? RedactedValue : string.Concat(value[0].ToString(), RedactedValue, value[atIndex..]);
    }

    private static string RedactPair(string pair)
    {
        int separatorIndex = pair.IndexOf('=', StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return pair;
        }

        string key = pair[..separatorIndex];

        // Match the decoded key, so an encoded spelling like "%63ode" for "code" cannot slip a secret
        // past the list. The output still keeps the original key text.
        if (SensitiveQueryKeys.Contains(Uri.UnescapeDataString(key)))
        {
            return key + "=" + RedactedValue;
        }

        string value = pair[(separatorIndex + 1)..];
        return key + "=" + MaskEmailsIn(value);
    }

    private static string MaskEmailsIn(string value)
    {
        bool mayHoldEmail = value.Contains('@', StringComparison.Ordinal)
            || value.Contains(PercentEncodedAt, StringComparison.Ordinal);
        if (!mayHoldEmail)
        {
            return value;
        }

        return EmailRegex().Replace(value, match => MaskEmail(match.Value));
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+(?:@|%40)[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();
}
