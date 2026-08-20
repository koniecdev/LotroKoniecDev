using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure.GameLaunching;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// Exercises the signature check done before launching (AUDIT-SEC-02, #392) against real files and the
/// real WinVerifyTrust, so it is Windows-only, like the rest of this project.
/// </summary>
public sealed class AuthenticodeLauncherSignatureVerifierTests
{
    private readonly AuthenticodeLauncherSignatureVerifier _sut = new();

    [Fact]
    public void VerifySignature_UnsignedExecutable_ShouldReturnUntrustedLauncherFailure()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            string unsignedLauncher = Path.Combine(tempDir, "LotroLauncher.exe");
            File.WriteAllText(unsignedLauncher, "not a signed executable");

            Result result = _sut.VerifySignature(unsignedLauncher);

            result.IsFailure.ShouldBeTrue();
            result.Error.Code.ShouldBe(DomainErrors.GameLaunch.UntrustedLauncherCode);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Launch_FakeUnsignedLauncherNextToScannedDat_ShouldBeRefused()
    {
        // The ticket's attack scenario end-to-end: a planted DAT + unsigned launcher pair in a
        // writable folder must be refused by the real launcher + real verifier composition.
        GameLauncher gameLauncher = new(_sut);
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            string fakeDatFile = Path.Combine(tempDir, "client_local_English.dat");
            File.WriteAllText(fakeDatFile, "fake");
            string fakeLauncher = Path.Combine(tempDir, "LotroLauncher.exe");
            File.WriteAllText(fakeLauncher, "not a signed executable");

            Result result = gameLauncher.Launch(fakeDatFile);

            result.IsFailure.ShouldBeTrue();
            result.Error.Code.ShouldBe(DomainErrors.GameLaunch.UntrustedLauncherCode);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void VerifySignature_NullOrWhitespacePath_ShouldThrow(string? launcherPath)
    {
        Should.Throw<ArgumentException>(() => _sut.VerifySignature(launcherPath!));
    }

    [Fact]
    public void VerifySignature_NonexistentPath_ShouldReturnUntrustedLauncherFailure()
    {
        string nonexistentLauncher = Path.Combine(
            Path.GetTempPath(), Guid.NewGuid().ToString(), "LotroLauncher.exe");

        Result result = _sut.VerifySignature(nonexistentLauncher);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.GameLaunch.UntrustedLauncherCode);
    }

    [Theory]
    [InlineData("Standing Stone Games LLC", true)]
    [InlineData("Standing Stone Games, LLC", true)]
    [InlineData("Standing Stone Games", true)]
    [InlineData("standing stone games llc", true)]
    [InlineData("Turbine, Inc.", true)]
    [InlineData("Turbine Inc", true)]
    [InlineData("Turbine", true)]
    [InlineData("Turbine Dynamics Ltd", false)]
    [InlineData("Standing Stone Games Fan Club", false)]
    [InlineData("Evil Corp LLC", false)]
    [InlineData("Microsoft Corporation", false)]
    [InlineData("", false)]
    public void IsTrustedPublisher_ShouldMatchOnlyKnownLotroPublisherCommonNames(string signerCommonName, bool expected)
    {
        AuthenticodeLauncherSignatureVerifier.IsTrustedPublisher(signerCommonName).ShouldBe(expected);
    }
}
