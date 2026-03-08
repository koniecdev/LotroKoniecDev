namespace LotroKoniecDev.Application.Features.GameLaunching;

public sealed record GameLaunchingResponse(
    string? ForumVersion,
    bool UpdateWasDetected,
    int GameExitCode)
{
    public override string ToString()
    {
        string updateInfo = UpdateWasDetected
            ? $"Game updated to version {ForumVersion}. "
            : string.Empty;

        return $"{updateInfo}Session ended (exit code {GameExitCode}).";
    }
}
