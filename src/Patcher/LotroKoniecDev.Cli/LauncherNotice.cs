namespace LotroKoniecDev.Cli;

/// <summary>
/// The note that <c>launch</c> prints because it starts the game launcher with no check of the file
/// (ADR-0069). Like <see cref="RiskNotice"/>, it only informs: no question, no change to exit codes.
/// </summary>
internal static class LauncherNotice
{
    public const string Text =
        "Na koniec uruchamiamy LotroLauncher.exe z folderu gry, a on uruchamia grę. SSG nie podpisuje " +
        "już launchera, więc nie sprawdzamy, czy pliki w folderze gry są oryginalne. Launcher dostaje " +
        "uprawnienia tego narzędzia (przez lotro.bat: administratora), a gdy nie może zapisywać " +
        "w folderze gry, sam prosi o prawa administratora. Jeśli wirus podmienił któryś z tych plików, może je " +
        "w ten sposób dostać. Uruchamiasz grę świadomie.";
}
