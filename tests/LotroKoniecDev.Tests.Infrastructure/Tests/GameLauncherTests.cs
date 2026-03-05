using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure.GameLaunching;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

public sealed class GameLauncherTests
{
    private readonly GameLauncher _sut = new();

    [Fact]
    public async Task LaunchAndWaitForExitAsync_ShouldReturnFailure_WhenLauncherNotFound()
    {
        // Arrange
        string fakePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "client_local_English.dat");

        // Act
        Result<int> result = await _sut.LaunchAndWaitForExitAsync(fakePath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameLaunch.NotFound");
    }

    [Fact]
    public async Task LaunchAndWaitForExitAsync_ShouldThrow_WhenPathIsNull()
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _sut.LaunchAndWaitForExitAsync(null!));
    }

    [Fact]
    public async Task LaunchAndWaitForExitAsync_ShouldThrow_WhenPathIsEmpty()
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _sut.LaunchAndWaitForExitAsync(""));
    }

    [Fact]
    public async Task LaunchAndWaitForExitAsync_ShouldThrow_WhenPathIsWhitespace()
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _sut.LaunchAndWaitForExitAsync("   "));
    }

    [Fact]
    public async Task LaunchAndWaitForExitAsync_ShouldLookForLauncherInDatFileDirectory()
    {
        // Arrange — create a temp directory with a fake DAT file but no launcher
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            string fakeDatFile = Path.Combine(tempDir, "client_local_English.dat");
            File.WriteAllText(fakeDatFile, "fake");

            // Act
            Result<int> result = await _sut.LaunchAndWaitForExitAsync(fakeDatFile);

            // Assert — should fail because LotroLauncher.exe doesn't exist in tempDir
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
    public async Task LaunchAndWaitForExitAsync_ShouldAcceptDirectoryPath()
    {
        // Arrange — pass a directory path (not a DAT file path)
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            // Act
            Result<int> result = await _sut.LaunchAndWaitForExitAsync(tempDir);

            // Assert — should fail because LotroLauncher.exe doesn't exist
            result.IsFailure.ShouldBeTrue();
            result.Error.Code.ShouldBe("GameLaunch.NotFound");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
