using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.DesktopApp;
using LotroKoniecDev.Domain.Core.Monads;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// Where the app keeps its own files (#1464): under the exe's folder, which only administrators can
/// change once the app is installed under Program Files. Nothing goes to the player's AppData or temp
/// folder, which any program of that player can change.
/// </summary>
public sealed class DesktopAppDataFolderTests : IDisposable
{
    private static readonly string ExeFolder = AppContext.BaseDirectory;

    private readonly string _updatesFolder = Path.Combine(ExeFolder, "updates");
    private readonly bool _updatesFolderExisted;
    private string? _downloadFolder;

    public void Dispose()
    {
        if (!_updatesFolderExisted && Directory.Exists(_updatesFolder))
        {
            Directory.Delete(_updatesFolder, recursive: true);
        }
        else if (_downloadFolder is not null && Directory.Exists(_downloadFolder))
        {
            Directory.Delete(_downloadFolder, recursive: true);
        }
    }

    [Fact]
    public void SettingsFilePath_TheAppsSettings_SitInTheDataFolderNextToTheExe()
    {
        // Act
        string settingsFilePath = DesktopAppDependencyInjection.SettingsFilePath;

        // Assert
        settingsFilePath.ShouldBe(Path.Combine(ExeFolder, "data", "config.json"));
    }

    [Fact]
    public void LogFilePath_TheAppsLog_SitsInTheDataFolderNextToTheExe()
    {
        // Act
        string logFilePath = Program.LogFilePath;

        // Assert
        logFilePath.ShouldBe(Path.Combine(ExeFolder, "data", "LotroPoPolsku.log"));
    }

    [Fact]
    public void BuildServiceProvider_TheUpdateWorkspace_KeepsDownloadsAndTheInstallerLogInTheUpdatesFolderNextToTheExe()
    {
        // Arrange
        using ServiceProvider serviceProvider = DesktopAppDependencyInjection.BuildServiceProvider();
        IAppUpdateWorkspace sut = serviceProvider.GetRequiredService<IAppUpdateWorkspace>();

        // Act
        Result<string> downloadFolder = sut.CreateDownloadFolder();
        _downloadFolder = downloadFolder.IsSuccess ? downloadFolder.Value : null;
        Result<string> installerLog = sut.PrepareInstallerLogPath();

        // Assert
        downloadFolder.IsSuccess.ShouldBeTrue();
        installerLog.IsSuccess.ShouldBeTrue();
        Path.GetDirectoryName(downloadFolder.Value).ShouldBe(_updatesFolder);
        installerLog.Value.ShouldBe(Path.Combine(_updatesFolder, "update-install.log"));
    }

    [Fact]
    public void BuildServiceProvider_ThePatchWorkspace_KeepsEveryTranslationCopyInTheDataFolderNextToTheExe()
    {
        // Arrange
        using ServiceProvider serviceProvider = DesktopAppDependencyInjection.BuildServiceProvider();

        // Act
        IDesktopPatchWorkspace sut = serviceProvider.GetRequiredService<IDesktopPatchWorkspace>();

        // Assert: the elevated app writes small files next to these, so none may sit in a folder the player chose (#42).
        string dataFolder = Path.Combine(ExeFolder, "data");
        sut.DownloadedTranslationFilePath.ShouldBe(Path.Combine(dataFolder, "download", "polish.txt"));
        sut.WorkingTranslationFilePath.ShouldBe(Path.Combine(dataFolder, "polish.txt"));
        sut.GameVersionFilePath.ShouldBe(Path.Combine(dataFolder, "last_known_game_version.txt"));
    }

    public DesktopAppDataFolderTests()
    {
        _updatesFolderExisted = Directory.Exists(_updatesFolder);
    }
}
