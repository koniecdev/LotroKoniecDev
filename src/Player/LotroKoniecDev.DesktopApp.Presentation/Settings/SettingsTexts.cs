using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.DesktopApp.Presentation.Settings;

/// <summary>The Polish texts about the settings file and the settings window (owner decision 1 on #43).</summary>
internal static class SettingsTexts
{
    public const string FileUnreadable =
        "Nie udało się odczytać pliku ustawień, więc program używa ustawień domyślnych. " +
        "Przy pierwszym zapisie odłoży ten plik obok, pod nazwą zaczynającą się od config.json.broken.";

    public const string FileBroken =
        "Plik ustawień jest uszkodzony, więc program używa ustawień domyślnych. " +
        "Przy pierwszym zapisie odłoży ten plik obok, pod nazwą zaczynającą się od config.json.broken.";

    public const string FileSetAside =
        "Nie dało się użyć pliku ustawień, więc program zaczął od ustawień domyślnych. " +
        "Stary plik leży obok config.json, pod nazwą zaczynającą się od config.json.broken.";

    public const string GameFolderDropped =
        "Folder gry w pliku ustawień nie jest pełną ścieżką, więc program go pominął.";

    public const string TranslationFileDropped =
        "Ścieżka do własnego pliku tłumaczenia w pliku ustawień nie jest pełną ścieżką, więc program ją pominął.";

    public const string AutoPatchDropped =
        "Ustawienie nakładania spolszczenia w pliku ustawień jest nieprawidłowe, więc program używa ustawienia domyślnego (włączone).";

    public const string ServerAddressDropped =
        "Adres serwera w pliku ustawień jest nieprawidłowy, więc program używa adresu domyślnego.";

    public const string NoGameFolder =
        $"Wskaż folder gry, czyli folder z plikiem {DatFileConstants.DatFileName}.";

    public const string TranslationFileMissingOnSave = "Nie ma takiego pliku tłumaczenia. Wskaż istniejący plik albo zostaw pole puste.";

    public const string ServerAddressRefused =
        "Adres serwera musi zaczynać się od https:// (http:// tylko dla localhost).";

    public const string SaveFailed =
        "Nie udało się zapisać pliku ustawień. Zmiany działają do zamknięcia programu.";

    public const string PickerFailed = "Nie udało się otworzyć okna wyboru.";

    public static string TranslationFileMissing(string path) =>
        $"Nie ma już pliku tłumaczenia {path}, więc program go pominął.";

    public static string NoGameIn(string folder) =>
        $"W folderze {folder} nie ma pliku {DatFileConstants.DatFileName}. Wskaż folder gry LOTRO.";
}
