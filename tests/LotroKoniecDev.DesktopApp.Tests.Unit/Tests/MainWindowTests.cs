using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using LotroKoniecDev.DesktopApp.Presentation.Views;
using LotroKoniecDev.DesktopApp.Tests.Unit.Shared;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

/// <summary>
/// The layout #41 asks for: a title, three status lines, the Patch and Play buttons and a progress bar
/// with its percent.
/// </summary>
/// <remarks>
/// Every window closes in a <c>finally</c>: the session is shared, so a failed check must not leave a
/// window open for the next test.
/// </remarks>
[Collection(HeadlessSessionCollection.Name)]
public sealed class MainWindowTests
{
    private readonly HeadlessSessionFixture _fixture;

    [Fact]
    public async Task Show_WithAFreshViewModel_TitlesTheWindowAndLabelsEveryStatusLine()
    {
        (string? Title, List<string?> Texts) shown = await _fixture.Session.Dispatch(
            () =>
            {
                MainWindow window = ShowWith(CreateViewModel());
                try
                {
                    return (window.Title, window.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text).ToList());
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        shown.Title.ShouldBe("LOTRO po Polsku");
        shown.Texts.ShouldContain("Status:");
        shown.Texts.ShouldContain("Game version:");
        shown.Texts.ShouldContain("Patch version:");
    }

    [Fact]
    public async Task Show_WithAFreshViewModel_BindsTheStatusLinesToTheViewModel()
    {
        (string? Status, string? GameVersion, string? PatchVersion) shown = await _fixture.Session.Dispatch(
            () =>
            {
                MainWindow window = ShowWith(CreateViewModel());
                try
                {
                    return (
                        window.FindControl<TextBlock>("StatusText")?.Text,
                        window.FindControl<TextBlock>("GameVersionText")?.Text,
                        window.FindControl<TextBlock>("PatchVersionText")?.Text);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        shown.Status.ShouldBe("Ready to play");
        shown.GameVersion.ShouldBe(MainWindowViewModel.UnknownValue);
        shown.PatchVersion.ShouldBe(MainWindowViewModel.UnknownValue);
    }

    [Theory]
    [InlineData("PatchButton", "Patch")]
    [InlineData("PlayButton", "Play")]
    public async Task Show_BeforeTheButtonsAreWired_ShowsThemDisabled(string buttonName, string expectedLabel)
    {
        (object? Content, bool? IsEnabled) button = await _fixture.Session.Dispatch(
            () =>
            {
                MainWindow window = ShowWith(CreateViewModel());
                try
                {
                    Button? found = window.FindControl<Button>(buttonName);
                    return (found?.Content, found?.IsEnabled);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        // #42 wires them; until then a click must not look like it did something.
        button.Content.ShouldBe(expectedLabel);
        button.IsEnabled.ShouldBe(false);
    }

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(60, "60%")]
    [InlineData(100, "100%")]
    public async Task Percent_WhenTheProgressBarChanges_MovesTheBarAndItsLabel(int percent, string expectedLabel)
    {
        (double? BarValue, string? Label) progress = await _fixture.Session.Dispatch(
            () =>
            {
                MainWindowViewModel viewModel = CreateViewModel();
                MainWindow window = ShowWith(viewModel);
                try
                {
                    viewModel.Progress.Percent = percent;
                    Dispatcher.UIThread.RunJobs();

                    return (
                        window.FindControl<ProgressBar>("PatchProgressBar")?.Value,
                        window.FindControl<TextBlock>("ProgressText")?.Text);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        progress.BarValue.ShouldBe(percent);
        progress.Label.ShouldBe(expectedLabel);
    }

    public MainWindowTests(HeadlessSessionFixture fixture)
    {
        _fixture = fixture;
    }

    private static MainWindowViewModel CreateViewModel() => new(new ProgressBarViewModel());

    private static MainWindow ShowWith(MainWindowViewModel viewModel)
    {
        MainWindow window = new() { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
