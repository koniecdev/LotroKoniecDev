using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.DesktopApp.Presentation.Settings;

/// <summary>
/// The shape of <c>config.json</c>. Every field may be missing, because the player can edit the file.
/// The default server address is left out, so a later change of the default reaches every player.
/// </summary>
internal sealed record SettingsFileDocument(
    string? GameFolder,
    string? TranslationFile,
    bool? AutoPatch,
    string? ServerAddress)
{
    public static SettingsFileDocument From(DesktopSettings settings) =>
        new(
            settings.GameFolder,
            settings.TranslationFileOverride,
            settings.AutoPatchOnLaunch,
            IsDefaultAddress(settings.ServerAddress) ? null : settings.ServerAddress);

    /// <summary>The default typed without its slash or in capitals is still the default.</summary>
    private static bool IsDefaultAddress(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) && uri == new Uri(TmsConstants.DefaultBaseUrl);
}
