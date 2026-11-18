using LotroKoniecDev.DesktopApp.Presentation.Settings;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using LotroKoniecDev.DesktopApp.Tests.Unit.Shared;
using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

public sealed class SettingsViewModelTests
{
    private static readonly string GameFolder = ViewModelBuilder.Rooted("Games", "LOTRO");
    private static readonly string TranslationFile = ViewModelBuilder.Rooted("Spolszczenie", "polish.txt");

    private readonly ViewModelBuilder _builder = new();

    [Fact]
    public void BeginEdit_WithSettingsInUse_FillsEveryField()
    {
        // Arrange
        _builder.Files.AddGame(GameFolder);
        _builder.Store.Save(new DesktopSettings(GameFolder, TranslationFile, false, "https://staging.tms.example.com/"));
        SettingsViewModel viewModel = _builder.Settings();

        // Act
        viewModel.BeginEdit();

        // Assert
        viewModel.GameFolder.ShouldBe(GameFolder);
        viewModel.TranslationFile.ShouldBe(TranslationFile);
        viewModel.AutoPatchOnLaunch.ShouldBeFalse();
        viewModel.ServerAddress.ShouldBe("https://staging.tms.example.com/");
        viewModel.IsErrorVisible.ShouldBeFalse();
    }

    [Fact]
    public void BeginEdit_OnAFirstRun_ShowsTheDefaults()
    {
        // Arrange
        SettingsViewModel viewModel = _builder.Settings();

        // Act
        viewModel.BeginEdit();

        // Assert
        viewModel.GameFolder.ShouldBeEmpty();
        viewModel.TranslationFile.ShouldBeEmpty();
        viewModel.AutoPatchOnLaunch.ShouldBeTrue();
        viewModel.ServerAddress.ShouldBe(TmsConstants.DefaultBaseUrl);
    }

    [Fact]
    public void Save_WithValidValues_SavesThemAndAsksToClose()
    {
        // Arrange
        _builder.Files.AddGame(GameFolder);
        _builder.Files.Add(TranslationFile);
        SettingsViewModel viewModel = Editing(GameFolder, TranslationFile, autoPatch: false, "https://staging.tms.example.com/");
        int closeRequests = 0;
        viewModel.CloseRequested += (_, _) => closeRequests++;

        // Act
        viewModel.SaveCommand.Execute(null);

        // Assert
        _builder.Store.Current.ShouldBe(new DesktopSettings(GameFolder, TranslationFile, false, "https://staging.tms.example.com/"));
        _builder.SettingsFile.Contents.ShouldNotBeNull();
        closeRequests.ShouldBe(1);
    }

    [Fact]
    public void Save_WithSpacesAroundTheValues_SavesThemTrimmed()
    {
        // Arrange
        _builder.Files.AddGame(GameFolder);
        SettingsViewModel viewModel = Editing($"  {GameFolder}  ", "   ", autoPatch: true, " https://staging.tms.example.com/ ");

        // Act
        bool saved = viewModel.Save();

        // Assert
        saved.ShouldBeTrue();
        _builder.Store.Current.ShouldBe(new DesktopSettings(GameFolder, null, true, "https://staging.tms.example.com/"));
    }

    [Theory]
    [InlineData("\"{0}\"")]
    [InlineData("  \"{0}\"  ")]
    [InlineData("\" {0} \"")]
    public void Save_WithPathsInQuotes_SavesThemWithoutTheQuotes(string format)
    {
        // Arrange: Explorer's "Copy as path" puts quotes around the path.
        _builder.Files.AddGame(GameFolder);
        _builder.Files.Add(TranslationFile);
        SettingsViewModel viewModel = Editing(
            string.Format(System.Globalization.CultureInfo.InvariantCulture, format, GameFolder),
            string.Format(System.Globalization.CultureInfo.InvariantCulture, format, TranslationFile),
            autoPatch: true,
            string.Empty);

        // Act
        bool saved = viewModel.Save();

        // Assert
        saved.ShouldBeTrue();
        _builder.Store.Current.GameFolder.ShouldBe(GameFolder);
        _builder.Store.Current.TranslationFileOverride.ShouldBe(TranslationFile);
    }

    [Fact]
    public void Save_WithOnlyTwoQuotesForTheTranslationFile_SavesNoOverride()
    {
        // Arrange
        _builder.Files.AddGame(GameFolder);
        SettingsViewModel viewModel = Editing(GameFolder, "\"\"", autoPatch: true, string.Empty);

        // Act
        bool saved = viewModel.Save();

        // Assert
        saved.ShouldBeTrue();
        _builder.Store.Current.TranslationFileOverride.ShouldBeNull();
    }

    [Fact]
    public void Save_WithOneQuoteOnly_KeepsItAndRefusesThePath()
    {
        // Arrange
        _builder.Files.AddGame(GameFolder);
        SettingsViewModel viewModel = Editing($"\"{GameFolder}", string.Empty, autoPatch: true, string.Empty);

        // Act
        bool saved = viewModel.Save();

        // Assert
        saved.ShouldBeFalse();
        _builder.SettingsFile.Contents.ShouldBeNull();
    }

    [Fact]
    public void Save_WithNoGameFolder_RefusesAndSaysWhatIsMissing()
    {
        // Arrange
        SettingsViewModel viewModel = Editing(string.Empty, string.Empty, autoPatch: true, string.Empty);

        // Act
        bool saved = viewModel.Save();

        // Assert
        saved.ShouldBeFalse();
        viewModel.ErrorText.ShouldBe(SettingsTexts.NoGameFolder);
        _builder.SettingsFile.Contents.ShouldBeNull();
    }

    [Fact]
    public void Save_WithAFolderWithoutTheGame_RefusesIt()
    {
        // Arrange
        SettingsViewModel viewModel = Editing(GameFolder, string.Empty, autoPatch: true, string.Empty);

        // Act
        bool saved = viewModel.Save();

        // Assert
        saved.ShouldBeFalse();
        viewModel.ErrorText.ShouldBe(SettingsTexts.NoGameIn(GameFolder));
        _builder.SettingsFile.Contents.ShouldBeNull();
    }

    [Fact]
    public void Save_WithARelativeFolder_RefusesIt()
    {
        // Arrange
        string relative = Path.Combine("Games", "LOTRO");
        _builder.Files.AddGame(relative);
        SettingsViewModel viewModel = Editing(relative, string.Empty, autoPatch: true, string.Empty);

        // Act
        bool saved = viewModel.Save();

        // Assert
        saved.ShouldBeFalse();
        _builder.SettingsFile.Contents.ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Save_WithATranslationFileThatIsNotThere_RefusesIt(bool relative)
    {
        // Arrange
        _builder.Files.AddGame(GameFolder);
        string path = relative ? Path.Combine("translations", "polish.txt") : TranslationFile;
        if (relative)
        {
            _builder.Files.Add(path);
        }

        SettingsViewModel viewModel = Editing(GameFolder, path, autoPatch: true, string.Empty);

        // Act
        bool saved = viewModel.Save();

        // Assert
        saved.ShouldBeFalse();
        viewModel.ErrorText.ShouldBe(SettingsTexts.TranslationFileMissingOnSave);
        _builder.SettingsFile.Contents.ShouldBeNull();
    }

    [Theory]
    [InlineData("http://tms.example.com/")]
    [InlineData("ftp://tms.example.com/")]
    [InlineData("tms.example.com")]
    public void Save_WithARefusedServerAddress_RefusesIt(string address)
    {
        // Arrange
        _builder.Files.AddGame(GameFolder);
        SettingsViewModel viewModel = Editing(GameFolder, string.Empty, autoPatch: true, address);

        // Act
        bool saved = viewModel.Save();

        // Assert
        saved.ShouldBeFalse();
        viewModel.ErrorText.ShouldBe(SettingsTexts.ServerAddressRefused);
        _builder.SettingsFile.Contents.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Save_WithAnEmptyServerAddress_UsesTheDefault(string address)
    {
        // Arrange: the player never has to type an address (wiki, Wersje systemu).
        _builder.Files.AddGame(GameFolder);
        SettingsViewModel viewModel = Editing(GameFolder, string.Empty, autoPatch: true, address);

        // Act
        bool saved = viewModel.Save();

        // Assert
        saved.ShouldBeTrue();
        _builder.Store.Current.ServerAddress.ShouldBe(TmsConstants.DefaultBaseUrl);
    }

    [Fact]
    public void Save_WhenTheFileCannotBeWritten_SaysSoAndStaysOpen()
    {
        // Arrange
        _builder.Files.AddGame(GameFolder);
        _builder.SettingsFile.WriteFailure = new UnauthorizedAccessException();
        SettingsViewModel viewModel = Editing(GameFolder, string.Empty, autoPatch: true, string.Empty);
        int closeRequests = 0;
        viewModel.CloseRequested += (_, _) => closeRequests++;

        // Act
        bool saved = viewModel.Save();

        // Assert
        saved.ShouldBeFalse();
        viewModel.ErrorText.ShouldBe(SettingsTexts.SaveFailed);
        closeRequests.ShouldBe(0);
    }

    [Fact]
    public void Save_AfterARefusal_ClearsTheError()
    {
        // Arrange
        SettingsViewModel viewModel = Editing(GameFolder, string.Empty, autoPatch: true, string.Empty);
        viewModel.Save();
        _builder.Files.AddGame(GameFolder);

        // Act
        bool saved = viewModel.Save();

        // Assert
        saved.ShouldBeTrue();
        viewModel.IsErrorVisible.ShouldBeFalse();
    }

    [Fact]
    public void Cancel_Always_AsksToCloseAndSavesNothing()
    {
        // Arrange
        _builder.Files.AddGame(GameFolder);
        SettingsViewModel viewModel = Editing(GameFolder, string.Empty, autoPatch: false, string.Empty);
        int closeRequests = 0;
        viewModel.CloseRequested += (_, _) => closeRequests++;

        // Act
        viewModel.CancelCommand.Execute(null);

        // Assert
        closeRequests.ShouldBe(1);
        _builder.Store.Current.ShouldBe(DesktopSettings.Default);
        _builder.SettingsFile.Contents.ShouldBeNull();
    }

    [Fact]
    public async Task BrowseGameFolderAsync_WithAPick_FillsTheField()
    {
        // Arrange
        SettingsViewModel viewModel = _builder.PickingFolder(GameFolder).Settings();

        // Act
        await viewModel.BrowseGameFolderAsync();

        // Assert
        viewModel.GameFolder.ShouldBe(GameFolder);
    }

    [Fact]
    public async Task BrowseGameFolderAsync_WhenThePlayerCancels_KeepsTheField()
    {
        // Arrange
        SettingsViewModel viewModel = _builder.PickingFolder(null).Settings();
        viewModel.GameFolder = GameFolder;

        // Act
        await viewModel.BrowseGameFolderAsync();

        // Assert
        viewModel.GameFolder.ShouldBe(GameFolder);
    }

    [Fact]
    public async Task BrowseGameFolderAsync_WhileADialogIsOpen_DisablesTheButtonsThatWouldOpenAnother()
    {
        // Arrange
        TaskCompletionSource<string?> dialog = new();
        _builder.Picker.PickGameFolderAsync().Returns(dialog.Task);
        SettingsViewModel viewModel = _builder.Settings();
        Task first = viewModel.BrowseGameFolderAsync();

        // Act
        (bool Folder, bool File, bool Save, bool Cancel) enabled = (
            viewModel.BrowseGameFolderCommand.CanExecute(null),
            viewModel.BrowseTranslationFileCommand.CanExecute(null),
            viewModel.SaveCommand.CanExecute(null),
            viewModel.CancelCommand.CanExecute(null));
        await viewModel.BrowseTranslationFileAsync();
        dialog.SetResult(GameFolder);
        await first;

        // Assert
        enabled.ShouldBe((false, false, false, true));
        await _builder.Picker.DidNotReceive().PickTranslationFileAsync();
        viewModel.GameFolder.ShouldBe(GameFolder);
        viewModel.SaveCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task BrowseTranslationFileAsync_WithAPick_FillsTheField()
    {
        // Arrange
        _builder.Picker.PickTranslationFileAsync().Returns(TranslationFile);
        SettingsViewModel viewModel = _builder.Settings();

        // Act
        await viewModel.BrowseTranslationFileAsync();

        // Assert
        viewModel.TranslationFile.ShouldBe(TranslationFile);
    }

    [Fact]
    public async Task BrowseTranslationFileAsync_WhenThePickerThrows_SaysSo()
    {
        // Arrange
        _builder.Picker.PickTranslationFileAsync().Returns(Task.FromException<string?>(new InvalidOperationException("no window")));
        SettingsViewModel viewModel = _builder.Settings();

        // Act
        await viewModel.BrowseTranslationFileAsync();

        // Assert
        viewModel.ErrorText.ShouldBe(SettingsTexts.PickerFailed);
        viewModel.BrowseTranslationFileCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void Save_WhileAnotherOperationHoldsTheGate_SavesNothingAndSaysWhy()
    {
        // Arrange: a patch is running, so the game folder must not change under it (#42).
        _builder.Files.AddGame(GameFolder);
        SettingsViewModel viewModel = Editing(GameFolder, string.Empty, true, string.Empty);
        _builder.Gate.TryEnter();

        // Act
        bool saved = viewModel.Save();

        // Assert: the button stays enabled, so a click shows this reason instead of doing nothing.
        saved.ShouldBeFalse();
        viewModel.ErrorText.ShouldBe("Trwa inna operacja. Zapisz, gdy się skończy.");
        viewModel.SaveCommand.CanExecute(null).ShouldBeTrue();
        _builder.SettingsFile.Contents.ShouldBeNull();
    }

    [Fact]
    public void Save_WhenTheGateIsFree_TakesItOnlyForTheSave()
    {
        // Arrange
        _builder.Files.AddGame(GameFolder);
        SettingsViewModel viewModel = Editing(GameFolder, string.Empty, true, string.Empty);
        List<bool> gateStates = [];
        _builder.Store.Saved += (_, _) => gateStates.Add(_builder.Gate.IsBusy);

        // Act
        bool saved = viewModel.Save();

        // Assert
        saved.ShouldBeTrue();
        gateStates.ShouldBe([true]);
        _builder.Gate.IsBusy.ShouldBeFalse();
    }

    private SettingsViewModel Editing(string gameFolder, string translationFile, bool autoPatch, string serverAddress)
    {
        SettingsViewModel viewModel = _builder.Settings();
        viewModel.BeginEdit();
        viewModel.GameFolder = gameFolder;
        viewModel.TranslationFile = translationFile;
        viewModel.AutoPatchOnLaunch = autoPatch;
        viewModel.ServerAddress = serverAddress;
        return viewModel;
    }
}
