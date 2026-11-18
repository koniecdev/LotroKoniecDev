using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure.Storage;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// The copy of the player's translation file when a read fails after the open worked (#42). Only
/// Windows enforces a byte-range lock against another handle, so this case runs here; the rest of the
/// workspace's tests run on any OS in <c>Infrastructure.Tests.Integration</c> (ADR-0033).
/// </summary>
public sealed class DesktopPatchWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lotro-patch-workspace-tests-{Guid.NewGuid():N}");

    private string DataFolder => Path.Combine(_root, "data");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void UseTranslationFile_AReadThatFailsAfterTheOpen_NamesThePlayersFileAndLeavesNoTempFile()
    {
        // Arrange: another program locks a range of the file, so the open works and the read fails.
        DesktopPatchWorkspace sut = new(DataFolder);
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(sut.WorkingTranslationFilePath, "earlier copy");
        string playersFolder = Path.Combine(_root, "player");
        Directory.CreateDirectory(playersFolder);
        string playersFile = Path.Combine(playersFolder, "polish.txt");
        File.WriteAllText(playersFile, "1||2||Nowy tekst||NULL||NULL||0||abc");
        using FileStream locker = new(playersFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        locker.Lock(0, locker.Length);

        // Act
        Result result = sut.UseTranslationFile(playersFile);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.DesktopPatchWorkspace.SourceUnreadableCode);
        result.Error.Message.ShouldContain(playersFile);
        File.ReadAllText(sut.WorkingTranslationFilePath).ShouldBe("earlier copy");
        Directory.GetFiles(DataFolder).ShouldBe([sut.WorkingTranslationFilePath]);
    }
}
