namespace LotroKoniecDev.TranslationSystem.API.Features.Bootstrap;

/// <summary>
/// One-time bootstrap knobs (spec 0001, first run / #28). Opt-in: <see cref="Enabled"/> is
/// <c>false</c> by default so an ordinary start never seeds. When enabled the bootstrap optionally
/// imports the English baseline (<see cref="ExportedTextPath"/> bound to <see cref="GameVersion"/>)
/// and then merges the existing production <c>polish.txt</c> onto those rows as Approved. Every step
/// is idempotent, so leaving it enabled across restarts is safe.
/// </summary>
internal sealed class BootstrapSettings
{
    public const string ConfigurationSection = "Bootstrap";

    /// <summary>Master switch — when <c>false</c> the bootstrap is a no-op.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Forum version string for the initial <c>GameVersion</c> the baseline import binds to
    /// (e.g. <c>48.0</c>). Unset together with <see cref="ExportedTextPath"/> means "skip the
    /// baseline, assume it was already imported via the M2-08 endpoint".
    /// </summary>
    public string? GameVersion { get; init; }

    /// <summary>Path to the English <c>exported.txt</c> for the baseline import. Optional.</summary>
    public string? ExportedTextPath { get; init; }

    /// <summary>Path to the production Polish translations merged onto the baseline as Approved.</summary>
    public string PolishTextPath { get; init; } = "translations/polish.txt";
}
