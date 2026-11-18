using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using LotroKoniecDev.DesktopApp.Tests.Unit.Shared;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void Constructor_BeforeAnythingRuns_ShowsThePolishReadyStatusAndUnknownVersions()
    {
        // Act
        MainWindowViewModel viewModel = ViewModelBuilder.WithASavedGame().MainWindow();

        // Assert: nothing reads the two versions yet, so they stay unknown.
        viewModel.PatchAndPlay.StatusText.ShouldBe("Gotowe.");
        viewModel.GameVersion.ShouldBe(MainWindowViewModel.UnknownValue);
        viewModel.PatchVersion.ShouldBe(MainWindowViewModel.UnknownValue);
    }

    [Fact]
    public async Task OpenSettings_AfterTheFirstRun_FillsTheFieldsFromTheSettingsInUse()
    {
        // Arrange
        MainWindowViewModel viewModel = ViewModelBuilder.WithASavedGame().MainWindow();
        await viewModel.GameFolder.StartAsync();

        // Act
        SettingsViewModel settings = viewModel.OpenSettings();

        // Assert
        settings.ShouldBeSameAs(viewModel.Settings);
        settings.GameFolder.ShouldBe(ViewModelBuilder.SavedGameFolder);
    }

    [Fact]
    public async Task OpenSettings_WhileTheSearchRuns_StopsItFromChoosing()
    {
        // Arrange: the search would find one known install, which is saved with no question.
        using ManualResetEventSlim release = new();
        string knownFolder = ViewModelBuilder.Rooted("Steam", "LOTRO");
        ViewModelBuilder builder = new();
        builder.Files.AddGame(knownFolder);
        builder.Locator.LocateAll(Arg.Any<Action<string>?>()).Returns(_ =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return Result.Success<IReadOnlyList<DatFileLocation>>([ViewModelBuilder.Location(knownFolder, DatFileSource.Steam)]);
        });
        MainWindowViewModel viewModel = builder.MainWindow();
        Task start = viewModel.GameFolder.StartAsync();

        // Act
        viewModel.OpenSettings();
        release.Set();
        await start;

        // Assert
        builder.Store.Current.GameFolder.ShouldBeNull();
        viewModel.GameFolder.IsPanelVisible.ShouldBeTrue();
    }
}
