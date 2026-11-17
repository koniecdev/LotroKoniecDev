using LotroKoniecDev.DesktopApp.Presentation.Settings;
using LotroKoniecDev.DesktopApp.Tests.Unit.Shared;
using LotroKoniecDev.Primitives.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

public sealed class DesktopSettingsStoreTests
{
    private static readonly string GameFolder = ViewModelBuilder.Rooted("Games", "LOTRO");
    private static readonly string TranslationFile = ViewModelBuilder.Rooted("Spolszczenie", "polish.txt");

    private readonly ViewModelBuilder _builder = new();

    [Fact]
    public void Load_WithNoFile_GivesTheDefaultsAndNoNote()
    {
        // Act
        IReadOnlyList<string> notes = _builder.Store.Load();

        // Assert
        notes.ShouldBeEmpty();
        _builder.Store.Current.ShouldBe(DesktopSettings.Default);
        _builder.Store.Current.AutoPatchOnLaunch.ShouldBeTrue();
        _builder.Store.Current.ServerAddress.ShouldBe(TmsConstants.DefaultBaseUrl);
    }

    [Fact]
    public void Load_WithEveryValueSet_ReadsEveryValue()
    {
        // Arrange
        _builder.Files.Add(TranslationFile);
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(
            GameFolder, TranslationFile, autoPatch: false, serverAddress: "https://staging.tms.example.com/");

        // Act
        IReadOnlyList<string> notes = _builder.Store.Load();

        // Assert
        notes.ShouldBeEmpty();
        _builder.Store.Current.ShouldBe(new DesktopSettings(GameFolder, TranslationFile, false, "https://staging.tms.example.com/"));
    }

    [Fact]
    public void Load_WithASavedFolderThatLostItsGame_KeepsTheFolder()
    {
        // Arrange: the first-run panel asks before it replaces the folder, so the load must not drop it.
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(GameFolder);

        // Act
        IReadOnlyList<string> notes = _builder.Store.Load();

        // Assert
        notes.ShouldBeEmpty();
        _builder.Store.Current.GameFolder.ShouldBe(GameFolder);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not json at all")]
    [InlineData("{\"autoPatch\":\"yes\"}")]
    [InlineData("{\"gameFolder\":42}")]
    public void Load_WithAFileThatIsNotTheExpectedJson_GivesTheDefaultsAndSaysSo(string contents)
    {
        // Arrange
        _builder.SettingsFile.Contents = contents;

        // Act
        IReadOnlyList<string> notes = _builder.Store.Load();

        // Assert
        notes.ShouldBe([SettingsTexts.FileBroken]);
        _builder.Store.Current.ShouldBe(DesktopSettings.Default);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void Load_WhenTheFileCannotBeRead_GivesTheDefaultsAndSaysSo(Type failure)
    {
        // Arrange
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(GameFolder);
        _builder.SettingsFile.ReadFailure = (Exception)Activator.CreateInstance(failure)!;

        // Act
        IReadOnlyList<string> notes = _builder.Store.Load();

        // Assert
        notes.ShouldBe([SettingsTexts.FileUnreadable]);
        _builder.Store.Current.ShouldBe(DesktopSettings.Default);
    }

    [Fact]
    public void Load_WithAMissingTranslationFile_DropsOnlyThatValue()
    {
        // Arrange: the player deleted their own polish.txt. That is a normal act, not a broken file.
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(
            GameFolder, TranslationFile, autoPatch: false, serverAddress: "https://staging.tms.example.com/");

        // Act
        IReadOnlyList<string> notes = _builder.Store.Load();

        // Assert
        notes.ShouldBe([SettingsTexts.TranslationFileMissing(TranslationFile)]);
        _builder.Store.Current.ShouldBe(new DesktopSettings(GameFolder, null, false, "https://staging.tms.example.com/"));
    }

    [Theory]
    [InlineData("http://tms.example.com/")]
    [InlineData("ftp://tms.example.com/")]
    [InlineData("tms.example.com")]
    [InlineData("not a url")]
    public void Load_WithARefusedServerAddress_UsesTheDefaultAndKeepsTheRest(string address)
    {
        // Arrange
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(GameFolder, autoPatch: false, serverAddress: address);

        // Act
        IReadOnlyList<string> notes = _builder.Store.Load();

        // Assert
        notes.ShouldBe([SettingsTexts.ServerAddressDropped]);
        _builder.Store.Current.ShouldBe(new DesktopSettings(GameFolder, null, false, TmsConstants.DefaultBaseUrl));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Load_WithABlankServerAddress_UsesTheDefaultWithNoNote(string address)
    {
        // Arrange
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(GameFolder, serverAddress: address);

        // Act
        IReadOnlyList<string> notes = _builder.Store.Load();

        // Assert
        notes.ShouldBeEmpty();
        _builder.Store.Current.ServerAddress.ShouldBe(TmsConstants.DefaultBaseUrl);
    }

    [Fact]
    public void Load_WithARelativeGameFolder_DropsIt()
    {
        // Arrange: the app never writes one, and it would depend on the folder the app starts from.
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(Path.Combine("Games", "LOTRO"));

        // Act
        IReadOnlyList<string> notes = _builder.Store.Load();

        // Assert
        notes.ShouldBe([SettingsTexts.GameFolderDropped]);
        _builder.Store.Current.GameFolder.ShouldBeNull();
    }

    [Fact]
    public void Load_WithARelativeTranslationFile_DropsIt()
    {
        // Arrange
        string relative = Path.Combine("translations", "polish.txt");
        _builder.Files.Add(relative);
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(GameFolder, relative);

        // Act
        IReadOnlyList<string> notes = _builder.Store.Load();

        // Assert
        notes.ShouldBe([SettingsTexts.TranslationFileDropped]);
        _builder.Store.Current.ShouldBe(DesktopSettings.Default with { GameFolder = GameFolder });
    }

    [Fact]
    public void Load_WithSeveralBadValues_GivesOneNoteForEach()
    {
        // Arrange
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(
            Path.Combine("Games", "LOTRO"), TranslationFile, serverAddress: "http://tms.example.com/");

        // Act
        IReadOnlyList<string> notes = _builder.Store.Load();

        // Assert
        notes.ShouldBe(
        [
            SettingsTexts.GameFolderDropped,
            SettingsTexts.TranslationFileMissing(TranslationFile),
            SettingsTexts.ServerAddressDropped
        ]);
    }

    [Fact]
    public void Save_ThenLoad_GivesTheSameSettings()
    {
        // Arrange
        _builder.Files.Add(TranslationFile);
        DesktopSettings settings = new(GameFolder, TranslationFile, false, "https://staging.tms.example.com/");
        _builder.Store.Save(settings);
        DesktopSettingsStore reopened = new(_builder.SettingsFile, _builder.Files, NullLogger<DesktopSettingsStore>.Instance);

        // Act
        IReadOnlyList<string> notes = reopened.Load();

        // Assert
        notes.ShouldBeEmpty();
        reopened.Current.ShouldBe(settings);
    }

    [Fact]
    public void Save_WithTheDefaultServerAddress_LeavesItOutOfTheFile()
    {
        // Act
        _builder.Store.Save(DesktopSettings.Default with { GameFolder = GameFolder });

        // Assert: a later change of the default then reaches every player.
        _builder.SettingsFile.Contents.ShouldNotBeNull().ShouldNotContain("serverAddress");
    }

    [Fact]
    public void Save_WhenTheFileCannotBeWritten_KeepsTheSettingsForThisRunAndReturnsFalse()
    {
        // Arrange
        _builder.SettingsFile.WriteFailure = new IOException("disk full");
        DesktopSettings settings = DesktopSettings.Default with { GameFolder = GameFolder };

        // Act
        bool saved = _builder.Store.Save(settings);

        // Assert
        saved.ShouldBeFalse();
        _builder.Store.Current.ShouldBe(settings);
    }

    [Fact]
    public void Save_Always_TellsTheListeners()
    {
        // Arrange
        int changes = 0;
        _builder.Store.Changed += (_, _) => changes++;

        // Act
        _builder.Store.Save(DesktopSettings.Default with { GameFolder = GameFolder });

        // Assert
        changes.ShouldBe(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ContainsGame_WithNoFolder_ReturnsFalse(string? folder)
    {
        // Act
        bool containsGame = _builder.Store.ContainsGame(folder);

        // Assert
        containsGame.ShouldBeFalse();
    }

    [Fact]
    public void ContainsGame_WithAFolderThatHoldsTheGameFile_ReturnsTrue()
    {
        // Arrange
        _builder.Files.AddGame(GameFolder);

        // Act
        bool containsGame = _builder.Store.ContainsGame(GameFolder);

        // Assert
        containsGame.ShouldBeTrue();
    }

    [Fact]
    public void ContainsGame_WithAFolderWithoutTheGameFile_ReturnsFalse()
    {
        // Act
        bool containsGame = _builder.Store.ContainsGame(GameFolder);

        // Assert
        containsGame.ShouldBeFalse();
    }

    [Fact]
    public void ContainsGame_WithARelativeFolder_ReturnsFalse()
    {
        // Arrange
        string relative = Path.Combine("Games", "LOTRO");
        _builder.Files.AddGame(relative);

        // Act
        bool containsGame = _builder.Store.ContainsGame(relative);

        // Assert
        containsGame.ShouldBeFalse();
    }
}
