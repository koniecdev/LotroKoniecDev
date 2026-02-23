using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Application.Features.Export;
using LotroKoniecDev.Application.Features.Patch;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure;
using LotroKoniecDev.Primitives.Enums;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using static LotroKoniecDev.Cli.ConsoleWriter;

namespace LotroKoniecDev.Cli;

internal static class Program
{
    private static readonly string DataDir = Path.GetFullPath("data");
    private const string TranslationsDir = "translations";
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

        if (command is "patch" && args.Length < 2)
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

        ISender sender = serviceProvider.GetRequiredService<ISender>();

        IOperationStatusReporter reporter = serviceProvider.GetRequiredService<IOperationStatusReporter>();

        switch (command)
        {
            case "export":
                {
                    IDatPathResolver datPathResolver = serviceProvider.GetRequiredService<IDatPathResolver>();
                    string? datPath = datPathResolver.Resolve(args.Length > 1 ? args[1] : null);
                    if (datPath is null)
                    {
                        return ExitCodes.FileNotFound;
                    }

                    string outputPath = args.Length > 2
                        ? args[2]
                        : Path.Combine(DataDir, "exported.txt");

                    ExportTextsQuery query = new(
                        DatFilePath: datPath,
                        OutputPath: outputPath);

                    Result<ExportSummaryResponse> result = await sender.Send(query);
                    if (result.IsFailure)
                    {
                        reporter.Report(result.Error.ToString());
                        return MapErrorToExitCode(result.Error);
                    }

                    reporter.Report(result.Value.ToString());
                    return ExitCodes.Success;
                }
            
            case "patch":
                {
                    string? translationArgument = args.Length > 1 ? args[1] : null;
                    if (translationArgument is null)
                    {
                        return ExitCodes.FileNotFound;
                    }
                    string translationsPath = ResolveTranslationsPath(translationArgument);
                    if (!File.Exists(translationsPath))
                    {
                        WriteError($"Translation file not found: {translationsPath}");
                        return ExitCodes.FileNotFound;
                    }
                    
                    IDatPathResolver datPathResolver = serviceProvider.GetRequiredService<IDatPathResolver>();
                    string? datPath = datPathResolver.Resolve(args.Length > 2 ? args[2] : null);
                    if (datPath is null)
                    {
                        return ExitCodes.FileNotFound;
                    }
                    if (!File.Exists(datPath))
                    {
                        WriteError($"DAT file not found: {datPath}");
                        return ExitCodes.FileNotFound;
                    }
                    
                    ApplyPatchCommand applyPatchCommand = new(
                        TranslationsPath: translationsPath,
                        DatFilePath: datPath,
                        VersionFilePath: VersionFilePath);
                    
                    Result<PatchSummaryResponse> result = await sender.Send(applyPatchCommand);
                    if (result.IsFailure)
                    {
                        reporter.Report(result.Error.ToString());
                        return MapErrorToExitCode(result.Error);
                    }
                    
                    reporter.Report(result.Value.ToString());
                    return ExitCodes.Success;
                }
            default:
                return HandleUnknownCommand();
        }
    }

    private static string ResolveTranslationsPath(string input)
    {
        return input.Contains(Path.DirectorySeparatorChar) ||
               input.Contains(Path.AltDirectorySeparatorChar) ||
               input.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? input
            : Path.Combine(TranslationsDir, input + ".txt");
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

    private static int MapErrorToExitCode(Error error) =>
        error.Type == ErrorType.NotFound
            ? ExitCodes.FileNotFound
            : ExitCodes.OperationFailed;

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
