using System.Security.Cryptography;
using System.Text;

namespace LotroKoniecDev.Application.Features.TranslationFileSyncing;

/// <summary>
/// Verifies a downloaded translation file against the TMS distribution contract: the endpoint's
/// strong <c>ETag</c> is the hex SHA-256 of the file's UTF-8 bytes (computed by the TMS
/// <c>PrecomputedTranslationFileProjector</c>), so the client can detect a body corrupted or
/// truncated in transit/storage (AUDIT-SEC-01 / #391). It is <b>not</b> an authenticity proof —
/// the hash arrives in the same response as the body, so anyone who can speak for the server can
/// forge a matching pair; authenticity rests entirely on TLS, which is why the sync validator
/// refuses plain http for non-loopback hosts. A missing or weak ETag is unverifiable and
/// therefore fails the check. Guarded on the server side by the
/// <c>Get_ETagIsTheSha256OfTheBody_SoThePatcherIntegrityCheckAcceptsIt</c> integration test —
/// changing either side's hash is a cross-context contract change.
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
