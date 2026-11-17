using LotroKoniecDev.DesktopApp;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// The desktop app's <c>config.json</c> and file check against the real filesystem (#43). The first
/// save has no file to replace, which is why the save moves a new file into place.
/// </summary>
public sealed class DesktopSettingsFileTests : IDisposable
{
    private const string Json = "{\n  \"gameFolder\": \"C:\\\\Gry\\\\LOTRO — świeża instalacja\"\n}";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"lotro-desktop-settings-tests-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_directory, "LotroPoPolsku", "config.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Exists_BeforeTheFirstSave_ReturnsFalse()
    {
        // Arrange
        SettingsFile sut = new(SettingsPath);

        // Act
        bool exists = sut.Exists();

        // Assert
        exists.ShouldBeFalse();
    }

    [Fact]
    public void Write_OnTheFirstSave_CreatesTheFolderAndTheFile()
    {
        // Arrange
        SettingsFile sut = new(SettingsPath);

        // Act
        sut.Write(Json);

        // Assert
        sut.Exists().ShouldBeTrue();
        sut.Read().ShouldBe(Json);
    }

    [Fact]
    public void Write_OverAnEarlierSave_ReplacesTheWholeFile()
    {
        // Arrange
        SettingsFile sut = new(SettingsPath);
        sut.Write(Json + new string(' ', 4096));

        // Act
        sut.Write("{}");

        // Assert
        sut.Read().ShouldBe("{}");
    }

    [Fact]
    public void Write_Always_LeavesNoTemporaryFileBehind()
    {
        // Arrange
        SettingsFile sut = new(SettingsPath);

        // Act
        sut.Write(Json);
        sut.Write(Json);

        // Assert
        Directory.GetFiles(Path.GetDirectoryName(SettingsPath)!).ShouldBe([SettingsPath]);
    }

    [Fact]
    public void Write_Always_WritesUtf8WithoutAByteOrderMark()
    {
        // Arrange
        SettingsFile sut = new(SettingsPath);

        // Act
        sut.Write(Json);

        // Assert
        File.ReadAllBytes(SettingsPath)[0].ShouldBe((byte)'{');
    }

    [Fact]
    public void Write_WhenTheFileCannotBeReplaced_ThrowsAnErrorTheStoreCatchesAndLeavesNoTemporaryFile()
    {
        // Arrange: a folder where the file should be stands for a file that cannot be replaced.
        Directory.CreateDirectory(SettingsPath);
        SettingsFile sut = new(SettingsPath);

        // Act
        Exception exception = Should.Throw<Exception>(() => sut.Write(Json));

        // Assert: the store catches only these two, so any other type would end the app.
        (exception is IOException or UnauthorizedAccessException).ShouldBeTrue(exception.GetType().FullName);
        Directory.GetFileSystemEntries(Path.GetDirectoryName(SettingsPath)!).ShouldBe([SettingsPath]);
    }

    [Fact]
    public void Read_AFileThatStartsWithAByteOrderMark_ReturnsTheTextWithoutIt()
    {
        // Arrange: older Windows Notepad writes one, and the player may edit the file.
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllBytes(SettingsPath, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("{}")]);
        SettingsFile sut = new(SettingsPath);

        // Act
        string text = sut.Read();

        // Assert
        text.ShouldBe("{}");
    }

    [Fact]
    public void SetAside_WithAFile_MovesItToANewNameNextToIt()
    {
        // Arrange: a name planted in advance must never be written over (ADR-0069 amendment, item 4).
        SettingsFile sut = new(SettingsPath);
        sut.Write(Json);
        string planted = $"{SettingsPath}.broken";
        File.WriteAllText(planted, "planted", new System.Text.UTF8Encoding(false));

        // Act
        sut.SetAside();

        // Assert
        sut.Exists().ShouldBeFalse();
        File.ReadAllText(planted).ShouldBe("planted");
        List<string> setAside = SetAsideCopies();
        setAside.Count.ShouldBe(1);
        File.ReadAllText(setAside[0]).ShouldBe(Json);
    }

    [Fact]
    public void SetAside_TwiceWithAFileEachTime_KeepsBothCopies()
    {
        // Arrange
        SettingsFile sut = new(SettingsPath);
        sut.Write("first");
        sut.SetAside();
        sut.Write("second");

        // Act
        sut.SetAside();

        // Assert
        SetAsideCopies()
            .Select(File.ReadAllText)
            .Order(StringComparer.Ordinal)
            .ShouldBe(["first", "second"]);
    }

    [Fact]
    public void SetAside_WithNoFile_DoesNothing()
    {
        // Arrange
        SettingsFile sut = new(SettingsPath);

        // Act
        sut.SetAside();

        // Assert
        File.Exists($"{SettingsPath}.broken").ShouldBeFalse();
    }

    [Fact]
    public void FileExists_ForAFileOnDisk_ReturnsTrueAndForAMissingOneFalse()
    {
        // Arrange
        Directory.CreateDirectory(_directory);
        string present = Path.Combine(_directory, "client_local_English.dat");
        File.WriteAllText(present, "x");
        FileProbe sut = new();

        // Act
        bool presentExists = sut.FileExists(present);
        bool missingExists = sut.FileExists(Path.Combine(_directory, "missing.dat"));

        // Assert
        presentExists.ShouldBeTrue();
        missingExists.ShouldBeFalse();
    }

    /// <summary>A search pattern would also match the planted <c>config.json.broken</c>, so the prefix is checked by hand.</summary>
    private List<string> SetAsideCopies() =>
        Directory.GetFiles(Path.GetDirectoryName(SettingsPath)!)
            .Where(path => Path.GetFileName(path).StartsWith("config.json.broken.", StringComparison.Ordinal))
            .ToList();
}
