using System.ComponentModel;
using Spectre.Console.Cli;

namespace LotroKoniecDev.Cli.Commands;

internal class GlobalSettings : CommandSettings
{
    public const string TranslationsDir = "translations";

    /// <summary>
    /// TMS distribution base URL the launch sync downloads the translation file from. Empty until the
    /// TMS is deployed — while blank the launch skips the sync and uses the local translation file. Set
    /// here (or override per-run with <c>--tms-url</c>) once the server has a stable address. Must be
    /// <c>https</c> — plain <c>http</c> passes validation only for localhost (AUDIT-SEC-01 / #391).
    /// </summary>
    public const string DefaultTmsBaseUrl = "";

    public static string DataDir => Path.GetFullPath("data");
    public static string VersionFilePath => Path.Combine(DataDir, "last_known_game_version.txt");

    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    [DefaultValue(false)]
    public bool Verbose { get; init; }
}
