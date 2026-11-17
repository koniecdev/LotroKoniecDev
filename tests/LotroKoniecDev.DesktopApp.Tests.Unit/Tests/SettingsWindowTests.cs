using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using LotroKoniecDev.DesktopApp.Presentation.Views;
using LotroKoniecDev.DesktopApp.Tests.Unit.Shared;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

/// <remarks>Every window closes in a <c>finally</c>: the session is shared.</remarks>
[Collection(HeadlessSessionCollection.Name)]
public sealed class SettingsWindowTests
{
    private readonly HeadlessSessionFixture _fixture;

    [Fact]
    public async Task Show_WithTheSettingsInUse_FillsEveryFieldUnderPolishLabels()
    {
        (string? Title, List<string?> Labels, string? GameFolder, bool? AutoPatch, object? Save, object? Cancel) shown =
            await _fixture.Session.Dispatch(
                () =>
                {
                    SettingsWindow window = ShowWith(Editing(ViewModelBuilder.WithASavedGame()));
                    try
                    {
                        return (
                            window.Title,
                            window.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text).ToList(),
                            window.FindControl<TextBox>("GameFolderBox")?.Text,
                            window.FindControl<CheckBox>("AutoPatchBox")?.IsChecked,
                            window.FindControl<Button>("SaveSettingsButton")?.Content,
                            window.FindControl<Button>("CancelSettingsButton")?.Content);
                    }
                    finally
                    {
                        window.Close();
                    }
                },
                CancellationToken.None);

        shown.Title.ShouldBe("Ustawienia");
        shown.Labels.ShouldContain("Folder gry");
        shown.Labels.ShouldContain("Własny plik polish.txt (opcjonalnie)");
        shown.Labels.ShouldContain("Adres serwera tłumaczeń");
        shown.GameFolder.ShouldBe(ViewModelBuilder.SavedGameFolder);
        shown.AutoPatch.ShouldBe(true);
        shown.Save.ShouldBe("Zapisz");
        shown.Cancel.ShouldBe("Anuluj");
    }

    [Fact]
    public async Task ClickZapisz_WithValidValues_ClosesTheWindow()
    {
        bool stillOpen = await _fixture.Session.Dispatch(
            () =>
            {
                SettingsWindow window = ShowWith(Editing(ViewModelBuilder.WithASavedGame()));
                try
                {
                    Click(window, "SaveSettingsButton");
                    return window.IsVisible;
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        stillOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task ClickZapisz_WithAFolderWithoutTheGame_ShowsWhyAndStaysOpen()
    {
        string emptyFolder = ViewModelBuilder.Rooted("Puste");
        (bool StillOpen, bool? ErrorVisible, string? Error) state = await _fixture.Session.Dispatch(
            () =>
            {
                SettingsViewModel viewModel = Editing(ViewModelBuilder.WithASavedGame());
                viewModel.GameFolder = emptyFolder;
                SettingsWindow window = ShowWith(viewModel);
                try
                {
                    Click(window, "SaveSettingsButton");
                    TextBlock? error = window.FindControl<TextBlock>("SettingsErrorText");
                    return (window.IsVisible, error?.IsVisible, error?.Text);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        state.StillOpen.ShouldBeTrue();
        state.ErrorVisible.ShouldBe(true);
        state.Error.ShouldNotBeNull().ShouldStartWith($"W folderze {emptyFolder} nie ma pliku");
    }

    [Fact]
    public async Task ClickAnuluj_Always_ClosesTheWindow()
    {
        bool stillOpen = await _fixture.Session.Dispatch(
            () =>
            {
                SettingsWindow window = ShowWith(Editing(ViewModelBuilder.WithASavedGame()));
                try
                {
                    Click(window, "CancelSettingsButton");
                    return window.IsVisible;
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        stillOpen.ShouldBeFalse();
    }

    public SettingsWindowTests(HeadlessSessionFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>The settings as the main window hands them over: loaded, then copied into the fields.</summary>
    private static SettingsViewModel Editing(ViewModelBuilder builder)
    {
        builder.Store.Load();
        SettingsViewModel viewModel = builder.Settings();
        viewModel.BeginEdit();
        return viewModel;
    }

    private static SettingsWindow ShowWith(SettingsViewModel viewModel)
    {
        SettingsWindow window = new() { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Click(Window window, string buttonName)
    {
        Button button = window.FindControl<Button>(buttonName).ShouldNotBeNull();
        button.Command.ShouldNotBeNull().Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();
    }
}
