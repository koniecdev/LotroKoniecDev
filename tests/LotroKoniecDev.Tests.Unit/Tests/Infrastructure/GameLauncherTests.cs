using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure.GameLaunching;

namespace LotroKoniecDev.Tests.Unit.Tests.Infrastructure;

public sealed class GameLauncherTests
{
    private readonly GameLauncher _sut = new();

    [Fact]
    public void Launch_ShouldReturnFailure_WhenLauncherNotFound()
    {
        // Arrange
        string fakePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "client_local_English.dat");

        // Act
        Result<int> result = _sut.Launch(fakePath);

        // Assert
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
        // Arrange — create a temp directory with a fake DAT file but no launcher
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            string fakeDatFile = Path.Combine(tempDir, "client_local_English.dat");
            File.WriteAllText(fakeDatFile, "fake");

            // Act
            Result<int> result = _sut.Launch(fakeDatFile);

            // Assert — should fail because TurbineLauncher.exe doesn't exist in tempDir
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
        // Arrange — pass a directory path (not a DAT file path)
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            // Act
            Result<int> result = _sut.Launch(tempDir);

            // Assert — should fail because TurbineLauncher.exe doesn't exist
            result.IsFailure.ShouldBeTrue();
            result.Error.Code.ShouldBe("GameLaunch.NotFound");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
