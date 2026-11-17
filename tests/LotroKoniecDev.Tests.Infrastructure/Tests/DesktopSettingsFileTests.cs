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
}
