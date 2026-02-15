using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Commands;
using LotroKoniecDev.Infrastructure;
using LotroKoniecDev.ValueObjects;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev;

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

        ServiceCollection services = new();
        services.AddApplicationServices();
        services.AddInfrastructureServices();

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();
        
        ISender sender = serviceProvider.GetRequiredService<ISender>();
        
        return command switch
        {
            //export can be parameterless, but it can have the dat file name, and the output file name specified.
            //most likely the problem: you cant provide output file name without dat file name
            "export" => ExportCommand.Run(args, serviceProvider, DataDir),
            
            //patch command requires the name of the translation file to apply, and optionally the dat file name
            //if no dat file is specified, it will try to find it automatically
            //the name of the translation is resolved to translations/<name>.txt
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
