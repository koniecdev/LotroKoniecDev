using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure.Storage;

namespace LotroKoniecDev.Infrastructure.Tests.Integration.Tests;

/// <summary>
/// The desktop app's copies of the translation file and its patch state, against the real filesystem
/// (#42). Every file the adapter writes stays in the data folder, never next to the player's file.
/// </summary>
public sealed class DesktopPatchWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lotro-patch-workspace-tests-{Guid.NewGuid():N}");

    private string DataFolder => Path.Combine(_root, "data");

    private string PlayerFolder => Path.Combine(_root, "player");

    private string MarkerPath => Path.Combine(DataFolder, DesktopPatchWorkspace.PatchedGameFileName);

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        // Windows refuses to delete a read-only file, and some tests make one on purpose.
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Constructor_AnyFolder_PutsEveryFileUnderTheDataFolder()
    {
        // Act
        DesktopPatchWorkspace sut = new(DataFolder);

        // Assert
        string data = Path.GetFullPath(DataFolder);
        sut.DownloadedTranslationFilePath.ShouldBe(Path.Combine(data, "download", "polish.txt"));
        sut.WorkingTranslationFilePath.ShouldBe(Path.Combine(data, "polish.txt"));
        sut.GameVersionFilePath.ShouldBe(Path.Combine(data, "last_known_game_version.txt"));
    }

    [Fact]
    public void UseTranslationFile_ThePlayersFile_CopiesItOverTheWorkingCopyAndWritesNothingBesideIt()
    {
        // Arrange
        DesktopPatchWorkspace sut = new(DataFolder);
        string playersFile = WritePlayersFile("polish.txt", "1||2||Nowy tekst||NULL||NULL||0||abc");
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(sut.WorkingTranslationFilePath, "old copy");

        // Act
        Result result = sut.UseTranslationFile(playersFile);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        File.ReadAllText(sut.WorkingTranslationFilePath).ShouldBe("1||2||Nowy tekst||NULL||NULL||0||abc");
        Directory.GetFiles(PlayerFolder).ShouldBe([playersFile]);
    }

    [Fact]
    public void UseTranslationFile_TheFirstTime_CreatesTheDataFolder()
    {
        // Arrange
        DesktopPatchWorkspace sut = new(DataFolder);
        string playersFile = WritePlayersFile("polish.txt", "content");

        // Act
        Result result = sut.UseTranslationFile(playersFile);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        File.Exists(sut.WorkingTranslationFilePath).ShouldBeTrue();
    }

    [Fact]
    public void UseTranslationFile_TheWorkingCopysPatcherFiles_LeavesThemAlone()
    {
        // Arrange: the ledger and the snapshot describe the game file, not the source of the copy.
        DesktopPatchWorkspace sut = new(DataFolder);
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(sut.WorkingTranslationFilePath + ".ledger", "ledger");
        File.WriteAllText(sut.WorkingTranslationFilePath + ".iterations", "snapshot");

        // Act
        Result result = sut.UseTranslationFile(WritePlayersFile("polish.txt", "content"));

        // Assert
        result.IsSuccess.ShouldBeTrue();
        File.ReadAllText(sut.WorkingTranslationFilePath + ".ledger").ShouldBe("ledger");
        File.ReadAllText(sut.WorkingTranslationFilePath + ".iterations").ShouldBe("snapshot");
    }

    [Theory]
    [InlineData("gone.txt")]
    [InlineData("unplugged-drive")]
    public void UseTranslationFile_AMissingFile_ReturnsNotFoundAndKeepsTheWorkingCopy(string missing)
    {
        // Arrange
        DesktopPatchWorkspace sut = new(DataFolder);
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(sut.WorkingTranslationFilePath, "earlier copy");
        string missingPath = missing.EndsWith(".txt", StringComparison.Ordinal)
            ? Path.Combine(PlayerFolder, missing)
            : Path.Combine(_root, missing, "polish.txt");

        // Act
        Result result = sut.UseTranslationFile(missingPath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.DesktopPatchWorkspace.SourceNotFoundCode);
        result.Error.Message.ShouldContain(missingPath);
        File.ReadAllText(sut.WorkingTranslationFilePath).ShouldBe("earlier copy");
        Directory.GetFiles(DataFolder).ShouldBe([sut.WorkingTranslationFilePath]);
    }

    [Fact]
    public void UseTranslationFile_AFolderInsteadOfAFile_ReturnsAFileSystemErrorAndLeavesNoTempFile()
    {
        // Arrange
        DesktopPatchWorkspace sut = new(DataFolder);
        Directory.CreateDirectory(PlayerFolder);

        // Act
        Result result = sut.UseTranslationFile(PlayerFolder);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.DesktopPatchWorkspace.FileSystemErrorCode);
        Directory.GetFiles(DataFolder).ShouldBeEmpty();
    }

    [Fact]
    public void UseTranslationFile_AReadOnlyFile_GivesAWorkingCopyTheNextRunCanReplace()
    {
        // Arrange: the player's file may carry the Read-only flag (a file from a CD, a hand-set flag).
        DesktopPatchWorkspace sut = new(DataFolder);
        string readOnlyFile = WritePlayersFile("polish.txt", "first");
        File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);
        string plainFile = WritePlayersFile("second.txt", "second");

        // Act
        Result first = sut.UseTranslationFile(readOnlyFile);
        Result second = sut.UseTranslationFile(plainFile);

        // Assert
        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        File.ReadAllText(sut.WorkingTranslationFilePath).ShouldBe("second");
        File.GetAttributes(sut.WorkingTranslationFilePath).HasFlag(FileAttributes.ReadOnly).ShouldBeFalse();
        Directory.GetFiles(DataFolder).ShouldBe([sut.WorkingTranslationFilePath]);
    }

    [Fact]
    public void UseTranslationFile_AWorkingCopyMarkedReadOnly_IsStillReplaced()
    {
        // Arrange
        DesktopPatchWorkspace sut = new(DataFolder);
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(sut.WorkingTranslationFilePath, "old copy");
        File.SetAttributes(sut.WorkingTranslationFilePath, FileAttributes.ReadOnly);

        // Act
        Result result = sut.UseTranslationFile(WritePlayersFile("polish.txt", "new copy"));

        // Assert
        result.IsSuccess.ShouldBeTrue();
        File.ReadAllText(sut.WorkingTranslationFilePath).ShouldBe("new copy");
    }

    [Fact]
    public void UseTranslationFile_TheWorkingCopyCannotBeReplaced_NamesTheWorkingCopyAndLeavesNoTempFile()
    {
        // Arrange: a folder where the working copy should be cannot be replaced by a file.
        DesktopPatchWorkspace sut = new(DataFolder);
        Directory.CreateDirectory(sut.WorkingTranslationFilePath);

        // Act
        Result result = sut.UseTranslationFile(WritePlayersFile("polish.txt", "content"));

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.DesktopPatchWorkspace.FileSystemErrorCode);
        result.Error.Message.ShouldContain(sut.WorkingTranslationFilePath);
        Directory.GetFiles(DataFolder).ShouldBeEmpty();
    }

    [Fact]
    public void ResetPatchStateIfGameFileChanged_NoMarkerYet_DeletesTheStoredVersionAndRecordsTheGameFile()
    {
        // Arrange: a version file with no marker was saved for a game file nobody recorded.
        DesktopPatchWorkspace sut = new(DataFolder);
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(sut.GameVersionFilePath, "40.2|100|200|abc");
        string gameFile = GameFile("LOTRO");

        // Act
        Result result = sut.ResetPatchStateIfGameFileChanged(gameFile);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        File.Exists(sut.GameVersionFilePath).ShouldBeFalse();
        File.ReadAllText(MarkerPath).ShouldBe(Path.GetFullPath(gameFile));
    }

    [Fact]
    public void ResetPatchStateIfGameFileChanged_BeforeTheDataFolderExists_RecordsTheGameFile()
    {
        // Arrange
        DesktopPatchWorkspace sut = new(DataFolder);
        string gameFile = GameFile("LOTRO");

        // Act
        Result result = sut.ResetPatchStateIfGameFileChanged(gameFile);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        File.ReadAllText(MarkerPath).ShouldBe(Path.GetFullPath(gameFile));
    }

    [Fact]
    public void ResetPatchStateIfGameFileChanged_TheSameGameFile_KeepsTheStoredVersion()
    {
        // Arrange
        DesktopPatchWorkspace sut = new(DataFolder);
        string gameFile = GameFile("LOTRO");
        sut.ResetPatchStateIfGameFileChanged(gameFile);
        File.WriteAllText(sut.GameVersionFilePath, "40.2|100|200|abc");

        // Act
        Result result = sut.ResetPatchStateIfGameFileChanged(gameFile);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        File.ReadAllText(sut.GameVersionFilePath).ShouldBe("40.2|100|200|abc");
    }

    [Fact]
    public void ResetPatchStateIfGameFileChanged_AnotherGameFile_DeletesTheStoredVersionAndRecordsTheNewOne()
    {
        // Arrange: a second install of the same game version would otherwise inherit the hash.
        DesktopPatchWorkspace sut = new(DataFolder);
        sut.ResetPatchStateIfGameFileChanged(GameFile("LOTRO"));
        File.WriteAllText(sut.GameVersionFilePath, "40.2|100|200|abc");
        string secondInstall = GameFile("LOTRO-copy");

        // Act
        Result result = sut.ResetPatchStateIfGameFileChanged(secondInstall);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        File.Exists(sut.GameVersionFilePath).ShouldBeFalse();
        File.ReadAllText(MarkerPath).ShouldBe(Path.GetFullPath(secondInstall));
    }

    [Fact]
    public void ResetPatchStateIfGameFileChanged_AnotherGameFile_KeepsTheLedgerAndTheSnapshot()
    {
        // Arrange: the ledger admits only text this patcher wrote, so it can stay (ADR-0047 §4).
        DesktopPatchWorkspace sut = new(DataFolder);
        sut.ResetPatchStateIfGameFileChanged(GameFile("LOTRO"));
        File.WriteAllText(sut.WorkingTranslationFilePath + ".ledger", "ledger");

        // Act
        Result result = sut.ResetPatchStateIfGameFileChanged(GameFile("LOTRO-copy"));

        // Assert
        result.IsSuccess.ShouldBeTrue();
        File.ReadAllText(sut.WorkingTranslationFilePath + ".ledger").ShouldBe("ledger");
    }

    [Fact]
    public void ResetPatchStateIfGameFileChanged_TheStoredVersionCannotBeDeleted_FailsAndRecordsNothing()
    {
        // Arrange: a folder where the version file should be cannot be deleted as a file.
        DesktopPatchWorkspace sut = new(DataFolder);
        Directory.CreateDirectory(sut.GameVersionFilePath);

        // Act
        Result result = sut.ResetPatchStateIfGameFileChanged(GameFile("LOTRO"));

        // Assert: with no marker written, the next run tries the delete again.
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.DesktopPatchWorkspace.FileSystemErrorCode);
        result.Error.Message.ShouldContain(sut.GameVersionFilePath);
        File.Exists(MarkerPath).ShouldBeFalse();
    }

    [Fact]
    public void ResetPatchStateIfGameFileChanged_AMarkerThatCannotBeRead_CountsAsAnotherGameFile()
    {
        // Arrange: a folder where the marker should be cannot be read as text.
        DesktopPatchWorkspace sut = new(DataFolder);
        Directory.CreateDirectory(MarkerPath);
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(sut.GameVersionFilePath, "40.2|100|200|abc");

        // Act
        Result result = sut.ResetPatchStateIfGameFileChanged(GameFile("LOTRO"));

        // Assert: the version goes, so the next patch is a full one; the marker cannot replace a folder.
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.DesktopPatchWorkspace.FileSystemErrorCode);
        File.Exists(sut.GameVersionFilePath).ShouldBeFalse();
    }

    private string WritePlayersFile(string name, string content)
    {
        Directory.CreateDirectory(PlayerFolder);
        string path = Path.Combine(PlayerFolder, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string GameFile(string folder) => Path.Combine(_root, folder, "client_local_English.dat");
}
