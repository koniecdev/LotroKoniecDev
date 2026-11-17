using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace LotroKoniecDev.DesktopApp.Presentation;

/// <summary>
/// Opens the dialog over the window the player is using, the settings window included. It looks the
/// window up on every call: DI builds this object before Avalonia starts.
/// </summary>
internal sealed class AvaloniaPathPicker : IPathPicker
{
    private static readonly FilePickerFileType TextFiles = new("Plik tłumaczenia (*.txt)") { Patterns = ["*.txt"] };

    private readonly Func<IApplicationLifetime?> _currentLifetime;

    public async Task<string?> PickGameFolderAsync()
    {
        IStorageProvider? provider = ActiveStorageProvider();
        if (provider is not { CanPickFolder: true })
        {
            return null;
        }

        IReadOnlyList<IStorageFolder> folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Wybierz folder gry LOTRO",
            AllowMultiple = false
        });

        return FirstLocalPath(folders);
    }

    public async Task<string?> PickTranslationFileAsync()
    {
        IStorageProvider? provider = ActiveStorageProvider();
        if (provider is not { CanOpen: true })
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Wybierz plik polish.txt",
            AllowMultiple = false,
            FileTypeFilter = [TextFiles]
        });

        return FirstLocalPath(files);
    }

    /// <summary>A pick that has no local path, such as a cloud item, counts as no pick.</summary>
    internal static string? FirstLocalPath(IReadOnlyList<IStorageItem> items)
    {
        try
        {
            return items.Count > 0 ? items[0].TryGetLocalPath() : null;
        }
        finally
        {
            foreach (IStorageItem item in items)
            {
                item.Dispose();
            }
        }
    }

    public AvaloniaPathPicker()
        : this(() => Avalonia.Application.Current?.ApplicationLifetime)
    {
    }

    /// <summary>For tests: Avalonia refuses a new lifetime once the app has started.</summary>
    internal AvaloniaPathPicker(Func<IApplicationLifetime?> currentLifetime)
    {
        _currentLifetime = currentLifetime;
    }

    private IStorageProvider? ActiveStorageProvider()
    {
        if (_currentLifetime() is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        Window? window = desktop.Windows.FirstOrDefault(candidate => candidate.IsActive) ?? desktop.MainWindow;
        return window?.StorageProvider;
    }
}
