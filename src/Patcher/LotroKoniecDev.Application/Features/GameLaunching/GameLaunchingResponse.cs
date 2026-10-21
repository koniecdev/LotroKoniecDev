namespace LotroKoniecDev.Application.Features.GameLaunching;

/// <param name="WipedSubFileCount">
/// Text subfiles a game update replaced since our last patch, found while the translation file was
/// unchanged (spec 0012 Tier 0). Zero when nothing was replaced or nothing could be compared.
/// </param>
/// <param name="RepairFailure">Why the repair of those subfiles failed, when it did. The game starts anyway.</param>
public sealed record GameLaunchingResponse(
    string? ForumVersion,
    bool UpdateWasDetected,
    int GameExitCode,
    bool TranslationsApplied = false,
    int AppliedCount = 0,
    int SkippedCount = 0,
    int WipedSubFileCount = 0,
    string? RepairFailure = null)
{
    public override string ToString()
    {
        string wipeInfo = WipedSubFileCount > 0
            ? $"A game update replaced {WipedSubFileCount} text subfile(s) since the last patch. "
            : string.Empty;

        if (TranslationsApplied)
        {
            return WipedSubFileCount > 0
                ? $"{wipeInfo}Translations restored ({AppliedCount} applied, {SkippedCount} skipped). Launcher started."
                : $"Translations applied ({AppliedCount} applied, {SkippedCount} skipped). Launcher started.";
        }

        if (RepairFailure is not null)
        {
            return $"{wipeInfo}Restoring the translations failed ({RepairFailure}). The next launch tries again. Launcher started.";
        }

        string updateInfo = UpdateWasDetected
            ? $"Game updated to version {ForumVersion}. "
            : string.Empty;

        return $"{updateInfo}Session ended (exit code {GameExitCode}).";
    }
}
