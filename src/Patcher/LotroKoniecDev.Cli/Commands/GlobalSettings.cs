using System.ComponentModel;
using Spectre.Console.Cli;

namespace LotroKoniecDev.Cli.Commands;

internal class GlobalSettings : CommandSettings
{
    public const string TranslationsDir = "translations";

    /// <summary>
    /// The TMS <b>root</b> URL. It is the only address the CLI is configured with: the download
    /// endpoint comes from the service document served there, found by link relation, so no route is
    /// baked into this binary (ADR-0041, #611).
    /// It is empty until the TMS is deployed. While it is empty the launch skips the sync and uses the
    /// local translation file. Set it here, or per run with <c>--tms-url</c>, once the server has a
    /// stable address. It must be <c>https</c>; plain <c>http</c> is only allowed for localhost
    /// (AUDIT-SEC-01, #391).
    /// </summary>
    public const string DefaultTmsBaseUrl = "";

    public static string DataDir => Path.GetFullPath("data");
    public static string VersionFilePath => Path.Combine(DataDir, "last_known_game_version.txt");

    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    [DefaultValue(false)]
    public bool Verbose { get; init; }
}
