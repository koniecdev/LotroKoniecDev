using System.ComponentModel;
using Avalonia.Threading;
using LotroKoniecDev.Application;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using LotroKoniecDev.DesktopApp.Tests.Unit.Shared;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

[Collection(HeadlessSessionCollection.Name)]
public sealed class ProgressBarViewModelTests
{
    private readonly HeadlessSessionFixture _fixture;

    [Fact]
    public void Percent_BeforeAnyReport_IsZero()
    {
        new ProgressBarViewModel().Percent.ShouldBe(0);
    }

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(1, 3, 33)]
    [InlineData(50, 100, 50)]
    [InlineData(99, 100, 99)]
    [InlineData(100, 100, 100)]
    [InlineData(150, 100, 100)]
    [InlineData(-5, 100, 0)]
    [InlineData(int.MaxValue - 1, int.MaxValue, 99)]
    [InlineData(int.MaxValue, int.MaxValue, 100)]
    public async Task Report_AnyProgress_MovesTheBarToTheWholePercentBelowIt(int current, int total, int expectedPercent)
    {
        int percent = await _fixture.Session.Dispatch(
            () =>
            {
                // Arrange
                ProgressBarViewModel progress = new();

                // Act
                progress.Report(new OperationProgress(current, total));
                Dispatcher.UIThread.RunJobs();

                return progress.Percent;
            },
            CancellationToken.None);

        // Assert
        percent.ShouldBe(expectedPercent);
    }

    [Theory]
    [InlineData(0, 0, 100)]
    [InlineData(-1, 0, 0)]
    [InlineData(-10, -5, 0)]
    public async Task Report_ATotalOfZeroOrLess_NeverDividesByZero(int current, int total, int expectedPercent)
    {
        int percent = await _fixture.Session.Dispatch(
            () =>
            {
                // Arrange: nothing left to do counts as done, as OperationProgress.IsCompleted says.
                ProgressBarViewModel progress = new();

                // Act
                progress.Report(new OperationProgress(current, total));
                Dispatcher.UIThread.RunJobs();

                return progress.Percent;
            },
            CancellationToken.None);

        // Assert
        percent.ShouldBe(expectedPercent);
    }

    [Fact]
    public async Task Report_FromABackgroundThread_ChangesTheBoundPropertyOnTheUiThread()
    {
        bool? changedOnUiThread = await _fixture.Session.Dispatch(
            async () =>
            {
                // Arrange: a patch handler reports from the thread it runs on, never the UI thread.
                ProgressBarViewModel progress = new();
                bool? onUiThread = null;
                progress.PropertyChanged += (_, _) => onUiThread = Dispatcher.UIThread.CheckAccess();

                // Act
                await Task.Run(() => progress.Report(new OperationProgress(1, 2)));
                Dispatcher.UIThread.RunJobs();

                return onUiThread;
            },
            CancellationToken.None);

        // Assert: Avalonia throws when a binding source changes off the UI thread.
        changedOnUiThread.ShouldBe(true);
    }

    [Fact]
    public void Report_Null_Throws()
    {
        // Arrange
        ProgressBarViewModel progress = new();

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => progress.Report(null!));
    }

    [Fact]
    public void Percent_SetToANewValue_RaisesPropertyChangedForItOnce()
    {
        // Arrange
        ProgressBarViewModel progress = new();
        List<string?> changedProperties = [];
        progress.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        // Act
        progress.Percent = 60;

        // Assert
        progress.Percent.ShouldBe(60);
        changedProperties.ShouldBe([nameof(ProgressBarViewModel.Percent)]);
    }

    [Fact]
    public void Percent_SetToTheValueItAlreadyHolds_RaisesNothing()
    {
        // Arrange
        ProgressBarViewModel progress = new() { Percent = 60 };
        List<PropertyChangedEventArgs> raised = [];
        progress.PropertyChanged += (_, e) => raised.Add(e);

        // Act
        progress.Percent = 60;

        // Assert
        raised.ShouldBeEmpty();
    }

    public ProgressBarViewModelTests(HeadlessSessionFixture fixture)
    {
        _fixture = fixture;
    }
}
