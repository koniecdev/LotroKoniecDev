using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Features.AppUpdating;
using LotroKoniecDev.Application.Features.GameLaunching;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Application.Features.TranslationFileSyncing;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using LotroKoniecDev.DesktopApp.Presentation.Views;
using LotroKoniecDev.DesktopApp.Tests.Unit.Shared;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

/// <summary>
/// The layout #41 asks for: a title, three status lines, the Patch and Play buttons and a progress bar
/// with its percent. #43 adds the first-run panel, the two notices and the settings button, #45 the
/// update panel, and #42 the Polish labels and a working Patch and Play.
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

    /// <summary>The usable height of a 1080p laptop screen at 150%, rounded up (#1463).</summary>
    private static readonly PixelRect LaptopWorkArea = new(0, 0, 1920, 690);

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
        shown.Texts.ShouldContain("Wersja gry:");
        shown.Texts.ShouldContain("Wersja tłumaczenia:");
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

        shown.Status.ShouldBe("Gotowe.");
        shown.GameVersion.ShouldBe(MainWindowViewModel.UnknownValue);
        shown.PatchVersion.ShouldBe(MainWindowViewModel.UnknownValue);
    }

    [Theory]
    [InlineData("PatchButton", "Nałóż spolszczenie")]
    [InlineData("PlayButton", "Graj")]
    public async Task Show_WithASavedGame_ShowsTheButtonsInPolishAndEnabled(string buttonName, string expectedLabel)
    {
        (object? Content, bool? IsEnabled) button = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = CreateViewModel();
                MainWindow window = ShowWith(viewModel);
                try
                {
                    await viewModel.GameFolder.StartAsync();
                    Dispatcher.UIThread.RunJobs();
                    Button? found = window.FindControl<Button>(buttonName);
                    return (found?.Content, found?.IsEffectivelyEnabled);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        // "Graj" is the wiki's word for the one click a player makes (Wersje systemu).
        button.Content.ShouldBe(expectedLabel);
        button.IsEnabled.ShouldBe(true);
    }

    [Theory]
    [InlineData("PatchButton")]
    [InlineData("PlayButton")]
    public async Task Show_WithNoGameFolderYet_DisablesTheButton(string buttonName)
    {
        bool? isEnabled = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = new ViewModelBuilder().MainWindow();
                MainWindow window = ShowWith(viewModel);
                try
                {
                    await viewModel.GameFolder.StartAsync();
                    Dispatcher.UIThread.RunJobs();
                    return window.FindControl<Button>(buttonName)?.IsEffectivelyEnabled;
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        isEnabled.ShouldBe(false);
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

    [Fact]
    public async Task Show_WithEightInstallsAndAnUpdateOnALaptopScreen_StaysInsideTheWorkArea()
    {
        (int Top, double Height, double ScrollRange) shown = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = EightInstallsAndAnUpdate();
                MainWindow window = ShowWith(viewModel, () => LaptopWorkArea);
                try
                {
                    await WaitForEveryPanel(viewModel);
                    return (window.Position.Y, window.ClientSize.Height, Scroller(window).ScrollBarMaximum.Y);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        // The panels came after the window was placed, and the content is now taller than the window.
        shown.ScrollRange.ShouldBeGreaterThan(0);
        // A headless window has no title bar, so its client area is its whole frame.
        shown.Top.ShouldBeGreaterThanOrEqualTo(LaptopWorkArea.Y);
        (shown.Top + shown.Height).ShouldBeLessThanOrEqualTo(LaptopWorkArea.Bottom);
    }

    [Theory]
    [InlineData("ChooseGameFolderButton")]
    [InlineData("PatchButton")]
    [InlineData("PlayButton")]
    [InlineData("AcceptUpdateButton")]
    [InlineData("DeclineUpdateButton")]
    [InlineData("SettingsButton")]
    public async Task Scroll_WithEightInstallsAndAnUpdateOnALaptopScreen_BringsTheButtonOnScreen(string buttonName)
    {
        (double Top, double Bottom, double WindowTop, double WindowBottom) button = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = EightInstallsAndAnUpdate();
                MainWindow window = ShowWith(viewModel, () => LaptopWorkArea);
                try
                {
                    await WaitForEveryPanel(viewModel);
                    return ScreenEdgesAfterScrollingTo(window, window.FindControl<Button>(buttonName).ShouldNotBeNull());
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        // Inside the work area, and inside the window's visible part, not hidden by the scroller.
        button.Top.ShouldBeGreaterThanOrEqualTo(Math.Max(LaptopWorkArea.Y, button.WindowTop));
        button.Bottom.ShouldBeLessThanOrEqualTo(Math.Min(LaptopWorkArea.Bottom, button.WindowBottom));
    }

    [Fact]
    public async Task Scroll_WithEightInstallsAndAnUpdateOnALaptopScreen_BringsTheLastInstallOnScreen()
    {
        (double Top, double Bottom, double WindowTop, double WindowBottom) lastInstall = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = EightInstallsAndAnUpdate();
                MainWindow window = ShowWith(viewModel, () => LaptopWorkArea);
                try
                {
                    await WaitForEveryPanel(viewModel);

                    // The list scrolls on its own and keeps a scroll request to itself, so the window
                    // scrolls to the list first.
                    ScreenEdgesAfterScrollingTo(window, window.FindControl<ScrollViewer>("GameFolderCandidatesScroller").ShouldNotBeNull());
                    Button last = window.FindControl<ItemsControl>("GameFolderCandidates").ShouldNotBeNull()
                        .GetVisualDescendants().OfType<Button>()
                        .Last(button => button.DataContext is GameFolderCandidateViewModel);
                    return ScreenEdgesAfterScrollingTo(window, last);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        lastInstall.Top.ShouldBeGreaterThanOrEqualTo(Math.Max(LaptopWorkArea.Y, lastInstall.WindowTop));
        lastInstall.Bottom.ShouldBeLessThanOrEqualTo(Math.Min(LaptopWorkArea.Bottom, lastInstall.WindowBottom));
    }

    [Fact]
    public async Task Focus_OnAnInstallAboveTheView_ScrollsTheWindowToIt()
    {
        (double Top, double Bottom, double WindowTop, double WindowBottom) firstInstall = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = EightInstallsAndAnUpdate();
                MainWindow window = ShowWith(viewModel, () => LaptopWorkArea);
                try
                {
                    await WaitForEveryPanel(viewModel);
                    Scroller(window).ScrollToEnd();
                    window.FindControl<ScrollViewer>("GameFolderCandidatesScroller").ShouldNotBeNull().ScrollToEnd();
                    Dispatcher.UIThread.RunJobs();

                    Button first = window.FindControl<ItemsControl>("GameFolderCandidates").ShouldNotBeNull()
                        .GetVisualDescendants().OfType<Button>()
                        .First(button => button.DataContext is GameFolderCandidateViewModel);
                    // Both scrollers react to the focus, so the window scrolls too, not only the list.
                    first.Focus(NavigationMethod.Tab);
                    Dispatcher.UIThread.RunJobs();
                    return ScreenEdges(window, first);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        firstInstall.Top.ShouldBeGreaterThanOrEqualTo(Math.Max(LaptopWorkArea.Y, firstInstall.WindowTop));
        firstInstall.Bottom.ShouldBeLessThanOrEqualTo(Math.Min(LaptopWorkArea.Bottom, firstInstall.WindowBottom));
    }

    [Fact]
    public async Task Show_WithASavedGameAndNoOfferOnALaptopScreen_KeepsItsHeightWithNoScrollBar()
    {
        (double Unlimited, double OnTheLaptop, double ScrollRange, Size Content, Size Viewport, Size Client) heights =
            await _fixture.Session.Dispatch(
            async () =>
            {
                double unlimitedHeight;
                MainWindowViewModel unlimitedViewModel = CreateViewModel();
                MainWindow unlimited = ShowWith(unlimitedViewModel, () => null);
                try
                {
                    await Settle(unlimitedViewModel);
                    unlimitedHeight = unlimited.ClientSize.Height;
                }
                finally
                {
                    unlimited.Close();
                }

                MainWindowViewModel viewModel = CreateViewModel();
                MainWindow window = ShowWith(viewModel, () => LaptopWorkArea);
                try
                {
                    await Settle(viewModel);
                    ScrollViewer scroller = Scroller(window);
                    return (unlimitedHeight, window.ClientSize.Height, scroller.ScrollBarMaximum.Y, scroller.Extent, scroller.Viewport, window.ClientSize);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        heights.OnTheLaptop.ShouldBe(heights.Unlimited);
        heights.ScrollRange.ShouldBe(0);
        // The scroller adds no padding and keeps no room for a scroll bar, so the content fills the window as before.
        heights.Content.Height.ShouldBe(heights.Client.Height);
        heights.Viewport.Width.ShouldBe(heights.Client.Width);
    }

    [Fact]
    public async Task ScalingChange_OnALaptopScreen_LimitsTheHeightInTheNewUnitsAndMovesTheWindowBackIn()
    {
        (int Top, double Height) shown = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = EightInstallsAndAnUpdate();
                MainWindow window = ShowWith(viewModel, () => LaptopWorkArea);
                try
                {
                    await WaitForEveryPanel(viewModel);
                    window.Position = new PixelPoint(window.Position.X, 200);
                    Dispatcher.UIThread.RunJobs();

                    window.SetRenderScaling(1.5);
                    Dispatcher.UIThread.RunJobs();
                    return (window.Position.Y, window.ClientSize.Height);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        // 690 pixels at 150% are 460 units: the window fills the work area from its top.
        shown.Height.ShouldBe(460, 0.001);
        shown.Top.ShouldBe(LaptopWorkArea.Y);
    }

    [Fact]
    public async Task Grow_AfterAMoveToAnotherScreen_FitsTheWorkAreaOfTheNewScreen()
    {
        PixelRect workArea = LaptopWorkArea;
        PixelRect lowerScreen = new(0, LaptopWorkArea.Bottom, 1920, 690);
        TaskCompletionSource<Result<AppUpdateCheckResponse>> check = new(TaskCreationOptions.RunContinuationsAsynchronously);

        (int Top, double Height) shown = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = ViewModelBuilder.WithASavedGame().MainWindow(
                    ViewModelBuilder.AppUpdate(ViewModelBuilder.CheckAnsweringWhen(check.Task)));
                MainWindow window = ShowWith(viewModel, () => workArea);
                try
                {
                    await viewModel.GameFolder.StartAsync();
                    Dispatcher.UIThread.RunJobs();

                    // Moved low on a second screen under the first one. A move alone never resizes the window.
                    workArea = lowerScreen;
                    window.Position = new PixelPoint(window.Position.X, lowerScreen.Y + 150);
                    Dispatcher.UIThread.RunJobs();

                    check.SetResult(Result.Success(AppUpdateCheckResponse.UpdateAvailable(OfferedUpdate)));
                    await WaitUntil(() => viewModel.AppUpdate.IsPromptVisible);
                    Dispatcher.UIThread.RunJobs();
                    return (window.Position.Y, window.ClientSize.Height);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        shown.Top.ShouldBeGreaterThanOrEqualTo(lowerScreen.Y);
        (shown.Top + shown.Height).ShouldBeLessThanOrEqualTo(lowerScreen.Bottom);
    }

    [Fact]
    public async Task Move_ToASmallerScreen_LimitsTheHeightToTheNewWorkArea()
    {
        PixelRect workArea = new(0, 0, 1920, 1280);
        (int Top, double Height) shown = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = EightInstallsAndAnUpdate();
                MainWindow window = ShowWith(viewModel, () => workArea);
                try
                {
                    await WaitForEveryPanel(viewModel);

                    workArea = LaptopWorkArea;
                    window.Position = new PixelPoint(window.Position.X, 100);
                    Dispatcher.UIThread.RunJobs();
                    return (window.Position.Y, window.ClientSize.Height);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        shown.Top.ShouldBeGreaterThanOrEqualTo(LaptopWorkArea.Y);
        (shown.Top + shown.Height).ShouldBeLessThanOrEqualTo(LaptopWorkArea.Bottom);
    }

    [Fact]
    public async Task Grow_FromTheTopOfTheWorkArea_StaysWithinTheLimitSetWhenTheWindowOpened()
    {
        (int Top, double Height, PixelRect WorkArea) shown = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = EightInstallsAndAnUpdate();
                PixelRect? workArea = null;
                MainWindow? window = null;
                window = new MainWindow
                {
                    DataContext = viewModel,
                    // The window opens at the top of this work area, so growing never moves it, and only the
                    // limit set when it opened keeps it inside.
                    WorkAreaSource = () => workArea ??= new PixelRect(0, window!.Position.Y, 1920, 690)
                };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                try
                {
                    await WaitForEveryPanel(viewModel);
                    return (
                        window.Position.Y,
                        window.ClientSize.Height,
                        workArea ?? throw new InvalidOperationException("The window never asked for its work area."));
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        shown.Top.ShouldBeGreaterThanOrEqualTo(shown.WorkArea.Y);
        (shown.Top + shown.Height).ShouldBeLessThanOrEqualTo(shown.WorkArea.Bottom);
    }

    [Fact]
    public async Task Grow_WhileMinimized_LeavesTheWindowWhereItIs()
    {
        TaskCompletionSource<Result<AppUpdateCheckResponse>> check = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (int Top, double Height) shown = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = ViewModelBuilder.WithASavedGame().MainWindow(
                    ViewModelBuilder.AppUpdate(ViewModelBuilder.CheckAnsweringWhen(check.Task)));
                MainWindow window = ShowWith(viewModel, () => LaptopWorkArea);
                try
                {
                    await viewModel.GameFolder.StartAsync();
                    window.Position = new PixelPoint(window.Position.X, 150);
                    window.WindowState = WindowState.Minimized;
                    Dispatcher.UIThread.RunJobs();

                    check.SetResult(Result.Success(AppUpdateCheckResponse.UpdateAvailable(OfferedUpdate)));
                    await WaitUntil(() => viewModel.AppUpdate.IsPromptVisible);
                    Dispatcher.UIThread.RunJobs();
                    return (window.Position.Y, window.ClientSize.Height);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        // The grown window reaches below the work area. It is fitted when it is restored, not before.
        (150 + shown.Height).ShouldBeGreaterThan(LaptopWorkArea.Bottom);
        shown.Top.ShouldBe(150);
    }

    [Fact]
    public async Task Restore_AfterAGrowthWhileMinimized_MovesTheWindowBackIntoTheWorkArea()
    {
        TaskCompletionSource<Result<AppUpdateCheckResponse>> check = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (int Top, double Height) shown = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = ViewModelBuilder.WithASavedGame().MainWindow(
                    ViewModelBuilder.AppUpdate(ViewModelBuilder.CheckAnsweringWhen(check.Task)));
                MainWindow window = ShowWith(viewModel, () => LaptopWorkArea);

                // Windows reports a restore as a resize first, and as the new state right after it, before any
                // posted job runs.
                EventHandler<WindowResizedEventArgs> restore = (_, _) =>
                    Dispatcher.UIThread.Post(() => window.WindowState = WindowState.Normal, DispatcherPriority.Send);
                try
                {
                    await viewModel.GameFolder.StartAsync();
                    window.Position = new PixelPoint(window.Position.X, 150);
                    window.WindowState = WindowState.Minimized;
                    Dispatcher.UIThread.RunJobs();

                    window.Resized += restore;
                    check.SetResult(Result.Success(AppUpdateCheckResponse.UpdateAvailable(OfferedUpdate)));
                    await WaitUntil(() => viewModel.AppUpdate.IsPromptVisible);
                    Dispatcher.UIThread.RunJobs();
                    return (window.Position.Y, window.ClientSize.Height);
                }
                finally
                {
                    window.Resized -= restore;
                    window.Close();
                }
            },
            CancellationToken.None);

        // At its old top of 150 the grown window would reach below the work area.
        (150 + shown.Height).ShouldBeGreaterThan(LaptopWorkArea.Bottom);
        shown.Top.ShouldBeGreaterThanOrEqualTo(LaptopWorkArea.Y);
        (shown.Top + shown.Height).ShouldBeLessThanOrEqualTo(LaptopWorkArea.Bottom);
    }

    [Fact]
    public async Task ClickNalozSpolszczenie_WhileThePatchRuns_DisablesBothButtonsAndMovesTheBarToHundred()
    {
        ViewModelBuilder builder = ViewModelBuilder.WithASavedGame();
        TaskCompletionSource<Result<PatchIfNeededResponse>> patch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.PatchHandler.Handle(Arg.Any<PatchIfNeededCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<Result<PatchIfNeededResponse>>(patch.Task));

        PatchRunSnapshot run = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = builder.MainWindow();
                MainWindow window = ShowWith(viewModel);
                try
                {
                    await viewModel.GameFolder.StartAsync();
                    Dispatcher.UIThread.RunJobs();
                    Button patchButton = window.FindControl<Button>("PatchButton").ShouldNotBeNull();
                    Button playButton = window.FindControl<Button>("PlayButton").ShouldNotBeNull();
                    ProgressBar bar = window.FindControl<ProgressBar>("PatchProgressBar").ShouldNotBeNull();
                    TextBlock status = window.FindControl<TextBlock>("StatusText").ShouldNotBeNull();

                    patchButton.Command.ShouldNotBeNull().Execute(patchButton.CommandParameter);
                    await WaitUntil(() => viewModel.PatchAndPlay.StatusText == "Nakładam spolszczenie…");

                    // The patcher reports every 1000 rows; this stands in for one report.
                    builder.Progress.Report(new OperationProgress(600, 1000));
                    Dispatcher.UIThread.RunJobs();
                    bool patchEnabledDuring = patchButton.IsEffectivelyEnabled;
                    bool playEnabledDuring = playButton.IsEffectivelyEnabled;
                    string? statusDuring = status.Text;
                    double barDuring = bar.Value;
                    bool percentShownDuring = !bar.IsIndeterminate && window.FindControl<TextBlock>("ProgressText")?.IsVisible == true;

                    patch.SetResult(Result.Success(new PatchIfNeededResponse(true, true, 900, 100, 0, null)));
                    await WaitUntil(() => !viewModel.PatchAndPlay.IsRunning);
                    Dispatcher.UIThread.RunJobs();

                    return new PatchRunSnapshot(
                        patchEnabledDuring,
                        playEnabledDuring,
                        statusDuring,
                        barDuring,
                        percentShownDuring,
                        bar.Value,
                        status.Text,
                        patchButton.IsEffectivelyEnabled);
                }
                finally
                {
                    patch.TrySetResult(Result.Success(new PatchIfNeededResponse(false, false, 0, 0, 0, null)));
                    await WaitUntil(() => !builder.Gate.IsBusy);
                    window.Close();
                }
            },
            CancellationToken.None);

        run.PatchEnabledDuring.ShouldBeFalse();
        run.PlayEnabledDuring.ShouldBeFalse();
        run.StatusDuring.ShouldBe("Nakładam spolszczenie…");
        run.BarDuring.ShouldBe(60);
        run.PercentShownDuring.ShouldBeTrue();
        run.BarAfter.ShouldBe(100);
        run.StatusAfter.ShouldBe("Spolszczenie nałożone.");
        run.EnabledAfter.ShouldBeTrue();
    }

    [Fact]
    public async Task ClickGraj_WhileTheLaunchRuns_ShowsItsStepWithAMovingBarAndEndsWithTheLaunchOutcome()
    {
        ViewModelBuilder builder = ViewModelBuilder.WithASavedGame();
        TaskCompletionSource<Result<GameLaunchingResponse>> launch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.LaunchHandler.Handle(Arg.Any<GameLaunchingCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<Result<GameLaunchingResponse>>(launch.Task));

        PlayRunSnapshot run = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = builder.MainWindow();
                MainWindow window = ShowWith(viewModel);
                try
                {
                    await viewModel.GameFolder.StartAsync();
                    Dispatcher.UIThread.RunJobs();
                    Button patchButton = window.FindControl<Button>("PatchButton").ShouldNotBeNull();
                    Button playButton = window.FindControl<Button>("PlayButton").ShouldNotBeNull();
                    ProgressBar bar = window.FindControl<ProgressBar>("PatchProgressBar").ShouldNotBeNull();
                    TextBlock percent = window.FindControl<TextBlock>("ProgressText").ShouldNotBeNull();
                    TextBlock status = window.FindControl<TextBlock>("StatusText").ShouldNotBeNull();

                    playButton.Command.ShouldNotBeNull().Execute(playButton.CommandParameter);
                    await WaitUntil(() => builder.LaunchHandler.ReceivedCalls().Any());
                    Dispatcher.UIThread.RunJobs();
                    string? statusDuring = status.Text;
                    bool indeterminateDuring = bar.IsIndeterminate;
                    bool percentVisibleDuring = percent.IsVisible;
                    bool patchEnabledDuring = patchButton.IsEffectivelyEnabled;
                    bool playEnabledDuring = playButton.IsEffectivelyEnabled;

                    // An answer only the launch gives: a Patch run would show the patch handler's outcome.
                    launch.SetResult(Result.Success(new GameLaunchingResponse(null, false, 0, TranslationsApplied: true, AppliedCount: 10)));
                    await WaitUntil(() => !viewModel.PatchAndPlay.IsRunning);
                    Dispatcher.UIThread.RunJobs();

                    return new PlayRunSnapshot(
                        statusDuring,
                        indeterminateDuring,
                        percentVisibleDuring,
                        patchEnabledDuring,
                        playEnabledDuring,
                        status.Text,
                        bar.Value);
                }
                finally
                {
                    launch.TrySetResult(Result.Success(new GameLaunchingResponse(null, false, 0)));
                    await WaitUntil(() => !builder.Gate.IsBusy);
                    window.Close();
                }
            },
            CancellationToken.None);

        run.StatusDuring.ShouldBe("Sprawdzam spolszczenie i uruchamiam grę…");
        run.IndeterminateDuring.ShouldBeTrue();
        run.PercentVisibleDuring.ShouldBeFalse();
        run.PatchEnabledDuring.ShouldBeFalse();
        run.PlayEnabledDuring.ShouldBeFalse();
        run.StatusAfter.ShouldBe("Spolszczenie nałożone. Uruchomiono launcher gry.");
        run.BarAfter.ShouldBe(100);
    }

    [Fact]
    public async Task Show_WhenAnotherOperationTakesTheGateLater_GreysOutBothButtonsUntilItEnds()
    {
        (bool PatchDuring, bool PlayDuring, bool PatchAfter, bool PlayAfter) buttons = await _fixture.Session.Dispatch(
            async () =>
            {
                ViewModelBuilder builder = ViewModelBuilder.WithASavedGame();
                MainWindowViewModel viewModel = builder.MainWindow();
                MainWindow window = ShowWith(viewModel);
                try
                {
                    await viewModel.GameFolder.StartAsync();
                    Dispatcher.UIThread.RunJobs();
                    Button patchButton = window.FindControl<Button>("PatchButton").ShouldNotBeNull();
                    Button playButton = window.FindControl<Button>("PlayButton").ShouldNotBeNull();

                    // An update takes the gate after the window is on screen.
                    builder.Gate.TryEnter();
                    Dispatcher.UIThread.RunJobs();
                    (bool Patch, bool Play) during = (patchButton.IsEffectivelyEnabled, playButton.IsEffectivelyEnabled);

                    builder.Gate.Exit();
                    Dispatcher.UIThread.RunJobs();
                    return (during.Patch, during.Play, patchButton.IsEffectivelyEnabled, playButton.IsEffectivelyEnabled);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        buttons.PatchDuring.ShouldBeFalse();
        buttons.PlayDuring.ShouldBeFalse();
        buttons.PatchAfter.ShouldBeTrue();
        buttons.PlayAfter.ShouldBeTrue();
    }

    [Fact]
    public async Task ClickNalozSpolszczenie_WhileTheDownloadRuns_MovesTheBarWithNoPercent()
    {
        ViewModelBuilder builder = ViewModelBuilder.WithASavedGame();
        TaskCompletionSource<Result<TranslationFileSyncResponse>> download = new(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.SyncHandler.Handle(Arg.Any<SyncTranslationFileCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<Result<TranslationFileSyncResponse>>(download.Task));

        DownloadSnapshot run = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = builder.MainWindow();
                MainWindow window = ShowWith(viewModel);
                try
                {
                    await viewModel.GameFolder.StartAsync();
                    Dispatcher.UIThread.RunJobs();
                    Button patchButton = window.FindControl<Button>("PatchButton").ShouldNotBeNull();
                    ProgressBar bar = window.FindControl<ProgressBar>("PatchProgressBar").ShouldNotBeNull();
                    TextBlock percent = window.FindControl<TextBlock>("ProgressText").ShouldNotBeNull();

                    patchButton.Command.ShouldNotBeNull().Execute(patchButton.CommandParameter);
                    await WaitUntil(() => bar.IsIndeterminate);
                    bool indeterminateDuring = bar.IsIndeterminate;
                    bool percentVisibleDuring = percent.IsVisible;
                    string? statusDuring = window.FindControl<TextBlock>("StatusText")?.Text;

                    download.SetResult(Result.Success(new TranslationFileSyncResponse(TranslationFileSyncOutcome.Updated, null)));
                    await WaitUntil(() => !viewModel.PatchAndPlay.IsRunning);
                    Dispatcher.UIThread.RunJobs();

                    return new DownloadSnapshot(indeterminateDuring, percentVisibleDuring, statusDuring, bar.IsIndeterminate, percent.IsVisible);
                }
                finally
                {
                    download.TrySetResult(Result.Success(new TranslationFileSyncResponse(TranslationFileSyncOutcome.UpToDate, null)));
                    await WaitUntil(() => !builder.Gate.IsBusy);
                    window.Close();
                }
            },
            CancellationToken.None);

        run.IndeterminateDuring.ShouldBeTrue();
        run.PercentVisibleDuring.ShouldBeFalse();
        run.StatusDuring.ShouldBe("Przygotowuję plik spolszczenia…");
        run.IndeterminateAfter.ShouldBeFalse();
        run.PercentVisibleAfter.ShouldBeTrue();
    }

    [Fact]
    public async Task Close_WhileThePatchRuns_IsRefusedWithAPolishLineAndWorksAfterIt()
    {
        ViewModelBuilder builder = ViewModelBuilder.WithASavedGame();
        TaskCompletionSource<Result<PatchIfNeededResponse>> patch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.PatchHandler.Handle(Arg.Any<PatchIfNeededCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<Result<PatchIfNeededResponse>>(patch.Task));

        CloseSnapshot close = await _fixture.Session.Dispatch(
            async () =>
            {
                MainWindowViewModel viewModel = builder.MainWindow();
                MainWindow window = ShowWith(viewModel);
                bool closed = false;
                window.Closed += (_, _) => closed = true;
                try
                {
                    await viewModel.GameFolder.StartAsync();
                    Dispatcher.UIThread.RunJobs();
                    _ = viewModel.PatchAndPlay.PatchAsync();
                    await WaitUntil(() => viewModel.PatchAndPlay.StatusText == "Nakładam spolszczenie…");

                    window.Close();
                    Dispatcher.UIThread.RunJobs();
                    bool openAfterRefusedClose = !closed;
                    string? status = window.FindControl<TextBlock>("StatusText")?.Text;

                    patch.SetResult(Result.Success(new PatchIfNeededResponse(false, false, 0, 0, 0, null)));
                    await WaitUntil(() => !viewModel.PatchAndPlay.IsRunning);
                    window.Close();
                    Dispatcher.UIThread.RunJobs();

                    return new CloseSnapshot(openAfterRefusedClose, status, closed);
                }
                finally
                {
                    patch.TrySetResult(Result.Success(new PatchIfNeededResponse(false, false, 0, 0, 0, null)));
                    await WaitUntil(() => !builder.Gate.IsBusy);
                    window.Close();
                }
            },
            CancellationToken.None);

        close.OpenAfterRefusedClose.ShouldBeTrue();
        close.Status.ShouldBe("Poczekaj, aż ta operacja się skończy. Potem zamknij okno.");
        close.ClosedAfterTheRun.ShouldBeTrue();
    }

    public MainWindowTests(HeadlessSessionFixture fixture)
    {
        _fixture = fixture;
    }

    private static MainWindowViewModel CreateViewModel() => ViewModelBuilder.WithASavedGame().MainWindow();

    /// <summary>The ticket's tallest case (#1463): the list of found copies and the update offer at once.</summary>
    private static MainWindowViewModel EightInstallsAndAnUpdate() =>
        new ViewModelBuilder()
            .Locating(Enumerable.Range(1, 8)
                .Select(copy => ViewModelBuilder.Location(ViewModelBuilder.Rooted($"Kopia{copy}", "LOTRO"), DatFileSource.DiskScan))
                .ToArray())
            .MainWindow(ViewModelBuilder.AppUpdate(
                ViewModelBuilder.CheckAnswering(AppUpdateCheckResponse.UpdateAvailable(OfferedUpdate))));

    /// <summary>With no work area the window keeps the headless platform's one fixed screen.</summary>
    private static MainWindow ShowWith(MainWindowViewModel viewModel, Func<PixelRect?>? workArea = null)
    {
        MainWindow window = new() { DataContext = viewModel, WorkAreaSource = workArea };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static async Task WaitForEveryPanel(MainWindowViewModel viewModel)
    {
        await WaitUntil(() => viewModel.GameFolder.IsCandidateListVisible && viewModel.AppUpdate.IsPromptVisible);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Both start-up tasks have ended, so the window has its everyday height.</summary>
    private static async Task Settle(MainWindowViewModel viewModel)
    {
        await viewModel.GameFolder.StartAsync();
        await viewModel.AppUpdate.CheckAsync();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A missing scroller fails the test at its first use.</summary>
    private static ScrollViewer Scroller(MainWindow window) => window.FindControl<ScrollViewer>("WindowScroller")!;

    private static (double Top, double Bottom, double WindowTop, double WindowBottom) ScreenEdgesAfterScrollingTo(
        MainWindow window, Control control)
    {
        control.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        return ScreenEdges(window, control);
    }

    /// <summary>
    /// The control's edges and the window's visible part, on screen. On the headless platform the scaling is 1
    /// and there is no title bar, so window units are screen pixels.
    /// </summary>
    private static (double Top, double Bottom, double WindowTop, double WindowBottom) ScreenEdges(MainWindow window, Control control)
    {
        Point topLeft = control.TranslatePoint(new Point(0, 0), window)!.Value;
        double top = window.Position.Y + topLeft.Y;
        return (top, top + control.Bounds.Height, window.Position.Y, window.Position.Y + window.ClientSize.Height);
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

    private sealed record PatchRunSnapshot(
        bool PatchEnabledDuring,
        bool PlayEnabledDuring,
        string? StatusDuring,
        double BarDuring,
        bool PercentShownDuring,
        double BarAfter,
        string? StatusAfter,
        bool EnabledAfter);

    private sealed record DownloadSnapshot(
        bool IndeterminateDuring,
        bool PercentVisibleDuring,
        string? StatusDuring,
        bool IndeterminateAfter,
        bool PercentVisibleAfter);

    private sealed record PlayRunSnapshot(
        string? StatusDuring,
        bool IndeterminateDuring,
        bool PercentVisibleDuring,
        bool PatchEnabledDuring,
        bool PlayEnabledDuring,
        string? StatusAfter,
        double BarAfter);

    private sealed record CloseSnapshot(bool OpenAfterRefusedClose, string? Status, bool ClosedAfterTheRun);
}
