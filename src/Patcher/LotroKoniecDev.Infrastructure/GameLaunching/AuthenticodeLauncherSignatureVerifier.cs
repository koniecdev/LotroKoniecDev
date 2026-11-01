using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.GameLaunching;

/// <summary>
/// Checks the launcher's Authenticode signature with WinVerifyTrust and requires the signer to be one
/// of the known LOTRO publishers. An executable someone dropped next to a DAT we found by scanning is
/// then refused before it can run under our identity, possibly with admin rights (#392).
/// </summary>
public sealed class AuthenticodeLauncherSignatureVerifier : ILauncherSignatureVerifier
{
    // Standing Stone Games LLC signs the current launcher, and Turbine, Inc. signed the old one.
    // After we normalize case, commas, periods and extra spaces, the signer's CN must equal one of
    // these. So "Turbine Dynamics Ltd" never matches, while punctuation variants of the real names do.
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
