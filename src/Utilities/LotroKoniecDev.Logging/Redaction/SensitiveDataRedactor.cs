using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace LotroKoniecDev.Logging.Redaction;

/// <summary>
/// Removes secrets and personal data from request-log fields before they reach a sink (audit #0001 /
/// M5). The values of OAuth, OIDC and credential query parameters (<c>code</c>, <c>access_token</c>,
/// <c>password</c> and friends) are replaced whole, and an e-mail address left in any other value has
/// its local part masked. So neither a token someone could replay nor personal data is written in
/// clear text.
/// No method here throws, because a logging path must never fail. They work on the raw query string
/// without decoding it, so the separator is found both as a plain <c>@</c> and as the <c>%40</c> that
/// <c>Uri.EscapeDataString</c> produces (ADR-0046).
/// An address escaped twice is not covered: <c>%2540</c>, as it would appear inside a
/// <c>returnUrl</c> that carries a link that itself carries an address, matches neither form and
/// stays unmasked. No such link exists today, and the fix would be one more alternative here, not a
/// decode step on a hot path.
/// </summary>
public static partial class SensitiveDataRedactor
{
    private const string RedactedValue = "***";

    private const string PercentEncodedAt = "%40";

    /// <summary>
    /// Query-parameter names whose value is a secret and must never be logged. The name is decoded
    /// first and matched without case. The list is exact, so any new parameter that carries a
    /// credential (a new OAuth or OIDC grant, hint or assertion) must be added here. If it is not,
    /// its value is logged in clear text.
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
    /// Cleans a raw query string, with or without its leading <c>?</c>. Sensitive values become
    /// <c>***</c> and e-mail addresses in the other values are masked. The result keeps the leading
    /// <c>?</c>, or is empty when there is no query.
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
