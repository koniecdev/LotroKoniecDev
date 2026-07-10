namespace LotroKoniecDev.Application.Abstractions;

public interface ILauncherSignatureVerifier
{
    /// <summary>
    /// Verifies that the launcher executable carries a valid Authenticode signature from a known
    /// LOTRO publisher. Any other outcome — unsigned, tampered, untrusted chain, foreign signer —
    /// is a failure, so a planted executable is refused before it can run (AUDIT-SEC-02).
    /// </summary>
    Result VerifySignature(string launcherPath);
}
