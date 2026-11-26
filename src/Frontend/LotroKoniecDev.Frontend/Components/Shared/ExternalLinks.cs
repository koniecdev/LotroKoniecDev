namespace LotroKoniecDev.Frontend.Components.Shared;

internal static class ExternalLinks
{
    internal const string Repository = "https://github.com/koniecdev/LotroKoniecDev";

    /// <summary>The README heading "The Patcher (game-side CLI)". No test checks that the heading keeps this name.</summary>
    internal const string PatcherReadme = Repository + "#the-patcher-game-side-cli";

    /// <summary>
    /// A permanent invite: it never expires and has no use limit. A new invite also goes into both links in README.md
    /// (the text link and the banner), which no test checks.
    /// </summary>
    internal const string Discord = "https://discord.gg/sncXwa64Ex";
}
