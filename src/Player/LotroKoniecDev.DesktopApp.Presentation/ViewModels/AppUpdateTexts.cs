using LotroKoniecDev.Application.Features.AppUpdating;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Errors;

namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>The update panel's Polish texts (every text in the app is Polish, #42 and #43).</summary>
internal static class AppUpdateTexts
{
    public const string InstallerStarted = "Uruchomiono instalator. Program zaraz się zamknie.";

    public static string Prompt(DesktopVersion version) => $"Dostępna jest wersja {version}. Zaktualizować?";

    public static string Downloading(DesktopVersion version) => $"Pobieram wersję {version}…";

    public static string CheckFailed(Error error, string installedVersion) =>
        $"Nie udało się sprawdzić aktualizacji: {Reason(error)}. Program działa dalej w wersji {installedVersion}.";

    public static string InstallFailed(Error error, string installedVersion) =>
        $"Aktualizacja się nie udała: {Reason(error)}. Program działa dalej w wersji {installedVersion}.";

    private static string Reason(Error error) => error.Code switch
    {
        DomainErrors.AppUpdate.NetworkErrorCode => "brak połączenia z GitHubem",
        DomainErrors.AppUpdate.TimedOutCode => "GitHub nie odpowiedział na czas",
        DomainErrors.AppUpdate.RateLimitedCode => "GitHub chwilowo ogranicza liczbę zapytań, spróbuj później",
        DomainErrors.AppUpdate.UnexpectedStatusCode => "GitHub odpowiedział błędem",
        DomainErrors.AppUpdate.HostRefusedCode => "odpowiedź przyszła spoza GitHuba",
        DomainErrors.AppUpdate.ResponseTooLargeCode => "odpowiedź GitHuba jest za duża",
        DomainErrors.AppUpdate.InvalidResponseCode => "GitHub odpowiedział w nieznanym formacie",
        DomainErrors.AppUpdate.NoDesktopReleaseCode => "na GitHubie nie ma jeszcze wydania aplikacji",
        DomainErrors.AppUpdate.ChecksumFileInvalidCode => "plik z sumą kontrolną jest nieprawidłowy",
        DomainErrors.AppUpdate.HashMismatchCode => "pobrany instalator nie zgadza się z sumą kontrolną",
        DomainErrors.AppUpdate.FileSystemErrorCode => "błąd zapisu lub odczytu pliku na dysku (szczegóły w logu programu)",
        DomainErrors.AppUpdate.InstallerStartCancelledCode => "nie zgodzono się na uruchomienie instalatora",
        DomainErrors.AppUpdate.InstallerStartFailedCode => "nie udało się uruchomić instalatora",
        _ => "nieoczekiwany błąd"
    };
}
