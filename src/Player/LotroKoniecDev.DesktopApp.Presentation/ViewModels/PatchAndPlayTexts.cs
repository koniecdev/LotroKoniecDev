using LotroKoniecDev.Application.Features.GameLaunching;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Errors;

namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>
/// The Patch and Play status texts (every text in the app is Polish, owner decision on #42). The
/// patcher's English errors go to the log, never to the player.
/// </summary>
internal static class PatchAndPlayTexts
{
    public const string Ready = "Gotowe.";

    public const string Preparing = "Przygotowuję plik spolszczenia…";

    public const string Patching = "Nakładam spolszczenie…";

    public const string Launching = "Sprawdzam spolszczenie i uruchamiam grę…";

    public const string LaunchingWithoutPatch = "Uruchamiam grę…";

    public const string Patched = "Spolszczenie nałożone.";

    /// <summary>
    /// Says only that the file did not change: a game file swapped for an unpatched copy of the same
    /// version is seen neither by the hash nor by the Tier 0 check.
    /// </summary>
    public const string Unchanged = "Plik spolszczenia się nie zmienił, więc pliki gry zostały bez zmian.";

    public const string Repaired = "Gra przywróciła część angielskich tekstów. Spolszczenie nałożono na nie ponownie.";

    /// <summary>The file parsed, but every row was skipped: not approved, no source digest, or its English moved.</summary>
    public const string NothingApplied = "Wczytano plik spolszczenia, ale nie nałożono z niego żadnego tekstu. " + LogHint;

    public const string RepairFailed =
        "Gra przywróciła część angielskich tekstów, ale nie udało się ponownie nałożyć na nie spolszczenia. " + LogHint;

    /// <summary>The launch does not wait for the game, so the app never knows when it closes.</summary>
    public const string LauncherStarted = "Uruchomiono launcher gry.";

    public const string PatchedAndLauncherStarted = "Spolszczenie nałożone. Uruchomiono launcher gry.";

    public const string LauncherStartedNothingApplied =
        "Uruchomiono launcher gry, ale z pliku spolszczenia nie nałożono żadnego tekstu. " + LogHint;

    public const string LauncherStartedRepairFailed =
        "Uruchomiono launcher gry, ale część tekstów będzie po angielsku: nie udało się ponownie nałożyć spolszczenia. " + LogHint;

    public const string UsedEarlierDownload = "Nie udało się pobrać nowego spolszczenia, więc użyto pobranego wcześniej.";

    public const string NoDownloadYet =
        "Nie udało się pobrać spolszczenia, a na tym komputerze nie ma jeszcze pobranego pliku. " +
        "Sprawdź połączenie z internetem i adres serwera w „Ustawieniach”, a potem spróbuj ponownie.";

    public const string GameFileMissing = "Nie znaleziono pliku gry w wybranym folderze. Sprawdź folder gry w „Ustawieniach”.";

    public const string GameRunning = "Gra albo jej launcher już działa. Zamknij je i spróbuj ponownie.";

    public const string LauncherNotFound =
        "Nie znaleziono pliku LotroLauncher.exe w folderze gry. Sprawdź folder gry w „Ustawieniach”.";

    public const string LauncherStartFailed = "Nie udało się uruchomić launchera gry. " + LogHint;

    public const string PatchFailed = "Nie udało się nałożyć spolszczenia. " + LogHint;

    public const string WorkspaceFailed = "Nie udało się przygotować plików spolszczenia w folderze programu. " + LogHint;

    public const string Failed = "Coś poszło nie tak. " + LogHint;

    public const string CloseRefused = "Poczekaj, aż ta operacja się skończy. Potem zamknij okno.";

    private const string LogHint = "Szczegóły są w pliku data\\LotroPoPolsku.log w folderze programu.";

    /// <summary>Owner decision 3 of 2026-11-18: name the file, say how to clear it, never fall back to the download.</summary>
    public static string OverrideMissing(string path) =>
        $"Nie znaleziono pliku {path}, wybranego w „Ustawieniach”. Podłącz dysk z tym plikiem albo wyczyść pole " +
        "„Własny plik polish.txt” w „Ustawieniach”, a program pobierze spolszczenie z serwera.";

    /// <summary>The server confirmed a download that is no longer on disk, so it sends nothing new.</summary>
    public static string DownloadMissing(string path) =>
        $"Brakuje pobranego pliku spolszczenia {path}. Usuń też plik {path}.etag i spróbuj ponownie, " +
        "a program pobierze spolszczenie od nowa.";

    public static string ForError(Error error) => error.Code switch
    {
        DomainErrors.GameLaunch.AlreadyRunningCode => GameRunning,
        DomainErrors.GameLaunch.LauncherNotFoundCode => LauncherNotFound,
        DomainErrors.GameLaunch.LaunchFailedCode => LauncherStartFailed,
        DomainErrors.GameLaunch.RepatchFailedCode => PatchFailed,
        DomainErrors.DesktopPatchWorkspace.FileSystemErrorCode => WorkspaceFailed,
        _ => Failed
    };

    public static string PatchOutcome(PatchIfNeededResponse response) => response switch
    {
        { RepairFailure: not null } => RepairFailed,
        { TranslationsApplied: true, AppliedCount: 0 } => NothingApplied,
        { TranslationChanged: true } => Patched,
        { TranslationsApplied: true } => Repaired,
        _ => Unchanged
    };

    /// <summary>A Patch that wrote nothing it should have ends with the bar at 0, like any failure.</summary>
    public static bool IsFailedPatch(PatchIfNeededResponse response) =>
        response is { RepairFailure: not null } or { TranslationsApplied: true, AppliedCount: 0 };

    public static string LaunchOutcome(GameLaunchingResponse response) => response switch
    {
        { RepairFailure: not null } => LauncherStartedRepairFailed,
        { TranslationsApplied: true, AppliedCount: 0 } => LauncherStartedNothingApplied,
        { TranslationsApplied: true } => PatchedAndLauncherStarted,
        _ => LauncherStarted
    };

    public static string WithEarlierDownloadNote(string text, bool usedEarlierDownload) =>
        usedEarlierDownload ? $"{text} {UsedEarlierDownload}" : text;
}
