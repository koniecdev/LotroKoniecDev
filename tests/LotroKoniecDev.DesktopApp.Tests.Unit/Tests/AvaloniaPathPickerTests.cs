using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LotroKoniecDev.DesktopApp.Presentation;
using LotroKoniecDev.DesktopApp.Tests.Unit.Shared;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

/// <summary>
/// The real dialog adapter on the headless platform. A headless window has no system dialog, so the
/// tests cover every way the adapter answers "no pick"; a real pick over the active window is checked
/// on Windows.
/// </summary>
[Collection(HeadlessSessionCollection.Name)]
public sealed class AvaloniaPathPickerTests
{
    private readonly HeadlessSessionFixture _fixture;

    [Fact]
    public async Task PickGameFolderAsync_InTheHeadlessAppWithNoDesktopLifetime_ReturnsNull()
    {
        // The public constructor reads the running app, which on the headless platform has no lifetime.
        string? picked = await _fixture.Session.Dispatch(
            async () => await new AvaloniaPathPicker().PickGameFolderAsync(),
            CancellationToken.None);

        picked.ShouldBeNull();
    }

    [Fact]
    public async Task PickTranslationFileAsync_WithALifetimeThatIsNotADesktop_ReturnsNull()
    {
        string? picked = await _fixture.Session.Dispatch(
            async () => await new AvaloniaPathPicker(() => Substitute.For<IApplicationLifetime>()).PickTranslationFileAsync(),
            CancellationToken.None);

        picked.ShouldBeNull();
    }

    [Fact]
    public async Task PickGameFolderAsync_WithNoWindowOpen_ReturnsNull()
    {
        string? picked = await _fixture.Session.Dispatch(
            async () =>
            {
                IClassicDesktopStyleApplicationLifetime desktop = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
                desktop.Windows.Returns([]);
                return await new AvaloniaPathPicker(() => desktop).PickGameFolderAsync();
            },
            CancellationToken.None);

        picked.ShouldBeNull();
    }

    [Fact]
    public async Task PickGameFolderAsync_OverAWindowThatCannotOpenADialog_ReturnsNull()
    {
        (bool CanPickFolder, string? Picked) state = await _fixture.Session.Dispatch(
            async () =>
            {
                Window window = new();
                window.Show();
                Dispatcher.UIThread.RunJobs();
                try
                {
                    IClassicDesktopStyleApplicationLifetime desktop = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
                    desktop.Windows.Returns([window]);
                    desktop.MainWindow.Returns(window);
                    string? picked = await new AvaloniaPathPicker(() => desktop).PickGameFolderAsync();
                    return (window.StorageProvider.CanPickFolder, picked);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        state.CanPickFolder.ShouldBeFalse();
        state.Picked.ShouldBeNull();
    }

    [Fact]
    public async Task PickTranslationFileAsync_OverAWindowThatCannotOpenADialog_ReturnsNull()
    {
        (bool CanOpen, string? Picked) state = await _fixture.Session.Dispatch(
            async () =>
            {
                Window window = new();
                window.Show();
                Dispatcher.UIThread.RunJobs();
                try
                {
                    IClassicDesktopStyleApplicationLifetime desktop = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
                    desktop.Windows.Returns([window]);
                    desktop.MainWindow.Returns(window);
                    string? picked = await new AvaloniaPathPicker(() => desktop).PickTranslationFileAsync();
                    return (window.StorageProvider.CanOpen, picked);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        state.CanOpen.ShouldBeFalse();
        state.Picked.ShouldBeNull();
    }

    [Fact]
    public void FirstLocalPath_WithALocalFolder_ReturnsItsPathAndDisposesEveryItem()
    {
        // Arrange
        string folder = ViewModelBuilder.Rooted("Gry", "LOTRO");
        IStorageFolder first = Item<IStorageFolder>(new Uri(folder));
        IStorageFolder second = Item<IStorageFolder>(new Uri(ViewModelBuilder.Rooted("Inne")));

        // Act
        string? path = AvaloniaPathPicker.FirstLocalPath([first, second]);

        // Assert
        path.ShouldBe(folder);
        first.Received(1).Dispose();
        second.Received(1).Dispose();
    }

    [Fact]
    public void FirstLocalPath_WithAnItemThatHasNoLocalPath_ReturnsNull()
    {
        // Arrange: a phone or a cloud library hands back an item that is not a file on this disk.
        IStorageFile item = Item<IStorageFile>(new Uri("content://com.example.provider/polish.txt"));

        // Act
        string? path = AvaloniaPathPicker.FirstLocalPath([item]);

        // Assert
        path.ShouldBeNull();
        item.Received(1).Dispose();
    }

    [Fact]
    public void FirstLocalPath_WithNoPick_ReturnsNull()
    {
        // Act
        string? path = AvaloniaPathPicker.FirstLocalPath([]);

        // Assert
        path.ShouldBeNull();
    }

    public AvaloniaPathPickerTests(HeadlessSessionFixture fixture)
    {
        _fixture = fixture;
    }

    private static T Item<T>(Uri path)
        where T : class, IStorageItem
    {
        T item = Substitute.For<T>();
        item.Path.Returns(path);
        return item;
    }
}
