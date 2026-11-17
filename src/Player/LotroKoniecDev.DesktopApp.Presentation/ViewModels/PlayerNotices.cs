namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>
/// The two notes the main window always shows, as the CLI's <c>launch</c> prints them. They only
/// inform: no question (spec 0011 LEGAL-12, ADR-0069 §3).
/// </summary>
internal static class PlayerNotices
{
    /// <summary>The CLI's <c>RiskNotice</c>, word for word; the CLI's copy is internal to it.</summary>
    public const string Risk =
        "Spolszczenie modyfikuje pliki gry — formalnie regulamin LOTRO nie przewiduje takich modyfikacji, " +
        "choć przez ponad dekadę działania analogicznych projektów (rosyjskiego i hiszpańskiego) " +
        "nie odnotowano za nie banów. Korzystasz na własną odpowiedzialność.";

    /// <summary>
    /// The app's own launcher notice. The app runs as administrator (owner decision 4 on #43), so the
    /// launcher it starts never has to ask for those rights, and the CLI's clause about it is left out.
    /// </summary>
    public const string Launcher =
        "Grę uruchamia LotroLauncher.exe z folderu gry. SSG nie podpisuje już launchera, więc nie " +
        "sprawdzamy, czy pliki w folderze gry są oryginalne. Launcher dostaje uprawnienia tej aplikacji " +
        "(administratora). Jeśli wirus podmienił któryś z tych plików, może je w ten sposób dostać. " +
        "Uruchamiasz grę świadomie.";
}
