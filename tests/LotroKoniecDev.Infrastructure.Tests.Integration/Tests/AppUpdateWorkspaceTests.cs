using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure.AppUpdating;

namespace LotroKoniecDev.Infrastructure.Tests.Integration.Tests;

/// <summary>
/// The update's folders against the real filesystem (spec 0016). The folder-in-use case needs
/// Windows, so it lives in <c>Tests.Infrastructure</c>.
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
    public void CreateDownloadFolder_Twice_GivesTwoNewEmptyFoldersUnderTheRoot()
    {
        // Arrange
        AppUpdateWorkspace sut = new(DownloadsRoot, LogFolder);

        // Act
        Result<string> first = sut.CreateDownloadFolder();
        Result<string> second = sut.CreateDownloadFolder();

        // Assert
        first.Value.ShouldNotBe(second.Value);
        Path.GetDirectoryName(first.Value).ShouldBe(Path.GetFullPath(DownloadsRoot));
        Directory.EnumerateFileSystemEntries(first.Value).ShouldBeEmpty();
        Directory.Exists(second.Value).ShouldBeTrue();
    }

    [Fact]
    public void CleanUpOldDownloads_EarlierFolders_RemovesEveryOne()
    {
        // Arrange
        AppUpdateWorkspace sut = new(DownloadsRoot, LogFolder);
        string old = sut.CreateDownloadFolder().Value;
        File.WriteAllText(Path.Combine(old, "LotroPoPolsku-Setup-0.1.0.exe"), "old installer");

        // Act
        sut.CleanUpOldDownloads();

        // Assert
        Directory.Exists(old).ShouldBeFalse();
    }

    [Fact]
    public void DeleteDownloadFolder_AFailedUpdatesFolder_RemovesItWithItsFile()
    {
        // Arrange
        AppUpdateWorkspace sut = new(DownloadsRoot, LogFolder);
        string folder = sut.CreateDownloadFolder().Value;
        File.WriteAllText(Path.Combine(folder, "LotroPoPolsku-Setup-0.3.0.exe"), "refused installer");

        // Act
        sut.DeleteDownloadFolder(folder);

        // Assert
        Directory.Exists(folder).ShouldBeFalse();
    }

    [Fact]
    public void DeleteDownloadFolder_AFolderThatIsGone_DoesNothing()
    {
        // Arrange
        AppUpdateWorkspace sut = new(DownloadsRoot, LogFolder);
        string folder = Path.Combine(DownloadsRoot, "gone");

        // Act + Assert: best effort, so nothing throws.
        Should.NotThrow(() => sut.DeleteDownloadFolder(folder));
    }

    [Fact]
    public void CleanUpOldDownloads_NoRootYet_DoesNothing()
    {
        // Arrange
        AppUpdateWorkspace sut = new(DownloadsRoot, LogFolder);

        // Act
        sut.CleanUpOldDownloads();

        // Assert
        Directory.Exists(DownloadsRoot).ShouldBeFalse();
    }

    [Fact]
    public void CreateDownloadFolder_ARootThatCannotBeMade_IsAFileSystemError()
    {
        // Arrange: a file stands where the folder should go.
        string blocker = CreateBlockingFile();
        AppUpdateWorkspace sut = new(Path.Combine(blocker, "updates"), LogFolder);

        // Act
        Result<string> folder = sut.CreateDownloadFolder();

        // Assert
        folder.Error.Code.ShouldBe(DomainErrors.AppUpdate.FileSystemErrorCode);
    }

    [Fact]
    public void PrepareInstallerLogPath_AFolderThatCannotBeMade_IsAFileSystemError()
    {
        // Arrange
        string blocker = CreateBlockingFile();
        AppUpdateWorkspace sut = new(DownloadsRoot, Path.Combine(blocker, "LotroPoPolsku"));

        // Act
        Result<string> logPath = sut.PrepareInstallerLogPath();

        // Assert
        logPath.Error.Code.ShouldBe(DomainErrors.AppUpdate.FileSystemErrorCode);
    }

    [Fact]
    public void PrepareInstallerLogPath_NoFolderYet_CreatesItAndReturnsAnAbsolutePath()
    {
        // Arrange
        AppUpdateWorkspace sut = new(DownloadsRoot, LogFolder);

        // Act
        Result<string> logPath = sut.PrepareInstallerLogPath();

        // Assert: Inno Setup stops when it cannot create its log, so the folder must exist first.
        logPath.Value.ShouldBe(Path.Combine(Path.GetFullPath(LogFolder), "update-install.log"));
        Path.IsPathFullyQualified(logPath.Value).ShouldBeTrue();
        Directory.Exists(LogFolder).ShouldBeTrue();
    }

    private string CreateBlockingFile()
    {
        Directory.CreateDirectory(_root);
        string blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "a file where a folder should be");
        return blocker;
    }
}
