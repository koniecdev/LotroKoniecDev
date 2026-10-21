using LotroKoniecDev.DesktopApp.Presentation.ViewModels;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void Constructor_BeforeAnythingIsChecked_ShowsTheStaticStatusAndUnknownVersions()
    {
        // Act
        MainWindowViewModel viewModel = new(new ProgressBarViewModel());

        // Assert: the values stay static until #42 and #43 read the real ones.
        viewModel.Status.ShouldBe("Ready to play");
        viewModel.GameVersion.ShouldBe(MainWindowViewModel.UnknownValue);
        viewModel.PatchVersion.ShouldBe(MainWindowViewModel.UnknownValue);
    }
}
