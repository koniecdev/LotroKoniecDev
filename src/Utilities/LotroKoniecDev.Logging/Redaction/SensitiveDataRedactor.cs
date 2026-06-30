using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace LotroKoniecDev.Logging.Redaction;

/// <summary>
/// Scrubs secrets and personal data out of request-log fields before they reach a sink (audit #0001 /
/// M5). Values of OAuth/OIDC and credential query parameters (e.g. <c>code</c>, <c>access_token</c>,
/// <c>password</c>) are replaced wholesale, and any e-mail address surviving in a non-sensitive value
/// has its local part masked, so neither a replayable token nor a piece of PII is persisted in plain
/// text. All methods are total — a logging hot path must never throw — and operate on the raw query
/// string without decoding, which is sufficient because query e-mails carry a literal <c>@</c>.
/// </summary>
public static partial class SensitiveDataRedactor
{
    private const string RedactedValue = "***";

    /// <summary>
    /// Query-parameter names whose value is a secret and must never be logged. Matched
    /// case-insensitively against the percent-decoded parameter name. This is an exact allowlist:
    /// any new credential-bearing query parameter (a new OAuth/OIDC grant, hint, or assertion) MUST
    /// be added here, or its value will be logged in clear.
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
    /// Redacts a raw query string (with or without its leading <c>?</c>). Sensitive parameter values
    /// become <c>***</c>; e-mail addresses in the remaining values are masked. Returns the redacted
    /// query string keeping its leading <c>?</c>, or an empty string when there is no query.
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
    /// Masks the local part of an e-mail, keeping only its first character (e.g.
    /// <c>alice@example.com</c> becomes <c>a***@example.com</c>). Anything without a non-empty local
    /// part and an <c>@</c> is treated as fully sensitive and replaced with <c>***</c>.
    /// </summary>
    public static string MaskEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RedactedValue;
        }

        int atIndex = value.IndexOf('@', StringComparison.Ordinal);
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

        // Match against the percent-decoded key so an encoded variant (e.g. "%63ode" = "code") cannot
        // slip a secret past the allowlist; the original key text is kept verbatim in the output.
        if (SensitiveQueryKeys.Contains(Uri.UnescapeDataString(key)))
        {
            return key + "=" + RedactedValue;
        }

        string value = pair[(separatorIndex + 1)..];
        return key + "=" + MaskEmailsIn(value);
    }

    private static string MaskEmailsIn(string value)
    {
        if (value.Length == 0 || !value.Contains('@', StringComparison.Ordinal))
        {
            return value;
        }

        return EmailRegex().Replace(value, match => MaskEmail(match.Value));
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();
}
