using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using LotroKoniecDev.DesktopApp.Presentation;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using LotroKoniecDev.DesktopApp.Presentation.Views;
using LotroKoniecDev.DesktopApp.Tests.Unit.Shared;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

[Collection(HeadlessSessionCollection.Name)]
public sealed class SplashScreenTests
{
    private readonly HeadlessSessionFixture _fixture;

    [Fact]
    public async Task Show_BeforeTheDurationEnds_KeepsTheSplashAsTheOnlyWindow()
    {
        (string? MainWindowTitle, bool SplashIsMainWindow, int ViewModelsCreated) state = await _fixture.Session.Dispatch(
            async () =>
            {
                // Arrange
                IClassicDesktopStyleApplicationLifetime desktop = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
                int viewModelsCreated = 0;

                // Act
                SplashScreen.Show(
                    desktop,
                    () =>
                    {
                        viewModelsCreated++;
                        return new MainWindowViewModel(new ProgressBarViewModel());
                    },
                    TimeSpan.FromHours(1));

                // Awaiting lets the dispatcher run, so a timer that ignored the duration would fire here.
                await Task.Delay(TimeSpan.FromMilliseconds(200));

                return (desktop.MainWindow?.Title, desktop.MainWindow is SplashWindow, viewModelsCreated);
            },
            CancellationToken.None);

        // Assert
        state.SplashIsMainWindow.ShouldBeTrue();
        state.MainWindowTitle.ShouldBe("LOTRO po Polsku");
        state.ViewModelsCreated.ShouldBe(0);
    }

    [Fact]
    public async Task Show_OnceTheDurationEnds_OpensTheMainWindowBeforeClosingTheSplash()
    {
        MainWindowViewModel viewModel = new(new ProgressBarViewModel());

        (object? MainWindowDataContext, bool MainWindowOpenWhenSplashClosed, bool SplashOpen) state = await _fixture.Session.Dispatch(
            async () =>
            {
                // Arrange
                IClassicDesktopStyleApplicationLifetime desktop = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
                SplashScreen.Show(desktop, () => viewModel, TimeSpan.Zero);

                // The lifetime shows its main window when it starts; the substitute does not, so do it here.
                Window splash = desktop.MainWindow.ShouldBeOfType<SplashWindow>();
                splash.Show();
                bool mainWindowOpenWhenSplashClosed = false;
                splash.Closed += (_, _) => mainWindowOpenWhenSplashClosed = desktop.MainWindow is MainWindow { IsVisible: true };

                try
                {
                    // Act
                    await WaitUntilAsync(() => !splash.IsVisible);

                    return (desktop.MainWindow?.DataContext, mainWindowOpenWhenSplashClosed, splash.IsVisible);
                }
                finally
                {
                    // The session is shared, so neither window may stay open, not even after a failure.
                    desktop.MainWindow?.Close();
                    splash.Close();
                }
            },
            CancellationToken.None);

        // Assert: with the default shutdown mode, closing the last open window would end the app.
        state.MainWindowDataContext.ShouldBeSameAs(viewModel);
        state.MainWindowOpenWhenSplashClosed.ShouldBeTrue();
        state.SplashOpen.ShouldBeFalse();
    }

    [Fact]
    public void Show_WithoutALifetime_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => SplashScreen.Show(null!, () => new MainWindowViewModel(new ProgressBarViewModel()), TimeSpan.Zero));
    }

    [Fact]
    public void Show_WithoutAViewModelFactory_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => SplashScreen.Show(Substitute.For<IClassicDesktopStyleApplicationLifetime>(), null!, TimeSpan.Zero));
    }

    public SplashScreenTests(HeadlessSessionFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Awaiting on the UI thread lets the headless dispatcher run its timers; a blocking wait would not.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }
}
