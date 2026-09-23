using System.Buffers.Text;
using System.Security.Cryptography;

namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// The per-request CSP nonce. <see cref="SecurityHeadersMiddleware"/> puts it in <c>style-src</c>, and
/// each account page puts the same value on its inline <c>&lt;style&gt;</c> block (#693).
/// </summary>
internal static class CspNonce
{
    private const string ItemsKey = "CspNonce";
    private const int NonceSizeInBytes = 32;

    /// <summary>
    /// Base64url, not plain base64: a <c>+</c> would be HTML-encoded inside the <c>nonce</c> attribute,
    /// and the page would no longer match the header byte for byte.
    /// </summary>
    internal static string Issue(HttpContext context)
    {
        string nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(NonceSizeInBytes));
        context.Items[ItemsKey] = nonce;
        return nonce;
    }

    /// <summary>
    /// Null when the middleware did not run, as in Development. Razor then leaves the <c>nonce</c>
    /// attribute out, and with no CSP there is nothing for it to match.
    /// </summary>
    internal static string? Get(HttpContext context) =>
        context.Items.TryGetValue(ItemsKey, out object? nonce) ? nonce as string : null;
}
