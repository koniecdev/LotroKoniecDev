using LotroKoniecDev.Infrastructure.AppUpdating;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// The update's folder clean-up while an installer still holds its file (spec 0016). Only Windows
/// refuses to delete an open file, so this case runs here; the rest of the workspace's tests run on
/// any OS in <c>Infrastructure.Tests.Integration</c> (ADR-0033, 2026-11-18 amendment).
/// </summary>
public sealed class AppUpdateWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lotro-update-workspace-tests-{Guid.NewGuid():N}");

    private string DownloadsRoot => Path.Combine(_root, "updates");

    private string LogFolder => Path.Combine(_root, "appdata", "LotroPoPolsku");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void CleanUpOldDownloads_AFolderInUse_StaysAndNothingFails()
    {
        // Arrange: an installer that is still running holds its file open.
        AppUpdateWorkspace sut = new(DownloadsRoot, LogFolder);
        string inUse = sut.CreateDownloadFolder().Value;
        string free = sut.CreateDownloadFolder().Value;
        using FileStream held = new(Path.Combine(inUse, "setup.exe"), FileMode.CreateNew, FileAccess.Write, FileShare.None);

        // Act
        sut.CleanUpOldDownloads();

        // Assert
        Directory.Exists(inUse).ShouldBeTrue();
        Directory.Exists(free).ShouldBeFalse();
    }
}
