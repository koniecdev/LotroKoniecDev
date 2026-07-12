namespace LotroKoniecDev.Cli;

/// <summary>
/// Player-facing risk note (spec 0011 / LEGAL-12) printed by <c>patch</c> and <c>launch</c>.
/// Informational only — never a prompt, never affects behavior or exit codes.
/// </summary>
internal static class RiskNotice
{
    public const string Text =
        "Spolszczenie modyfikuje pliki gry — formalnie regulamin LOTRO nie przewiduje takich modyfikacji, " +
        "choć przez ponad dekadę działania analogicznych projektów (rosyjskiego i hiszpańskiego) " +
        "nie odnotowano za nie banów. Korzystasz na własną odpowiedzialność.";
}
