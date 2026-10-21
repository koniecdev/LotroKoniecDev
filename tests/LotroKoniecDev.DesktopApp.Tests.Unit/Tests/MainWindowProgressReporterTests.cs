using Avalonia.Threading;
using LotroKoniecDev.Application;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using LotroKoniecDev.DesktopApp.Tests.Unit.Shared;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

[Collection(HeadlessSessionCollection.Name)]
public sealed class MainWindowProgressReporterTests
{
    private readonly HeadlessSessionFixture _fixture;

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
        await _fixture.Session.Dispatch(
            () =>
            {
                // Arrange
                MainWindowViewModel viewModel = new();
                MainWindowProgressReporter reporter = new(viewModel);

                // Act
                reporter.Report(new OperationProgress(current, total));
                Dispatcher.UIThread.RunJobs();

                // Assert
                viewModel.ProgressPercent.ShouldBe(expectedPercent);
            },
            CancellationToken.None);
    }

    [Theory]
    [InlineData(0, 0, 100)]
    [InlineData(-1, 0, 0)]
    [InlineData(-10, -5, 0)]
    public async Task Report_ATotalOfZeroOrLess_NeverDividesByZero(int current, int total, int expectedPercent)
    {
        await _fixture.Session.Dispatch(
            () =>
            {
                // Arrange: nothing left to do counts as done, as OperationProgress.IsCompleted says.
                MainWindowViewModel viewModel = new();
                MainWindowProgressReporter reporter = new(viewModel);

                // Act
                reporter.Report(new OperationProgress(current, total));
                Dispatcher.UIThread.RunJobs();

                // Assert
                viewModel.ProgressPercent.ShouldBe(expectedPercent);
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Report_FromABackgroundThread_ChangesTheBoundPropertyOnTheUiThread()
    {
        bool? changedOnUiThread = await _fixture.Session.Dispatch(
            async () =>
            {
                // Arrange: a patch handler reports from the thread it runs on, never the UI thread.
                MainWindowViewModel viewModel = new();
                MainWindowProgressReporter reporter = new(viewModel);
                bool? onUiThread = null;
                viewModel.PropertyChanged += (_, _) => onUiThread = Dispatcher.UIThread.CheckAccess();

                // Act
                await Task.Run(() => reporter.Report(new OperationProgress(1, 2)));
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
        MainWindowProgressReporter reporter = new(new MainWindowViewModel());

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => reporter.Report(null!));
    }

    public MainWindowProgressReporterTests(HeadlessSessionFixture fixture)
    {
        _fixture = fixture;
    }
}
