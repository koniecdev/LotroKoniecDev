namespace LotroKoniecDev.Application.Abstractions;

public interface ILauncherSignatureVerifier
{
    /// <summary>
    /// Checks that the launcher carries a valid Authenticode signature from a known LOTRO publisher.
    /// Everything else fails: no signature, a changed file, a chain we do not trust, or another
    /// signer. An executable someone put there is refused before it can run (AUDIT-SEC-02).
    /// </summary>
    Result VerifySignature(string launcherPath);
}
