using System.ComponentModel;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void Constructor_BeforeAnythingIsChecked_ShowsTheStaticStatusAndUnknownVersions()
    {
        // Act
        MainWindowViewModel viewModel = new();

        // Assert: the values stay static until #42 and #43 read the real ones.
        viewModel.Status.ShouldBe("Ready to play");
        viewModel.GameVersion.ShouldBe(MainWindowViewModel.UnknownValue);
        viewModel.PatchVersion.ShouldBe(MainWindowViewModel.UnknownValue);
        viewModel.ProgressPercent.ShouldBe(0);
    }

    [Fact]
    public void ProgressPercent_SetToANewValue_RaisesPropertyChangedForItOnce()
    {
        // Arrange
        MainWindowViewModel viewModel = new();
        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        // Act
        viewModel.ProgressPercent = 60;

        // Assert
        viewModel.ProgressPercent.ShouldBe(60);
        changedProperties.ShouldBe([nameof(MainWindowViewModel.ProgressPercent)]);
    }

    [Fact]
    public void ProgressPercent_SetToTheValueItAlreadyHolds_RaisesNothing()
    {
        // Arrange
        MainWindowViewModel viewModel = new() { ProgressPercent = 60 };
        List<PropertyChangedEventArgs> raised = [];
        viewModel.PropertyChanged += (_, e) => raised.Add(e);

        // Act
        viewModel.ProgressPercent = 60;

        // Assert
        raised.ShouldBeEmpty();
    }
}
