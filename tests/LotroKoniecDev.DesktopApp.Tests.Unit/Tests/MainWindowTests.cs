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
                MainWindow window = ShowWith(new MainWindowViewModel());
                (string?, List<string?>) content = (
                    window.Title,
                    window.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text).ToList());
                window.Close();
                return content;
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
                MainWindow window = ShowWith(new MainWindowViewModel());
                (string?, string?, string?) values = (
                    TextOf(window, "StatusText"),
                    TextOf(window, "GameVersionText"),
                    TextOf(window, "PatchVersionText"));
                window.Close();
                return values;
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
        (object? Content, bool IsEnabled) button = await _fixture.Session.Dispatch(
            () =>
            {
                MainWindow window = ShowWith(new MainWindowViewModel());
                Button found = window.FindControl<Button>(buttonName).ShouldNotBeNull();
                (object?, bool) state = (found.Content, found.IsEnabled);
                window.Close();
                return state;
            },
            CancellationToken.None);

        // #42 wires them; until then a click must not look like it did something.
        button.Content.ShouldBe(expectedLabel);
        button.IsEnabled.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(60, "60%")]
    [InlineData(100, "100%")]
    public async Task ProgressPercent_WhenTheViewModelChanges_MovesTheBarAndItsLabel(int percent, string expectedLabel)
    {
        (double BarValue, string? Label) progress = await _fixture.Session.Dispatch(
            () =>
            {
                MainWindowViewModel viewModel = new();
                MainWindow window = ShowWith(viewModel);

                viewModel.ProgressPercent = percent;
                Dispatcher.UIThread.RunJobs();

                (double, string?) state = (
                    window.FindControl<ProgressBar>("PatchProgressBar").ShouldNotBeNull().Value,
                    TextOf(window, "ProgressText"));
                window.Close();
                return state;
            },
            CancellationToken.None);

        progress.BarValue.ShouldBe(percent);
        progress.Label.ShouldBe(expectedLabel);
    }

    public MainWindowTests(HeadlessSessionFixture fixture)
    {
        _fixture = fixture;
    }

    private static MainWindow ShowWith(MainWindowViewModel viewModel)
    {
        MainWindow window = new() { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static string? TextOf(MainWindow window, string name) =>
        window.FindControl<TextBlock>(name).ShouldNotBeNull().Text;
}
