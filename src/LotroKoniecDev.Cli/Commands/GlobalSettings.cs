using System.ComponentModel;
using Spectre.Console.Cli;

namespace LotroKoniecDev.Cli.Commands;

internal class GlobalSettings : CommandSettings
{
    public const string TranslationsDir = "translations";
    public static string DataDir => Path.GetFullPath("data");
    public static string VersionFilePath => Path.Combine(DataDir, "last_known_game_version.txt");
    
    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    [DefaultValue(false)]
    public bool Verbose { get; init; }
}
