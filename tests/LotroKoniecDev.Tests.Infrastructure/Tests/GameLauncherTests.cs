using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure.GameLaunching;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

public sealed class GameLauncherTests
{
    private readonly GameLauncher _sut = new();

    [Fact]
    public void Launch_ShouldReturnFailure_WhenLauncherNotFound()
    {
        string fakePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "client_local_English.dat");

        Result result = _sut.Launch(fakePath);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameLaunch.NotFound");
    }

    [Fact]
    public void Launch_ShouldThrow_WhenPathIsNull()
    {
        Should.Throw<ArgumentException>(() => _sut.Launch(null!));
    }

    [Fact]
    public void Launch_ShouldThrow_WhenPathIsEmpty()
    {
        Should.Throw<ArgumentException>(() => _sut.Launch(""));
    }

    [Fact]
    public void Launch_ShouldThrow_WhenPathIsWhitespace()
    {
        Should.Throw<ArgumentException>(() => _sut.Launch("   "));
    }

    [Fact]
    public void Launch_ShouldLookForLauncherInDatFileDirectory()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            string fakeDatFile = Path.Combine(tempDir, "client_local_English.dat");
            File.WriteAllText(fakeDatFile, "fake");

            Result result = _sut.Launch(fakeDatFile);

            result.IsFailure.ShouldBeTrue();
            result.Error.Code.ShouldBe("GameLaunch.NotFound");
            result.Error.Message.ShouldContain(tempDir);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Launch_ShouldAcceptDirectoryPath()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            Result result = _sut.Launch(tempDir);

            result.IsFailure.ShouldBeTrue();
            result.Error.Code.ShouldBe("GameLaunch.NotFound");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
