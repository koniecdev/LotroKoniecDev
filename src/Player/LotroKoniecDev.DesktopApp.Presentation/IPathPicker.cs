namespace LotroKoniecDev.DesktopApp.Presentation;

/// <summary>The system dialogs that let the player pick a path. Each answers <c>null</c> when the player cancels.</summary>
public interface IPathPicker
{
    Task<string?> PickGameFolderAsync();

    Task<string?> PickTranslationFileAsync();
}
