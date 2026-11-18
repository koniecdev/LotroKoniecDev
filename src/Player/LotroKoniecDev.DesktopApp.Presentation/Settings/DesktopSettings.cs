using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.DesktopApp.Presentation.Settings;

/// <param name="GameFolder">The folder that holds the game file, or <c>null</c> before one is found or picked.</param>
/// <param name="TranslationFileOverride">The player's own <c>polish.txt</c>, or <c>null</c> for the downloaded one (#42).</param>
public sealed record DesktopSettings(
    string? GameFolder,
    string? TranslationFileOverride,
    bool AutoPatchOnLaunch,
    string ServerAddress)
{
    /// <summary>Auto-patch is on and the server is the production one (owner decisions on #43).</summary>
    public static DesktopSettings Default { get; } = new(null, null, true, TmsConstants.DefaultBaseUrl);
}
