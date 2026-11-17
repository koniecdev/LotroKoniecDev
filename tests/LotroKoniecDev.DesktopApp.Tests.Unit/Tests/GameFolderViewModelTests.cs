using LotroKoniecDev.DesktopApp.Presentation.Settings;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using LotroKoniecDev.DesktopApp.Tests.Unit.Shared;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

/// <summary>
/// The first run (#43) and the guard ADR-0069 §2 leaves: a folder from outside the allow-list is
/// never used until the player confirms it or picks it.
/// </summary>
public sealed class GameFolderViewModelTests
{
    private static readonly string KnownFolder = ViewModelBuilder.Rooted("Program Files (x86)", "StandingStoneGames", "LOTRO");
    private static readonly string ScannedFolder = ViewModelBuilder.Rooted("Gry", "LOTRO");
    private static readonly string PickedFolder = ViewModelBuilder.Rooted("Moje gry", "LOTRO");

    private readonly ViewModelBuilder _builder = new();

    [Fact]
    public async Task StartAsync_WithASavedFolderThatHoldsTheGame_HidesThePanelAndDoesNotSearch()
    {
        // Arrange
        ViewModelBuilder builder = ViewModelBuilder.WithASavedGame();
        GameFolderViewModel viewModel = builder.GameFolder();

        // Act
        await viewModel.StartAsync();

        // Assert: a full search can walk every drive, so it must not run when nothing needs it.
        viewModel.IsPanelVisible.ShouldBeFalse();
        builder.Locator.DidNotReceive().LocateAll(Arg.Any<Action<string>?>());
    }

    [Theory]
    [InlineData(DatFileSource.StandingStoneGames)]
    [InlineData(DatFileSource.Steam)]
    [InlineData(DatFileSource.Registry)]
    public async Task StartAsync_WithOneKnownInstall_SavesItWithoutAQuestion(DatFileSource source)
    {
        // Arrange
        GameFolderViewModel viewModel = _builder.Locating(ViewModelBuilder.Location(KnownFolder, source)).GameFolder();

        // Act
        await viewModel.StartAsync();

        // Assert
        viewModel.IsPanelVisible.ShouldBeFalse();
        _builder.Store.Current.GameFolder.ShouldBe(KnownFolder);
        _builder.SettingsFile.Contents.ShouldNotBeNull().ShouldContain("gameFolder");
    }

    [Theory]
    [InlineData(DatFileSource.DiskScan)]
    [InlineData(DatFileSource.LocalFallback)]
    [InlineData((DatFileSource)99)]
    public async Task StartAsync_WithOneFolderOutsideTheAllowList_AsksBeforeUsingIt(DatFileSource source)
    {
        // Arrange
        GameFolderViewModel viewModel = _builder.Locating(ViewModelBuilder.Location(ScannedFolder, source)).GameFolder();

        // Act
        await viewModel.StartAsync();

        // Assert
        viewModel.IsPanelVisible.ShouldBeTrue();
        viewModel.IsQuestionVisible.ShouldBeTrue();
        viewModel.PromptText.ShouldBe(GameFolderTexts.FoundOne(ScannedFolder, source));
        viewModel.PromptText.ShouldNotBeNull().ShouldContain(GameFolderTexts.Unchecked);
        _builder.Store.Current.GameFolder.ShouldBeNull();
        _builder.SettingsFile.Contents.ShouldBeNull();
    }

    [Fact]
    public async Task Confirm_AfterTheQuestion_SavesTheFolderAndHidesThePanel()
    {
        // Arrange
        GameFolderViewModel viewModel = _builder.Locating(ViewModelBuilder.Location(ScannedFolder, DatFileSource.DiskScan)).GameFolder();
        await viewModel.StartAsync();

        // Act
        viewModel.ConfirmCommand.Execute(null);

        // Assert
        viewModel.IsPanelVisible.ShouldBeFalse();
        _builder.Store.Current.GameFolder.ShouldBe(ScannedFolder);
    }

    [Fact]
    public async Task Decline_AfterTheQuestion_SavesNothingAndAsksForTheFolder()
    {
        // Arrange
        GameFolderViewModel viewModel = _builder.Locating(ViewModelBuilder.Location(ScannedFolder, DatFileSource.DiskScan)).GameFolder();
        await viewModel.StartAsync();

        // Act
        viewModel.DeclineCommand.Execute(null);

        // Assert
        viewModel.IsPanelVisible.ShouldBeTrue();
        viewModel.IsQuestionVisible.ShouldBeFalse();
        viewModel.PromptText.ShouldBe(GameFolderTexts.ChooseByHand);
        viewModel.ConfirmCommand.CanExecute(null).ShouldBeFalse();
        _builder.SettingsFile.Contents.ShouldBeNull();
    }

    [Fact]
    public async Task StartAsync_WithSeveralInstalls_ListsThemAndWarnsOnlyWhereTheGuardAsks()
    {
        // Arrange
        GameFolderViewModel viewModel = _builder.Locating(
            ViewModelBuilder.Location(KnownFolder, DatFileSource.StandingStoneGames),
            ViewModelBuilder.Location(ScannedFolder, DatFileSource.DiskScan)).GameFolder();

        // Act
        await viewModel.StartAsync();

        // Assert: even a known install waits for the player when there is a choice.
        viewModel.PromptText.ShouldBe(GameFolderTexts.SeveralFound);
        viewModel.IsCandidateListVisible.ShouldBeTrue();
        viewModel.Candidates.Select(candidate => (candidate.Folder, candidate.HasWarning)).ShouldBe(
        [
            (KnownFolder, false),
            (ScannedFolder, true)
        ]);
        viewModel.Candidates[1].Warning.ShouldBe(GameFolderTexts.CandidateWarning);
        _builder.SettingsFile.Contents.ShouldBeNull();
    }

    [Fact]
    public async Task ChooseCommand_OnAListedInstall_SavesItsFolder()
    {
        // Arrange
        GameFolderViewModel viewModel = _builder.Locating(
            ViewModelBuilder.Location(KnownFolder, DatFileSource.StandingStoneGames),
            ViewModelBuilder.Location(ScannedFolder, DatFileSource.DiskScan)).GameFolder();
        await viewModel.StartAsync();

        // Act: picking an entry from the list is the player's confirmation (ADR-0069 §2).
        viewModel.Candidates[1].ChooseCommand.Execute(null);

        // Assert
        viewModel.IsPanelVisible.ShouldBeFalse();
        _builder.Store.Current.GameFolder.ShouldBe(ScannedFolder);
    }

    [Fact]
    public async Task StartAsync_WithNoInstallFound_AsksForTheFolder()
    {
        // Arrange
        GameFolderViewModel viewModel = _builder.GameFolder();

        // Act
        await viewModel.StartAsync();

        // Assert
        viewModel.IsPanelVisible.ShouldBeTrue();
        viewModel.PromptText.ShouldBe(GameFolderTexts.NotFound);
        viewModel.IsQuestionVisible.ShouldBeFalse();
        viewModel.IsCandidateListVisible.ShouldBeFalse();
    }

    [Fact]
    public async Task StartAsync_WhenTheSearchThrows_AsksForTheFolder()
    {
        // Arrange
        _builder.Locator.LocateAll(Arg.Any<Action<string>?>()).Returns(_ => throw new InvalidOperationException("bug"));
        GameFolderViewModel viewModel = _builder.GameFolder();

        // Act
        await viewModel.StartAsync();

        // Assert
        viewModel.PromptText.ShouldBe(GameFolderTexts.NotFound);
        viewModel.ChooseFolderCommand.CanExecute(null).ShouldBeTrue();
    }

    [Theory]
    [InlineData(DatFileSource.StandingStoneGames, false)]
    [InlineData(DatFileSource.DiskScan, true)]
    public async Task StartAsync_WithALostFolderAndOneInstall_AsksBeforeReplacingIt(DatFileSource source, bool needsConfirmation)
    {
        // Arrange: a drive that is only missing for a while must not lose the player's folder.
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(PickedFolder);
        string before = _builder.SettingsFile.Contents;
        GameFolderViewModel viewModel = _builder.Locating(ViewModelBuilder.Location(ScannedFolder, source)).GameFolder();

        // Act
        await viewModel.StartAsync();

        // Assert
        viewModel.IsQuestionVisible.ShouldBeTrue();
        viewModel.PromptText.ShouldBe(GameFolderTexts.FoundInstead(PickedFolder, ScannedFolder, source, needsConfirmation));
        _builder.SettingsFile.Contents.ShouldBe(before);
    }

    [Fact]
    public async Task StartAsync_WithALostFolderAndSeveralInstalls_NamesTheLostFolderAboveTheList()
    {
        // Arrange
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(PickedFolder);
        GameFolderViewModel viewModel = _builder.Locating(
            ViewModelBuilder.Location(KnownFolder, DatFileSource.StandingStoneGames),
            ViewModelBuilder.Location(ScannedFolder, DatFileSource.DiskScan)).GameFolder();

        // Act
        await viewModel.StartAsync();

        // Assert
        viewModel.PromptText.ShouldBe(GameFolderTexts.SeveralFoundInstead(PickedFolder));
        viewModel.Candidates.Count.ShouldBe(2);
    }

    [Fact]
    public async Task StartAsync_WithALostFolderAndNoInstall_NamesTheLostFolder()
    {
        // Arrange
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(PickedFolder);
        GameFolderViewModel viewModel = _builder.GameFolder();

        // Act
        await viewModel.StartAsync();

        // Assert
        viewModel.PromptText.ShouldBe(GameFolderTexts.LostAndNotFound(PickedFolder));
        _builder.Store.Current.GameFolder.ShouldBe(PickedFolder);
    }

    [Fact]
    public async Task Decline_WithALostFolder_LeavesTheSavedFolderInTheFile()
    {
        // Arrange
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(PickedFolder);
        string before = _builder.SettingsFile.Contents;
        GameFolderViewModel viewModel = _builder.Locating(ViewModelBuilder.Location(KnownFolder, DatFileSource.StandingStoneGames)).GameFolder();
        await viewModel.StartAsync();

        // Act
        viewModel.DeclineCommand.Execute(null);

        // Assert
        viewModel.PromptText.ShouldBe(GameFolderTexts.Lost(PickedFolder));
        _builder.SettingsFile.Contents.ShouldBe(before);
    }

    [Fact]
    public async Task ChooseFolderAsync_WithAFolderThatHoldsTheGame_SavesItWithoutAQuestion()
    {
        // Arrange
        _builder.Files.AddGame(PickedFolder);
        GameFolderViewModel viewModel = _builder.PickingFolder(PickedFolder).GameFolder();
        await viewModel.StartAsync();

        // Act: a folder the player picks counts like the CLI's -d (ADR-0069 §2).
        await viewModel.ChooseFolderAsync();

        // Assert
        viewModel.IsPanelVisible.ShouldBeFalse();
        _builder.Store.Current.GameFolder.ShouldBe(PickedFolder);
    }

    [Fact]
    public async Task ChooseFolderAsync_WithAFolderWithoutTheGame_RefusesIt()
    {
        // Arrange
        GameFolderViewModel viewModel = _builder.PickingFolder(PickedFolder).GameFolder();
        await viewModel.StartAsync();

        // Act
        await viewModel.ChooseFolderAsync();

        // Assert
        viewModel.IsPanelVisible.ShouldBeTrue();
        viewModel.PromptText.ShouldBe(SettingsTexts.NoGameIn(PickedFolder));
        _builder.Store.Current.GameFolder.ShouldBeNull();
        _builder.SettingsFile.Contents.ShouldBeNull();
    }

    [Fact]
    public async Task ChooseFolderAsync_WhenThePlayerCancels_KeepsTheQuestion()
    {
        // Arrange
        GameFolderViewModel viewModel = _builder
            .Locating(ViewModelBuilder.Location(ScannedFolder, DatFileSource.DiskScan))
            .PickingFolder(null)
            .GameFolder();
        await viewModel.StartAsync();

        // Act
        await viewModel.ChooseFolderAsync();

        // Assert
        viewModel.IsQuestionVisible.ShouldBeTrue();
        viewModel.PromptText.ShouldBe(GameFolderTexts.FoundOne(ScannedFolder, DatFileSource.DiskScan));
        _builder.SettingsFile.Contents.ShouldBeNull();
    }

    [Fact]
    public async Task ChooseFolderAsync_WhenThePickerThrows_SaysSoAndKeepsTheButton()
    {
        // Arrange
        _builder.Picker.PickGameFolderAsync().Returns(Task.FromException<string?>(new InvalidOperationException("no window")));
        GameFolderViewModel viewModel = _builder.GameFolder();
        await viewModel.StartAsync();

        // Act
        await viewModel.ChooseFolderAsync();

        // Assert
        viewModel.StatusText.ShouldBe(GameFolderTexts.PickerFailed);
        viewModel.ChooseFolderCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task ChooseFolderAsync_WhileTheSearchRuns_DropsTheSearchResult()
    {
        // Arrange: the search would find one known install, which is saved with no question.
        using ManualResetEventSlim release = new();
        StallSearch(release, ViewModelBuilder.Location(KnownFolder, DatFileSource.StandingStoneGames));
        _builder.Files.AddGame(PickedFolder);
        GameFolderViewModel viewModel = _builder.PickingFolder(PickedFolder).GameFolder();
        Task start = viewModel.StartAsync();
        viewModel.PromptText.ShouldBe(GameFolderTexts.Searching);

        // Act
        await viewModel.ChooseFolderAsync();
        release.Set();
        await start;

        // Assert: the late result must not replace what the player picked.
        _builder.Store.Current.GameFolder.ShouldBe(PickedFolder);
        viewModel.IsPanelVisible.ShouldBeFalse();
    }

    [Fact]
    public async Task DropPendingScan_WhileTheSearchRuns_AsksForTheFolderAndIgnoresTheResult()
    {
        // Arrange: the player opens the settings window while the search runs.
        using ManualResetEventSlim release = new();
        StallSearch(release, ViewModelBuilder.Location(KnownFolder, DatFileSource.StandingStoneGames));
        GameFolderViewModel viewModel = _builder.GameFolder();
        Task start = viewModel.StartAsync();

        // Act
        viewModel.DropPendingScan();
        release.Set();
        await start;

        // Assert
        viewModel.PromptText.ShouldBe(GameFolderTexts.ChooseByHand);
        _builder.Store.Current.GameFolder.ShouldBeNull();
        _builder.SettingsFile.Contents.ShouldBeNull();
    }

    [Fact]
    public async Task ChooseFolderAsync_WhileTheDialogIsOpen_IgnoresASecondClick()
    {
        // Arrange
        TaskCompletionSource<string?> dialog = new();
        _builder.Picker.PickGameFolderAsync().Returns(dialog.Task);
        GameFolderViewModel viewModel = _builder.GameFolder();
        await viewModel.StartAsync();
        Task first = viewModel.ChooseFolderAsync();

        // Act
        bool canClickAgain = viewModel.ChooseFolderCommand.CanExecute(null);
        await viewModel.ChooseFolderAsync();
        dialog.SetResult(null);
        await first;

        // Assert: a second dialog over the first would confuse the player.
        canClickAgain.ShouldBeFalse();
        await _builder.Picker.Received(1).PickGameFolderAsync();
        viewModel.ChooseFolderCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task ChooseCommand_OnAnEntryFromAListThatIsGone_SavesNothing()
    {
        // Arrange: the player picked a folder without the game, which replaced the list.
        GameFolderViewModel viewModel = _builder
            .Locating(
                ViewModelBuilder.Location(KnownFolder, DatFileSource.StandingStoneGames),
                ViewModelBuilder.Location(ScannedFolder, DatFileSource.DiskScan))
            .PickingFolder(PickedFolder)
            .GameFolder();
        await viewModel.StartAsync();
        GameFolderCandidateViewModel staleEntry = viewModel.Candidates[1];
        await viewModel.ChooseFolderAsync();

        // Act
        staleEntry.ChooseCommand.Execute(null);

        // Assert
        viewModel.IsCandidateListVisible.ShouldBeFalse();
        _builder.Store.Current.GameFolder.ShouldBeNull();
        _builder.SettingsFile.Contents.ShouldBeNull();
    }

    [Fact]
    public async Task StartAsync_WhenTheSettingsReadThrowsSomethingUnexpected_AsksForTheFolder()
    {
        // Arrange: not an IO error, so the store does not catch it; the first run must not stop.
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(PickedFolder);
        _builder.SettingsFile.ReadFailure = new InvalidOperationException("bug");
        GameFolderViewModel viewModel = _builder.GameFolder();

        // Act
        await viewModel.StartAsync();

        // Assert
        viewModel.IsPanelVisible.ShouldBeTrue();
        viewModel.PromptText.ShouldBe(GameFolderTexts.NotFound);
        viewModel.ChooseFolderCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task DropPendingScan_WhileTheSearchForALostFolderRuns_NamesTheLostFolder()
    {
        // Arrange
        using ManualResetEventSlim release = new();
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(PickedFolder);
        StallSearch(release, ViewModelBuilder.Location(KnownFolder, DatFileSource.StandingStoneGames));
        GameFolderViewModel viewModel = _builder.GameFolder();
        Task start = viewModel.StartAsync();

        // Act
        viewModel.DropPendingScan();
        release.Set();
        await start;

        // Assert
        viewModel.PromptText.ShouldBe(GameFolderTexts.Lost(PickedFolder));
        _builder.Store.Current.GameFolder.ShouldBe(PickedFolder);
    }

    [Fact]
    public async Task StartAsync_AfterThePlayerStartedChoosingWithALostFolder_NamesTheLostFolder()
    {
        // Arrange
        _builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(PickedFolder);
        GameFolderViewModel viewModel = _builder.GameFolder();
        viewModel.DropPendingScan();

        // Act
        await viewModel.StartAsync();

        // Assert
        viewModel.PromptText.ShouldBe(GameFolderTexts.Lost(PickedFolder));
        _builder.Locator.DidNotReceive().LocateAll(Arg.Any<Action<string>?>());
    }

    [Fact]
    public async Task StartAsync_AfterThePlayerStartedChoosing_AsksForTheFolderWithoutSearching()
    {
        // Arrange: a search started now would be dropped anyway, and the panel would wait for it forever.
        GameFolderViewModel viewModel = _builder.Locating(ViewModelBuilder.Location(KnownFolder, DatFileSource.Steam)).GameFolder();
        viewModel.DropPendingScan();

        // Act
        await viewModel.StartAsync();

        // Assert
        viewModel.PromptText.ShouldBe(GameFolderTexts.ChooseByHand);
        _builder.Locator.DidNotReceive().LocateAll(Arg.Any<Action<string>?>());
    }

    [Fact]
    public async Task StartAsync_CalledTwice_SearchesOnce()
    {
        // Arrange: the window starts the first run on its own; a test or a second open may call it too.
        GameFolderViewModel viewModel = _builder.GameFolder();

        // Act
        await viewModel.StartAsync();
        await viewModel.StartAsync();

        // Assert
        _builder.Locator.Received(1).LocateAll(Arg.Any<Action<string>?>());
    }

    [Fact]
    public async Task StartAsync_WithValuesTheLoadDropped_ShowsWhyInTheStatusLine()
    {
        // Arrange
        ViewModelBuilder builder = ViewModelBuilder.WithASavedGame();
        builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(
            ViewModelBuilder.SavedGameFolder, serverAddress: "http://tms.example.com/");
        GameFolderViewModel viewModel = builder.GameFolder();

        // Act
        await viewModel.StartAsync();

        // Assert
        viewModel.IsStatusVisible.ShouldBeTrue();
        viewModel.StatusText.ShouldBe(SettingsTexts.ServerAddressDropped);
    }

    [Fact]
    public async Task StartAsync_WhenTheFoundFolderCannotBeSaved_UsesItAndSaysSo()
    {
        // Arrange
        _builder.SettingsFile.WriteFailure = new IOException("disk full");
        GameFolderViewModel viewModel = _builder.Locating(ViewModelBuilder.Location(KnownFolder, DatFileSource.Steam)).GameFolder();

        // Act
        await viewModel.StartAsync();

        // Assert
        viewModel.IsPanelVisible.ShouldBeFalse();
        viewModel.StatusText.ShouldBe(GameFolderTexts.SaveFailed);
        _builder.Store.Current.GameFolder.ShouldBe(KnownFolder);
    }

    [Fact]
    public async Task SettingsSave_WithAFolderThatHoldsTheGame_EndsTheFirstRun()
    {
        // Arrange
        GameFolderViewModel viewModel = _builder.GameFolder();
        await viewModel.StartAsync();
        _builder.Files.AddGame(PickedFolder);

        // Act: the settings window saves through the same store.
        _builder.Store.Save(_builder.Store.Current with { GameFolder = PickedFolder });

        // Assert
        viewModel.IsPanelVisible.ShouldBeFalse();
    }

    private void StallSearch(ManualResetEventSlim release, params DatFileLocation[] locations)
    {
        foreach (DatFileLocation location in locations)
        {
            _builder.Files.Add(location.Path);
        }

        _builder.Locator.LocateAll(Arg.Any<Action<string>?>()).Returns(_ =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return Result.Success<IReadOnlyList<DatFileLocation>>(locations);
        });
    }
}
