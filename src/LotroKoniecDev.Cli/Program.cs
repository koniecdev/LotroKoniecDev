using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Cli.Commands;
using LotroKoniecDev.Cli.ValueObjects;
using LotroKoniecDev.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static LotroKoniecDev.Cli.ConsoleWriter;

namespace LotroKoniecDev.Cli;

internal static class Program
{
    private static readonly string DataDir = Path.GetFullPath("data");
    private static readonly string VersionFilePath = Path.Combine(DataDir, "last_known_game_version.txt");

    private static async Task<int> Main(string[] args)
    {
        PrintBanner();

        if (args.Length == 0)
        {
            PrintUsage();
            return ExitCodes.InvalidArguments;
        }
        
        string command = args[0].ToLowerInvariant();

        if (command == "patch" && args.Length < 2)
        {
            WriteError("Missing required argument: translation name");
            PrintUsage();
            return ExitCodes.InvalidArguments;
        }

        ServiceCollection services = new();
        services.AddApplicationServices();
        services.AddInfrastructureServices();
        services.AddCliServices();

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        return command switch
        {
            "export" => ExportCommand.Run(args, serviceProvider, DataDir),

            "patch" => await PatchCommand.RunAsync(
                translationPathArg: new TranslationPath(args[1]),
                datPathArg: args.Length > 2 ? new DatPath(args[2]) : null,
                serviceProvider: serviceProvider,
                versionFilePath: VersionFilePath),

            _ => HandleUnknownCommand()
        };
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine();
        Console.WriteLine("  EXPORT texts from game:");
        Console.WriteLine("    LotroKoniecDev export [dat_file] [output.txt]");
        Console.WriteLine();
        Console.WriteLine("  PATCH (inject translations):");
        Console.WriteLine("    LotroKoniecDev patch <name> [dat_file]");
        Console.WriteLine("    Name resolves to translations/<name>.txt");
        Console.WriteLine();
        Console.WriteLine("If no DAT file is specified, LOTRO installation is detected automatically.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  LotroKoniecDev patch example_polish");
        Console.WriteLine(@"  LotroKoniecDev patch example_polish C:\path\to\client_local_English.dat");
        Console.WriteLine("  LotroKoniecDev export");
    }
    
    private static int HandleUnknownCommand()
    {
        PrintUsage();
        return ExitCodes.InvalidArguments;
    }

    private static void PrintBanner()
    {
        Console.WriteLine("=== LOTRO Translations Patcher ===");
        Console.WriteLine();
    }
}
