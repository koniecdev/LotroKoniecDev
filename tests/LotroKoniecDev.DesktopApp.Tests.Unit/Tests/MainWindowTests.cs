using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Features.AppUpdating;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using LotroKoniecDev.DesktopApp.Presentation.Views;
using LotroKoniecDev.DesktopApp.Tests.Unit.Shared;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

/// <summary>
/// The layout #41 asks for: a title, three status lines, the Patch and Play buttons and a progress bar
/// with its percent. #43 adds the first-run panel, the two notices and the settings button, and #45 the
/// update panel.
/// </summary>
/// <remarks>
/// Every window closes in a <c>finally</c>: the session is shared, so a failed check must not leave a
/// window open for the next test.
/// </remarks>
[Collection(HeadlessSessionCollection.Name)]
public sealed class MainWindowTests
{
    private static readonly AvailableAppUpdate OfferedUpdate = new(
        new DesktopVersion(0, 3, 0),
        new Uri("https://github.com/koniecdev/LotroKoniecDev/releases/download/desktop-v0.3.0/LotroPoPolsku-Setup-0.3.0.exe"),
        new Uri("https://github.com/koniecdev/LotroKoniecDev/releases/download/desktop-v0.3.0/LotroPoPolsku-Setup-0.3.0.exe.sha256"));

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

    [Fact]
    public async Task Show_Always_ShowsTheRiskNoteAndTheLauncherNotice()
    {
        (string? Risk, string? Launcher, bool? BothVisible) notices = await _fixture.Session.Dispatch(
            () =>
            {
                MainWindow window = ShowWith(CreateViewModel());
                try
                {
                    TextBlock? risk = window.FindControl<TextBlock>("RiskNoticeText");
                    TextBlock? launcher = window.FindControl<TextBlock>("LauncherNoticeText");
                    return (risk?.Text, launcher?.Text, risk?.IsEffectivelyVisible == true && launcher?.IsEffectivelyVisible == true);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        // The CLI's LEGAL-12 sentence word for word (spec 0011), and the app's own launcher notice (ADR-0069 §3).
        notices.Risk.ShouldBe(
            "Spolszczenie modyfikuje pliki gry — formalnie regulamin LOTRO nie przewiduje takich modyfikacji, " +
            "choć przez ponad dekadę działania analogicznych projektów (rosyjskiego i hiszpańskiego) " +
            "nie odnotowano za nie banów. Korzystasz na własną odpowiedzialność.");
        notices.Launcher.ShouldNotBeNull().ShouldContain("LotroLauncher.exe");
        notices.Launcher.ShouldContain("uprawnienia tej aplikacji (administratora)");
        notices.Launcher.ShouldNotContain("lotro.bat");
        notices.Launcher.ShouldNotContain("sam prosi");
        notices.BothVisible.ShouldBe(true);
    }

    [Fact]
    public async Task Show_WithASavedGame_HidesTheGameFolderPanel()
    {
        bool? panelVisible = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = CreateViewModel();
                MainWindow window = ShowWith(viewModel);
                try
                {
                    await viewModel.GameFolder.StartAsync();
                    Dispatcher.UIThread.RunJobs();
                    return window.FindControl<StackPanel>("GameFolderPanel")?.IsVisible;
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        panelVisible.ShouldBe(false);
    }

    [Fact]
    public async Task Show_WithNoSavedGame_StartsTheSearchOnItsOwnAndAsksInPolish()
    {
        string scannedFolder = ViewModelBuilder.Rooted("Gry", "LOTRO");
        (bool? PanelVisible, string? Prompt, object? Confirm, object? Decline, object? ChooseFolder) panel =
            await _fixture.Session.Dispatch(
                async () =>
                {
                    MainWindowViewModel viewModel = new ViewModelBuilder()
                        .Locating(ViewModelBuilder.Location(scannedFolder, DatFileSource.DiskScan))
                        .MainWindow();

                    // No StartAsync here: opening the window must start the first run (#43).
                    MainWindow window = ShowWith(viewModel);
                    try
                    {
                        await WaitUntil(() => viewModel.GameFolder.IsQuestionVisible);
                        return (
                            window.FindControl<StackPanel>("GameFolderPanel")?.IsVisible,
                            window.FindControl<TextBlock>("GameFolderPromptText")?.Text,
                            window.FindControl<Button>("ConfirmGameFolderButton")?.Content,
                            window.FindControl<Button>("DeclineGameFolderButton")?.Content,
                            window.FindControl<Button>("ChooseGameFolderButton")?.Content);
                    }
                    finally
                    {
                        window.Close();
                    }
                },
                CancellationToken.None);

        panel.PanelVisible.ShouldBe(true);
        panel.Prompt.ShouldNotBeNull().ShouldStartWith($"Znaleziono grę w folderze {scannedFolder}");
        panel.Confirm.ShouldBe("Tak");
        panel.Decline.ShouldBe("Nie");
        panel.ChooseFolder.ShouldBe("Wybierz folder gry…");
    }

    [Fact]
    public async Task ClickTak_OnTheQuestion_SavesTheFolderAndHidesThePanel()
    {
        string scannedFolder = ViewModelBuilder.Rooted("Gry", "LOTRO");
        ViewModelBuilder builder = new ViewModelBuilder().Locating(ViewModelBuilder.Location(scannedFolder, DatFileSource.DiskScan));
        bool? panelVisible = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = builder.MainWindow();
                MainWindow window = ShowWith(viewModel);
                try
                {
                    await WaitUntil(() => viewModel.GameFolder.IsQuestionVisible);
                    Button confirm = window.FindControl<Button>("ConfirmGameFolderButton").ShouldNotBeNull();
                    confirm.Command.ShouldNotBeNull().Execute(confirm.CommandParameter);
                    Dispatcher.UIThread.RunJobs();
                    return window.FindControl<StackPanel>("GameFolderPanel")?.IsVisible;
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        panelVisible.ShouldBe(false);
        builder.Store.Current.GameFolder.ShouldBe(scannedFolder);
    }

    [Fact]
    public async Task Show_WithSeveralInstalls_ShowsOneButtonPerInstall()
    {
        List<string?> folders = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = new ViewModelBuilder()
                    .Locating(
                        ViewModelBuilder.Location(ViewModelBuilder.Rooted("A", "LOTRO"), DatFileSource.Steam),
                        ViewModelBuilder.Location(ViewModelBuilder.Rooted("B", "LOTRO"), DatFileSource.DiskScan))
                    .MainWindow();
                MainWindow window = ShowWith(viewModel);
                try
                {
                    await WaitUntil(() => viewModel.GameFolder.IsCandidateListVisible);
                    ItemsControl list = window.FindControl<ItemsControl>("GameFolderCandidates").ShouldNotBeNull();
                    return list.GetVisualDescendants().OfType<Button>()
                        .Select(button => (button.DataContext as GameFolderCandidateViewModel)?.Folder)
                        .ToList();
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        folders.ShouldBe([ViewModelBuilder.Rooted("A", "LOTRO"), ViewModelBuilder.Rooted("B", "LOTRO")]);
    }

    [Fact]
    public async Task ClickUstawienia_Always_OpensTheSettingsWindowOverTheMainWindow()
    {
        (string? Label, string? SettingsTitle, string? GameFolder) settings = await _fixture.Session.Dispatch(
            () =>
            {
                MainWindow window = ShowWith(CreateViewModel());
                try
                {
                    Button button = window.FindControl<Button>("SettingsButton").ShouldNotBeNull();
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Dispatcher.UIThread.RunJobs();
                    SettingsWindow? opened = window.OwnedWindows.OfType<SettingsWindow>().SingleOrDefault();
                    return (button.Content as string, opened?.Title, opened?.FindControl<TextBox>("GameFolderBox")?.Text);
                }
                finally
                {
                    foreach (Window owned in window.OwnedWindows.ToList())
                    {
                        owned.Close();
                    }

                    window.Close();
                }
            },
            CancellationToken.None);

        settings.Label.ShouldBe("Ustawienia");
        settings.SettingsTitle.ShouldBe("Ustawienia");
        settings.GameFolder.ShouldBe(ViewModelBuilder.SavedGameFolder);
    }

    [Fact]
    public async Task Show_WithNothingToOffer_HidesTheUpdatePanelAndItsStatusLine()
    {
        (bool? PanelVisible, bool? StatusVisible) update = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = CreateViewModel();
                await viewModel.AppUpdate.CheckAsync();
                MainWindow window = ShowWith(viewModel);
                try
                {
                    return (
                        window.FindControl<StackPanel>("UpdatePanel")?.IsVisible,
                        window.FindControl<TextBlock>("UpdateStatusText")?.IsVisible);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        update.PanelVisible.ShouldBe(false);
        update.StatusVisible.ShouldBe(false);
    }

    [Fact]
    public async Task Show_WithANewerVersion_AsksInPolishWithTakAndNie()
    {
        (bool? PanelVisible, string? Prompt, object? Accept, bool? AcceptEnabled, object? Decline) update =
            await _fixture.Session.Dispatch(
                async () =>
                {
                    MainWindowViewModel viewModel = ViewModelBuilder.WithASavedGame().MainWindow(ViewModelBuilder.AppUpdate(
                        ViewModelBuilder.CheckAnswering(AppUpdateCheckResponse.UpdateAvailable(OfferedUpdate))));
                    await viewModel.AppUpdate.CheckAsync();
                    MainWindow window = ShowWith(viewModel);
                    try
                    {
                        Button? accept = window.FindControl<Button>("AcceptUpdateButton");
                        return (
                            window.FindControl<StackPanel>("UpdatePanel")?.IsVisible,
                            window.FindControl<TextBlock>("UpdatePromptText")?.Text,
                            accept?.Content,
                            accept?.IsEffectivelyEnabled,
                            window.FindControl<Button>("DeclineUpdateButton")?.Content);
                    }
                    finally
                    {
                        window.Close();
                    }
                },
                CancellationToken.None);

        update.PanelVisible.ShouldBe(true);
        update.Prompt.ShouldBe("Dostępna jest wersja 0.3.0. Zaktualizować?");
        update.Accept.ShouldBe("Tak");
        update.AcceptEnabled.ShouldBe(true);
        update.Decline.ShouldBe("Nie");
    }

    [Fact]
    public async Task Show_WithANewerVersion_StartsTheCheckOnItsOwnAndShowsTheQuestion()
    {
        bool? panelVisible = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = ViewModelBuilder.WithASavedGame().MainWindow(ViewModelBuilder.AppUpdate(
                    ViewModelBuilder.CheckAnswering(AppUpdateCheckResponse.UpdateAvailable(OfferedUpdate))));

                // No CheckAsync here: opening the window must start the check (spec 0016).
                MainWindow window = ShowWith(viewModel);
                try
                {
                    DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                    while (!viewModel.AppUpdate.IsPromptVisible && DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(20));
                        Dispatcher.UIThread.RunJobs();
                    }

                    return window.FindControl<StackPanel>("UpdatePanel")?.IsVisible;
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        panelVisible.ShouldBe(true);
    }

    [Fact]
    public async Task Show_AfterAFailedCheck_ShowsTheReasonUnderThePanel()
    {
        (bool? StatusVisible, string? Status) update = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = ViewModelBuilder.WithASavedGame().MainWindow(ViewModelBuilder.AppUpdate(
                    ViewModelBuilder.CheckAnswering(
                        AppUpdateCheckResponse.CheckFailed(DomainErrors.AppUpdate.TimedOut))));
                await viewModel.AppUpdate.CheckAsync();
                MainWindow window = ShowWith(viewModel);
                try
                {
                    TextBlock? status = window.FindControl<TextBlock>("UpdateStatusText");
                    return (status?.IsVisible, status?.Text);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        update.StatusVisible.ShouldBe(true);
        update.Status.ShouldBe("Nie udało się sprawdzić aktualizacji: GitHub nie odpowiedział na czas. Program działa dalej w wersji 0.1.0.");
    }

    [Fact]
    public async Task ClickTak_WithANewerVersion_StartsTheInstallAndSaysSo()
    {
        string? status = await _fixture.Session.Dispatch(
            async () =>
            {
                ICommandHandler<InstallAppUpdateCommand, Result> install =
                    Substitute.For<ICommandHandler<InstallAppUpdateCommand, Result>>();
                install.Handle(Arg.Any<InstallAppUpdateCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
                MainWindowViewModel viewModel = ViewModelBuilder.WithASavedGame().MainWindow(ViewModelBuilder.AppUpdate(
                    ViewModelBuilder.CheckAnswering(AppUpdateCheckResponse.UpdateAvailable(OfferedUpdate)), install));
                MainWindow window = ShowWith(viewModel);
                try
                {
                    await WaitForTheWindowsOwnCheck(viewModel);
                    Button accept = window.FindControl<Button>("AcceptUpdateButton").ShouldNotBeNull();
                    accept.Command.ShouldNotBeNull().Execute(accept.CommandParameter);
                    TextBlock? statusText = window.FindControl<TextBlock>("UpdateStatusText");
                    await WaitUntil(() => statusText?.Text is not null && !statusText.Text.StartsWith("Pobieram", StringComparison.Ordinal));
                    return statusText?.Text;
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        status.ShouldBe("Uruchomiono instalator. Program zaraz się zamknie.");
    }

    [Fact]
    public async Task ClickNie_WithANewerVersion_HidesTheUpdatePanel()
    {
        bool? panelVisible = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = ViewModelBuilder.WithASavedGame().MainWindow(ViewModelBuilder.AppUpdate(
                    ViewModelBuilder.CheckAnswering(AppUpdateCheckResponse.UpdateAvailable(OfferedUpdate))));
                MainWindow window = ShowWith(viewModel);
                try
                {
                    await WaitForTheWindowsOwnCheck(viewModel);
                    Button decline = window.FindControl<Button>("DeclineUpdateButton").ShouldNotBeNull();
                    decline.Command.ShouldNotBeNull().Execute(decline.CommandParameter);
                    Dispatcher.UIThread.RunJobs();
                    return window.FindControl<StackPanel>("UpdatePanel")?.IsVisible;
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        panelVisible.ShouldBe(false);
    }

    [Fact]
    public async Task Show_WhileAnotherOperationHoldsTheGate_DisablesTak()
    {
        (bool? PanelVisible, bool? AcceptEnabled) update = await _fixture.Session.Dispatch(
            async () =>
            {
                OperationGate gate = new();
                gate.TryEnter();
                MainWindowViewModel viewModel = ViewModelBuilder.WithASavedGame().MainWindow(ViewModelBuilder.AppUpdate(
                    ViewModelBuilder.CheckAnswering(AppUpdateCheckResponse.UpdateAvailable(OfferedUpdate)), gate: gate));
                MainWindow window = ShowWith(viewModel);
                try
                {
                    await WaitForTheWindowsOwnCheck(viewModel);
                    return (
                        window.FindControl<StackPanel>("UpdatePanel")?.IsVisible,
                        window.FindControl<Button>("AcceptUpdateButton")?.IsEffectivelyEnabled);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        // The offer is shown, so "Tak" is disabled by the gate and not by a missing release.
        update.PanelVisible.ShouldBe(true);
        update.AcceptEnabled.ShouldBe(false);
    }


    public MainWindowTests(HeadlessSessionFixture fixture)
    {
        _fixture = fixture;
    }

    private static MainWindowViewModel CreateViewModel() => ViewModelBuilder.WithASavedGame().MainWindow();

    private static MainWindow ShowWith(MainWindowViewModel viewModel)
    {
        MainWindow window = new() { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>
    /// Opening the window starts a check of its own. A click before that check has ended would race
    /// with it: a late answer shows the question again.
    /// </summary>
    private static Task WaitForTheWindowsOwnCheck(MainWindowViewModel viewModel) =>
        WaitUntil(() => viewModel.AppUpdate.IsPromptVisible);

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
            Dispatcher.UIThread.RunJobs();
        }
    }
}
