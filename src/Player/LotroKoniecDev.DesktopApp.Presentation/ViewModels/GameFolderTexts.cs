using LotroKoniecDev.Primitives.Constants;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>The first-run panel's Polish texts (owner decision 1 on #43). The locator's English texts never reach the player.</summary>
internal static class GameFolderTexts
{
    public const string Searching = "Szukam gry na tym komputerze…";

    public const string ChooseByHand =
        $"Wskaż folder gry LOTRO, czyli folder z plikiem {DatFileConstants.DatFileName}.";

    public const string NotFound =
        $"Nie znaleziono gry. Wskaż folder gry LOTRO, czyli folder z plikiem {DatFileConstants.DatFileName}.";

    public const string SeveralFound = "Znaleziono kilka instalacji gry. Wybierz swoją:";

    /// <summary>ADR-0069: how the folder was found is the only guard before the launcher starts.</summary>
    public const string Unchecked = "Program nie sprawdza plików w tym folderze, a uruchomi z niego launcher gry.";

    public const string CandidateWarning =
        "Program nie sprawdza plików w tym folderze. Wybierz go tylko wtedy, gdy to Twoja gra.";

    public const string SaveFailed =
        "Nie udało się zapisać ustawień. Wybrany folder działa do zamknięcia programu.";

    public const string PickerFailed = "Nie udało się otworzyć okna wyboru folderu.";

    public static string FoundOne(string folder, DatFileSource source) =>
        $"Znaleziono grę w folderze {folder} ({SourceLabel(source)}). {Unchecked} Czy to Twoja instalacja LOTRO?";

    public static string FoundInstead(string lostFolder, string folder, DatFileSource source, bool needsConfirmation) =>
        $"W zapisanym folderze {lostFolder} nie ma już gry. Znaleziono ją w folderze {folder} ({SourceLabel(source)}). " +
        (needsConfirmation ? $"{Unchecked} " : string.Empty) +
        "Użyć nowego folderu?";

    public static string SeveralFoundInstead(string lostFolder) =>
        $"W zapisanym folderze {lostFolder} nie ma już gry. Wybierz jedną ze znalezionych instalacji:";

    public static string LostAndNotFound(string lostFolder) =>
        $"W zapisanym folderze {lostFolder} nie ma już gry i nie znaleziono jej gdzie indziej. {ChooseByHand}";

    public static string Lost(string lostFolder) =>
        $"W zapisanym folderze {lostFolder} nie ma już gry. {ChooseByHand}";

    public static string NoGameIn(string folder) =>
        $"W folderze {folder} nie ma pliku {DatFileConstants.DatFileName}. Wskaż folder gry LOTRO.";

    public static string SourceLabel(DatFileSource source) => source switch
    {
        DatFileSource.StandingStoneGames => "domyślny folder Standing Stone Games",
        DatFileSource.Steam => "Steam",
        DatFileSource.Registry => "wpis w rejestrze Windows",
        DatFileSource.DiskScan => "znaleziony na dysku",
        DatFileSource.LocalFallback => "folder data obok miejsca startu programu",
        _ => "inne źródło"
    };
}
