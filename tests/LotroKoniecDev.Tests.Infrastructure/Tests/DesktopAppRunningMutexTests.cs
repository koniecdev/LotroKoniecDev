using LotroKoniecDev.DesktopApp;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// The mutex the uninstaller checks before it removes the app (spec 0016). Windows can refuse it, for
/// example when another account made it first, and the app must start anyway.
/// </summary>
public sealed class DesktopAppRunningMutexTests
{
    [Fact]
    public void TryCreateMutex_AFreeName_MakesAMutexOthersCanFind()
    {
        // Arrange
        string name = $"LotroPoPolsku_Test_{Guid.NewGuid():N}";

        // Act
        using Mutex? mutex = Program.TryCreateMutex(name);

        // Assert: the uninstaller looks the name up, as TryOpenExisting does.
        mutex.ShouldNotBeNull();
        Mutex.TryOpenExisting(name, out Mutex? found).ShouldBeTrue();
        found?.Dispose();
    }

    [Fact]
    public void TryCreateMutex_ANameWindowsRefuses_ReturnsNullInsteadOfThrowing()
    {
        // Arrange: a semaphore holds the name, so Windows refuses a mutex under it.
        string name = $"LotroPoPolsku_Test_{Guid.NewGuid():N}";
        using Semaphore holder = new(initialCount: 1, maximumCount: 1, name);

        // Act
        using Mutex? mutex = Program.TryCreateMutex(name);

        // Assert
        mutex.ShouldBeNull();
    }
}
