using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.GameLaunching;

/// <summary>
/// Verifies the launcher's Authenticode signature via WinVerifyTrust and pins the signer subject
/// to the known LOTRO publishers, so an executable planted next to a drive-scanned DAT is refused
/// before it can run — potentially elevated — under the tool's identity (AUDIT-SEC-02 / #392).
/// </summary>
public sealed class AuthenticodeLauncherSignatureVerifier : ILauncherSignatureVerifier
{
    // Standing Stone Games LLC signs the current launcher; Turbine, Inc. signed the legacy one
    // (the same publisher history this repo's registry lookup already models). The signer's
    // subject CN must equal one of these after normalization (case, commas, periods, extra
    // whitespace) — exact equality, so a foreign "Turbine Dynamics Ltd"-style CN never matches,
    // while legal-suffix punctuation variants of the real publishers still do.
    private static readonly string[] TrustedPublisherCommonNames =
    [
        "standing stone games",
        "standing stone games llc",
        "turbine",
        "turbine inc"
    ];

    public Result VerifySignature(string launcherPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherPath);

        int trustStatus = WinTrustNative.VerifyEmbeddedSignature(launcherPath, out string? signerCommonName);
        if (trustStatus != WinTrustNative.Success)
        {
            return Result.Failure(DomainErrors.GameLaunch.UntrustedLauncher(
                launcherPath, DescribeTrustFailure(trustStatus)));
        }

        if (string.IsNullOrWhiteSpace(signerCommonName))
        {
            return Result.Failure(DomainErrors.GameLaunch.UntrustedLauncher(
                launcherPath, "its signer certificate could not be read"));
        }

        if (!IsTrustedPublisher(signerCommonName))
        {
            return Result.Failure(DomainErrors.GameLaunch.UntrustedLauncher(
                launcherPath, $"it is signed by '{signerCommonName}' instead of a known LOTRO publisher"));
        }

        return Result.Success();
    }

    internal static bool IsTrustedPublisher(string signerCommonName)
    {
        string normalizedCommonName = NormalizeCompanyName(signerCommonName);

        return TrustedPublisherCommonNames.Any(publisherName =>
            string.Equals(normalizedCommonName, publisherName, StringComparison.Ordinal));
    }

    private static string NormalizeCompanyName(string value) =>
        string.Join(' ', value
            .ToLowerInvariant()
            .Replace(",", string.Empty)
            .Replace(".", string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string DescribeTrustFailure(int trustStatus) =>
        trustStatus switch
        {
            WinTrustNative.TrustENoSignature => "it has no Authenticode signature",
            WinTrustNative.TrustEBadDigest => "its signature does not match the file contents (the file was modified)",
            WinTrustNative.TrustEExplicitDistrust => "its signature is explicitly distrusted on this machine",
            WinTrustNative.CertEUntrustedRoot => "its signature chain does not lead to a trusted root",
            WinTrustNative.TrustESubjectNotTrusted => "its signature is not trusted",
            _ => $"signature verification failed with status 0x{trustStatus:X8}"
        };
}
