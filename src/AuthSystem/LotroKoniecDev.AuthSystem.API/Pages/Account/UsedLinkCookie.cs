using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

/// <summary>
/// Remembers, in the browser that used it, that a one-time link already did its job. Back from a success
/// page loads the link's form again, and its button would send the used link, which the server rightly
/// calls dead (#941). The cookie holds only hashes of the last few used tokens, and it changes only the
/// answer to the browser that sends it, so no page has to look an account up to tell "done" from "dead"
/// (ADR-0063).
/// </summary>
internal sealed class UsedLinkCookie
{
    public static readonly UsedLinkCookie EmailChangeConfirm = new(".lotrokoniecdev.used-link.email-change");

    public static readonly UsedLinkCookie PasswordReset = new(".lotrokoniecdev.used-link.password-reset");

    /// <summary>
    /// Long enough for the Back button after the success page, and the lifetime the privacy policy gives.
    /// </summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A second change in the same half hour must not make the first link's page call it dead again.
    /// </summary>
    private const int RememberedLinks = 5;

    /// <summary>
    /// Not a base64url character, so it can never appear inside a hash.
    /// </summary>
    private const char Separator = '.';

    private UsedLinkCookie(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public void Remember(HttpContext httpContext, string token)
    {
        string hash = HashOf(token);
        IEnumerable<string> earlierHashes = HashesIn(httpContext.Request)
            .Where(earlierHash => !string.Equals(earlierHash, hash, StringComparison.Ordinal))
            .Take(RememberedLinks - 1);

        httpContext.Response.Cookies.Append(
            Name,
            string.Join(Separator, earlierHashes.Prepend(hash)),
            BuildOptions(httpContext.Request.IsHttps));
    }

    public bool WasUsedHere(HttpRequest request, string token) =>
        !string.IsNullOrEmpty(token)
        && HashesIn(request).Contains(HashOf(token), StringComparer.Ordinal);

    private string[] HashesIn(HttpRequest request) =>
        request.Cookies.TryGetValue(Name, out string? value)
            ? value.Split(Separator, StringSplitOptions.RemoveEmptyEntries)
            : [];

    internal static string HashOf(string token) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Path "/" so a link typed in another letter case still finds it. Secure follows the request scheme,
    /// as the frontend's short-lived cookie does, so the plain-http dev profile keeps working. SameSite Lax,
    /// not Strict: the link's page was first opened from a mail client, and a browser may treat Back to it
    /// like that first visit from another site, which gets no Strict cookie.
    /// </summary>
    internal static CookieOptions BuildOptions(bool isHttps) => new()
    {
        Path = "/",
        HttpOnly = true,
        Secure = isHttps,
        SameSite = SameSiteMode.Lax,
        MaxAge = Lifetime,
        IsEssential = true
    };
}
