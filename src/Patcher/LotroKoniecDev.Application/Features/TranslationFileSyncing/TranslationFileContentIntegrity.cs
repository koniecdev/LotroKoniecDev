using System.Security.Cryptography;
using System.Text;

namespace LotroKoniecDev.Application.Features.TranslationFileSyncing;

/// <summary>
/// Checks a downloaded translation file against the TMS contract: the endpoint's strong <c>ETag</c> is
/// the SHA-256 of the file's UTF-8 bytes in hex, computed by the TMS
/// <c>PrecomputedTranslationFileProjector</c>. So the client can tell that a body was damaged or cut
/// short on the way (AUDIT-SEC-01, #391).
/// It does <b>not</b> prove who sent the file. The hash arrives in the same response as the body, so
/// anyone who can answer for the server can make a matching pair. TLS is what proves the sender, which
/// is why the sync validator refuses plain http for anything but loopback.
/// A missing or weak ETag cannot be checked, so it fails.
/// On the server side the
/// <c>Get_ETagIsTheSha256OfTheBody_SoThePatcherIntegrityCheckAcceptsIt</c> integration test holds this
/// up. Changing the hash on either side changes a contract between the two contexts.
/// </summary>
public static class TranslationFileContentIntegrity
{
    public static bool Matches(string content, string? eTag)
    {
        if (string.IsNullOrEmpty(eTag) || eTag.StartsWith("W/", StringComparison.Ordinal))
        {
            return false;
        }

        string expectedHash = eTag.Trim('"');
        string actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase);
    }
}
