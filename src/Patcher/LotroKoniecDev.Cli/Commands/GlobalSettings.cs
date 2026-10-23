using System.ComponentModel;
using Spectre.Console.Cli;

namespace LotroKoniecDev.Cli.Commands;

internal class GlobalSettings : CommandSettings
{
    public const string TranslationsDir = "translations";

    /// <summary>
    /// The TMS <b>root</b> URL, the only address the CLI is configured with: the download endpoint is
    /// found by link relation in the service document served there (ADR-0041, #611).
    /// It is empty, so the launch skips the sync and uses the local translation file unless a run
    /// passes <c>--tms-url</c>. It must be <c>https</c>; plain <c>http</c> is only allowed for
    /// localhost (#391).
    /// </summary>
    public const string DefaultTmsBaseUrl = "";

    public static string DataDir => Path.GetFullPath("data");
    public static string VersionFilePath => Path.Combine(DataDir, "last_known_game_version.txt");

    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    [DefaultValue(false)]
    public bool Verbose { get; init; }
}
