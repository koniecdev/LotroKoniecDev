using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LotroKoniecDev.Infrastructure.GameLaunching;

/// <summary>
/// The P/Invoke wrapper for WinVerifyTrust (wintrust.dll). It checks a file's embedded Authenticode
/// signature against Windows' own trust rules and returns the signer taken from the certificate chain
/// that was checked.
/// </summary>
internal static partial class WinTrustNative
{
    public const int Success = 0;

    public const int TrustENoSignature = unchecked((int)0x800B0100);
    public const int TrustESubjectNotTrusted = unchecked((int)0x800B0004);
    public const int TrustEBadDigest = unchecked((int)0x80096010);
    public const int TrustEExplicitDistrust = unchecked((int)0x800B0111);
    public const int CertEUntrustedRoot = unchecked((int)0x800B0109);

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x10;

    private static readonly Guid WintrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    // The WinVerifyTrust docs say INVALID_HANDLE_VALUE turns off every dialog the trust provider
    // would show.
    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>
    /// Checks the embedded Authenticode signature of <paramref name="filePath"/>.
    /// Revocation is not checked online, and a timestamped signature stays valid after its
    /// certificate expires. Gaming machines are often offline, and launchers get old.
    /// </summary>
    /// <param name="signerCommonName">
    /// On success, the common name (CN) of the signing certificate, read from the chain that was
    /// checked. <c>null</c> when it cannot be read.
    /// </param>
    /// <returns>
    /// <see cref="Success"/> (zero) when the signature is valid and trusted, otherwise the
    /// WinVerifyTrust status code, such as <see cref="TrustENoSignature"/>.
    /// </returns>
    public static unsafe int VerifyEmbeddedSignature(string filePath, out string? signerCommonName)
    {
        signerCommonName = null;

        fixed (char* filePathPointer = filePath)
        {
            WintrustFileInfo fileInfo = new()
            {
                StructSize = (uint)sizeof(WintrustFileInfo),
                FilePath = (IntPtr)filePathPointer
            };

            WintrustData data = new()
            {
                StructSize = (uint)sizeof(WintrustData),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceFile,
                FileInfo = (IntPtr)(&fileInfo),
                StateAction = WtdStateActionVerify,
                ProvFlags = WtdRevocationCheckNone
            };

            try
            {
                int status = WinVerifyTrust(InvalidHandleValue, in WintrustActionGenericVerifyV2, ref data);

                if (status == Success)
                {
                    signerCommonName = ReadSignerCommonName(data.StateData);
                }

                return status;
            }
            finally
            {
                data.StateAction = WtdStateActionClose;
                _ = WinVerifyTrust(InvalidHandleValue, in WintrustActionGenericVerifyV2, ref data);
            }
        }
    }

    private static unsafe string? ReadSignerCommonName(IntPtr stateData)
    {
        IntPtr providerData = WTHelperProvDataFromStateData(stateData);
        if (providerData == IntPtr.Zero)
        {
            return null;
        }

        CryptProviderSgnr* signer = (CryptProviderSgnr*)WTHelperGetProvSignerFromChain(
            providerData, signerIndex: 0, counterSigner: 0, counterSignerIndex: 0);
        if (signer is null || signer->CertChainCount == 0 || signer->CertChain is null)
        {
            return null;
        }

        // pasCertChain[0] is the signing certificate at the end of the checked chain.
        IntPtr certContext = signer->CertChain->CertContext;
        if (certContext == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            // The IntPtr constructor copies the CERT_CONTEXT, so the name stays valid after the
            // WinVerifyTrust state this pointer belongs to is gone.
            using X509Certificate2 signerCertificate = new(certContext);
            string commonName = signerCertificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return string.IsNullOrWhiteSpace(commonName) ? null : commonName;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int WinVerifyTrust(IntPtr hwnd, in Guid actionId, ref WintrustData data);

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperProvDataFromStateData")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperGetProvSignerFromChain")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial IntPtr WTHelperGetProvSignerFromChain(
        IntPtr providerData, uint signerIndex, int counterSigner, uint counterSignerIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubjectGuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct CryptProviderSgnr
    {
        public uint StructSize;
        public uint VerifyAsOfLow;
        public uint VerifyAsOfHigh;
        public uint CertChainCount;
        public CryptProviderCert* CertChain;
        public uint SignerType;
        public IntPtr Signer;
        public uint Error;
        public uint CounterSignersCount;
        public IntPtr CounterSigners;
        public IntPtr ChainContext;
    }

    /// <summary>
    /// The first fields of the native CRYPT_PROVIDER_CERT. We read only those, so the rest are left
    /// out on purpose.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCert
    {
        public uint StructSize;
        public IntPtr CertContext;
    }
}
