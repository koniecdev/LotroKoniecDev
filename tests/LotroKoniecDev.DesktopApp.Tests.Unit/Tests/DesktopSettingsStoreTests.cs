using System.Text.Json.Nodes;
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
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not json at all")]
    [InlineData("42")]
    [InlineData("\"config\"")]
    [InlineData("true")]
    public void Load_WithAFileThatIsNotAJsonObject_GivesTheDefaultsAndSaysSo(string contents)
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
    [InlineData("gameFolder", "42")]
    [InlineData("gameFolder", "[\"LOTRO\"]")]
    [InlineData("translationFile", "true")]
    [InlineData("translationFile", "{}")]
    [InlineData("autoPatch", "\"yes\"")]
    [InlineData("autoPatch", "\"false\"")]
    [InlineData("autoPatch", "0")]
    [InlineData("serverAddress", "443")]
    [InlineData("serverAddress", "[]")]
    public void Load_WithAValueOfTheWrongJsonKind_DropsOnlyThatValue(string field, string wrongValue)
    {
        // Arrange: a hand edit like "autoPatch": "yes" must not cost the player the saved game folder.
        const string ServerAddress = "https://staging.tms.example.com/";
        _builder.Files.Add(TranslationFile);
        JsonObject document = JsonNode.Parse(ViewModelBuilder.SettingsJson(
            GameFolder, TranslationFile, autoPatch: false, serverAddress: ServerAddress))!.AsObject();
        document[field] = JsonNode.Parse(wrongValue);
        _builder.SettingsFile.Contents = document.ToJsonString();
        DesktopSettings everyValue = new(GameFolder, TranslationFile, false, ServerAddress);
        (DesktopSettings expected, string expectedNote) = field switch
        {
            "gameFolder" => (everyValue with { GameFolder = null }, SettingsTexts.GameFolderDropped),
            "translationFile" => (everyValue with { TranslationFileOverride = null }, SettingsTexts.TranslationFileDropped),
            "autoPatch" => (everyValue with { AutoPatchOnLaunch = true }, SettingsTexts.AutoPatchDropped),
            _ => (everyValue with { ServerAddress = TmsConstants.DefaultBaseUrl }, SettingsTexts.ServerAddressDropped)
        };

        // Act
        IReadOnlyList<string> notes = _builder.Store.Load();

        // Assert
        notes.ShouldBe([expectedNote]);
        _builder.Store.Current.ShouldBe(expected);
    }

    [Theory]
    [InlineData("{\"gameFolder\":\"\\uD800\",\"autoPatch\":false}")]
    [InlineData("{\"translationFile\":\"a\\uDC00b\",\"autoPatch\":false}")]
    [InlineData("{\"serverAddress\":\"https://\\uD800.example.com/\",\"autoPatch\":false}")]
    [InlineData("{\"game\\uD800Folde\":1,\"autoPatch\":false}")]
    [InlineData("{\"auto\\uD800Patc\":1}")]
    public void Load_WithAnEscapeThatIsNotText_GivesTheDefaultsAndSaysSo(string contents)
    {
        // Arrange: a lone surrogate escape parses, but no value or field name can be read from it.
        _builder.SettingsFile.Contents = contents;

        // Act
        IReadOnlyList<string> notes = _builder.Store.Load();

        // Assert
        notes.ShouldBe([SettingsTexts.FileBroken]);
        _builder.Store.Current.ShouldBe(DesktopSettings.Default);
    }

    [Fact]
    public void Save_AfterAnEscapeThatIsNotText_SetsTheOldFileAsideFirst()
    {
        // Arrange
        const string Contents = "{\"gameFolder\":\"\\uD800\",\"autoPatch\":false}";
        _builder.SettingsFile.Contents = Contents;
        _builder.Store.Load();

        // Act
        bool written = _builder.Store.Save(DesktopSettings.Default with { GameFolder = GameFolder });

        // Assert
        written.ShouldBeTrue();
        _builder.SettingsFile.SetAsideContents.ShouldBe(Contents);
    }

    [Fact]
    public void Load_WithAnEmptyObject_GivesTheDefaultsWithNoNote()
    {
        // Arrange
        _builder.SettingsFile.Contents = "{}";

        // Act
        IReadOnlyList<string> notes = _builder.Store.Load();

        // Assert
        notes.ShouldBeEmpty();
        _builder.Store.Current.ShouldBe(DesktopSettings.Default);
    }

    [Fact]
    public void Load_WithAJsonNullForEveryValue_ReadsThemAsNotSetWithNoNote()
    {
        // Arrange
        _builder.SettingsFile.Contents =
            "{\"gameFolder\":null,\"translationFile\":null,\"autoPatch\":null,\"serverAddress\":null}";

        // Act
        IReadOnlyList<string> notes = _builder.Store.Load();

        // Assert
        notes.ShouldBeEmpty();
        _builder.Store.Current.ShouldBe(DesktopSettings.Default);
    }

    [Fact]
    public void Load_AfterAFileThatIsNotAJsonObject_SetsItAsideOnTheNextSave()
    {
        // Arrange
        _builder.SettingsFile.Contents = "[]";
        _builder.Store.Load();

        // Act
        bool written = _builder.Store.Save(DesktopSettings.Default with { GameFolder = GameFolder });

        // Assert
        written.ShouldBeTrue();
        _builder.SettingsFile.SetAsideContents.ShouldBe("[]");
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
    public void Load_WithEmptyPaths_ReadsThemAsNotSetWithNoNote()
    {
        // Arrange
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(string.Empty, string.Empty);

        // Act
        IReadOnlyList<string> notes = _builder.Store.Load();

        // Assert
        notes.ShouldBeEmpty();
        _builder.Store.Current.ShouldBe(DesktopSettings.Default);
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
    public void Save_WithPolishLettersInAPath_WritesThemAsTheyAre()
    {
        // Arrange
        string folder = ViewModelBuilder.Rooted("Gry", "Władca Pierścieni");

        // Act
        _builder.Store.Save(DesktopSettings.Default with { GameFolder = folder });

        // Assert
        _builder.SettingsFile.Contents.ShouldNotBeNull();
        _builder.SettingsFile.Contents.ShouldContain("Władca Pierścieni");
    }

    [Fact]
    public void Save_TheDefaultsWithAFolder_ThenLoad_GivesTheSameSettings()
    {
        // Arrange: the file the app writes most often leaves out the override and the default address,
        // and has auto-patch on.
        DesktopSettings settings = DesktopSettings.Default with { GameFolder = GameFolder };
        _builder.Store.Save(settings);
        DesktopSettingsStore reopened = new(_builder.SettingsFile, _builder.Files, NullLogger<DesktopSettingsStore>.Instance);

        // Act
        IReadOnlyList<string> notes = reopened.Load();

        // Assert
        notes.ShouldBeEmpty();
        reopened.Current.ShouldBe(settings);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_AfterALoadThatCouldNotUseTheFile_SetsTheOldFileAsideFirst(bool unreadable)
    {
        // Arrange: one typo from a hand edit must not cost the player the rest of their settings.
        const string broken = "{\"gameFolder\": \"C:\\\\Gry\", \"autoPatch\": nie}";
        _builder.SettingsFile.Contents = broken;
        if (unreadable)
        {
            _builder.SettingsFile.ReadFailure = new IOException("locked");
        }

        _builder.Store.Load();
        _builder.SettingsFile.ReadFailure = null;

        // Act
        bool saved = _builder.Store.Save(DesktopSettings.Default with { GameFolder = GameFolder });

        // Assert
        saved.ShouldBeTrue();
        _builder.SettingsFile.SetAsideContents.ShouldBe(broken);
        _builder.SettingsFile.Contents.ShouldNotBeNull().ShouldContain("gameFolder");
    }

    [Fact]
    public void Save_WhenTheOldFileCannotBeSetAside_LeavesItAndReturnsFalse()
    {
        // Arrange
        _builder.SettingsFile.Contents = "{";
        _builder.Store.Load();
        _builder.SettingsFile.SetAsideFailure = new IOException("locked");
        DesktopSettings settings = DesktopSettings.Default with { GameFolder = GameFolder };

        // Act
        bool saved = _builder.Store.Save(settings);

        // Assert
        saved.ShouldBeFalse();
        _builder.SettingsFile.Contents.ShouldBe("{");
        _builder.Store.Current.ShouldBe(settings);
    }

    [Fact]
    public void Save_AgainAfterTheSetAsideFailed_StillSetsTheOldFileAsideFirst()
    {
        // Arrange: a second click on Zapisz while the old file is still locked must not wipe it.
        _builder.SettingsFile.Contents = "{";
        _builder.Store.Load();
        _builder.SettingsFile.SetAsideFailure = new IOException("locked");
        DesktopSettings settings = DesktopSettings.Default with { GameFolder = GameFolder };
        _builder.Store.Save(settings);
        bool secondTry = _builder.Store.Save(settings);
        _builder.SettingsFile.SetAsideFailure = null;

        // Act
        bool thirdTry = _builder.Store.Save(settings);

        // Assert
        secondTry.ShouldBeFalse();
        thirdTry.ShouldBeTrue();
        _builder.SettingsFile.SetAsideContents.ShouldBe("{");
    }

    [Fact]
    public void Save_WhenTheOldFileIsSetAsideButTheWriteFails_KeepsTheOldFileAsideAndReturnsFalse()
    {
        // Arrange
        _builder.SettingsFile.Contents = "{";
        _builder.Store.Load();
        _builder.SettingsFile.WriteFailure = new IOException("disk full");

        // Act
        bool saved = _builder.Store.Save(DesktopSettings.Default with { GameFolder = GameFolder });

        // Assert
        saved.ShouldBeFalse();
        _builder.SettingsFile.SetAsideContents.ShouldBe("{");
    }

    [Fact]
    public void Save_ASecondTimeAfterTheOldFileWasSetAside_KeepsTheSetAsideCopy()
    {
        // Arrange
        _builder.SettingsFile.Contents = "{";
        _builder.Store.Load();
        _builder.Store.Save(DesktopSettings.Default with { GameFolder = GameFolder });

        // Act
        _builder.Store.Save(DesktopSettings.Default with { GameFolder = GameFolder, AutoPatchOnLaunch = false });

        // Assert
        _builder.SettingsFile.SetAsideContents.ShouldBe("{");
    }

    [Fact]
    public void Save_AfterAGoodLoad_SetsNothingAside()
    {
        // Arrange
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(GameFolder);
        _builder.Store.Load();

        // Act
        _builder.Store.Save(DesktopSettings.Default with { GameFolder = GameFolder, AutoPatchOnLaunch = false });

        // Assert
        _builder.SettingsFile.SetAsideContents.ShouldBeNull();
    }

    [Fact]
    public void Save_ThatWorks_TellsTheListenersItWasWritten()
    {
        // Arrange
        List<SettingsSavedEventArgs> saves = [];
        _builder.Store.Saved += (_, e) => saves.Add(e);

        // Act
        _builder.Store.Save(DesktopSettings.Default with { GameFolder = GameFolder });

        // Assert
        saves.Select(e => (e.Written, e.FileSetAside)).ShouldBe([(true, false)]);
    }

    [Fact]
    public void Save_ThatFails_TellsTheListenersAndTheSettingsInUseAreAlreadyNew()
    {
        // Arrange
        _builder.SettingsFile.WriteFailure = new IOException("disk full");
        DesktopSettings settings = DesktopSettings.Default with { GameFolder = GameFolder };
        List<(bool Written, DesktopSettings Current)> saves = [];
        _builder.Store.Saved += (_, e) => saves.Add((e.Written, _builder.Store.Current));

        // Act
        _builder.Store.Save(settings);

        // Assert
        saves.ShouldBe([(false, settings)]);
    }

    [Fact]
    public void Save_AfterALoadThatCouldNotUseTheFile_TellsTheListenersTheFileWasSetAside()
    {
        // Arrange
        _builder.SettingsFile.Contents = "{";
        _builder.Store.Load();
        List<SettingsSavedEventArgs> saves = [];
        _builder.Store.Saved += (_, e) => saves.Add(e);

        // Act
        _builder.Store.Save(DesktopSettings.Default with { GameFolder = GameFolder });

        // Assert
        saves.Select(e => (e.Written, e.FileSetAside)).ShouldBe([(true, true)]);
    }

    [Fact]
    public void Save_AfterAGoodLoadThatFollowedABadOne_SetsNothingAside()
    {
        // Arrange: the file was fixed between two loads, so the healthy file must stay.
        _builder.SettingsFile.Contents = "{";
        _builder.Store.Load();
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(GameFolder);
        _builder.Store.Load();

        // Act
        _builder.Store.Save(DesktopSettings.Default with { GameFolder = GameFolder, AutoPatchOnLaunch = false });

        // Assert
        _builder.SettingsFile.SetAsideContents.ShouldBeNull();
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
