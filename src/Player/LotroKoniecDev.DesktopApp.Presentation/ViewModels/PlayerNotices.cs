using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>
/// The two notes the main window always shows, as the CLI's <c>launch</c> prints them. They only
/// inform: no question (spec 0011 LEGAL-12, ADR-0069 §3).
/// </summary>
internal static class PlayerNotices
{
    public const string Risk = PlayerNoticeConstants.Risk;

    /// <summary>
    /// The app's own launcher notice. The app runs as administrator (owner decision 4 on #43, carried
    /// out in #1464). The launcher it starts then never has to ask for those
    /// rights, so the CLI's clause about it is left out.
    /// </summary>
    public const string Launcher =
        "Grę uruchamia LotroLauncher.exe z folderu gry. SSG nie podpisuje już launchera, więc nie " +
        "sprawdzamy, czy pliki w folderze gry są oryginalne. Launcher dostaje uprawnienia tej aplikacji " +
        "(administratora). Jeśli wirus podmienił któryś z tych plików, może je w ten sposób dostać. " +
        "Uruchamiasz grę świadomie.";
}
