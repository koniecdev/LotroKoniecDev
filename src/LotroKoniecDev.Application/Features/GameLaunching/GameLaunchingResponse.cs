namespace LotroKoniecDev.Application.Features.GameLaunching;

public sealed record GameLaunchingResponse(
    string? ForumVersion,
    bool UpdateWasDetected,
    int GameExitCode,
    bool TranslationsApplied = false,
    int AppliedCount = 0,
    int SkippedCount = 0)
{
    public override string ToString()
    {
        if (TranslationsApplied)
        {
            return $"Translations applied ({AppliedCount} applied, {SkippedCount} skipped). Launcher started.";
        }

        string updateInfo = UpdateWasDetected
            ? $"Game updated to version {ForumVersion}. "
            : string.Empty;

        return $"{updateInfo}Session ended (exit code {GameExitCode}).";
    }
}
