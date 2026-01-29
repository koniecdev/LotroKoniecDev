using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev;

/// <summary>
/// LOTRO Polish Patcher - CLI application for translating LOTRO game files.
/// </summary>
internal static class Program
{
    private static readonly string DataDir = Path.GetFullPath("data");
    private const string TranslationsDir = "translations";

    private static int Main(string[] args)
    {
        PrintBanner();

        if (args.Length == 0)
        {
            PrintUsage();
            return ExitCodes.InvalidArguments;
        }

        string command = args[0].ToLowerInvariant();

        // Setup DI container
        var services = new ServiceCollection();
        services.AddApplicationServices();
        services.AddInfrastructureServices();

        using var serviceProvider = services.BuildServiceProvider();

        return command switch
        {
            "export" => RunExport(args, serviceProvider),
            "patch" => RunPatch(args, serviceProvider),
            _ => HandleUnknownCommand()
        };
    }

    private static int RunExport(string[] args, IServiceProvider serviceProvider)
    {
        string datPath = args.Length > 1
            ? args[1]
            : Path.Combine(DataDir, "client_local_English.dat");

        string outputPath = args.Length > 2
            ? args[2]
            : Path.Combine(DataDir, "exported.txt");

        if (!File.Exists(datPath))
        {
            WriteError($"DAT file not found: {datPath}");
            return ExitCodes.FileNotFound;
        }

        WriteInfo($"Opening: {datPath}");

        using var scope = serviceProvider.CreateScope();
        var exporter = scope.ServiceProvider.GetRequiredService<IExporter>();

        Result<ExportSummary> result = exporter.ExportAllTexts(
            datPath,
            outputPath,
            (processed, total) => WriteProgress($"Processing {processed}/{total} files..."));

        if (result.IsFailure)
        {
            WriteError(result.Error.Message);
            return ExitCodes.OperationFailed;
        }

        ExportSummary summary = result.Value;
        Console.WriteLine();
        WriteSuccess("=== EXPORT COMPLETE ===");
        WriteInfo($"Exported {summary.TotalFragments:N0} texts from {summary.TotalTextFiles:N0} files");
        WriteInfo($"Output: {summary.OutputPath}");
        Console.WriteLine();
        WriteInfo("Next step: Translate the texts and run:");
        WriteInfo($"  dotnet run -- patch {summary.OutputPath} <path_to_dat>");

        return ExitCodes.Success;
    }

    private static int RunPatch(string[] args, IServiceProvider serviceProvider)
    {
        if (args.Length < 2)
        {
            PrintUsage();
            return ExitCodes.InvalidArguments;
        }

        string translationsPath = ResolveTranslationsPath(args[1]);
        string datPath = args.Length > 2
            ? args[2]
            : Path.Combine(DataDir, "client_local_English.dat");

        if (!File.Exists(translationsPath))
        {
            WriteError($"Translation file not found: {translationsPath}");
            return ExitCodes.FileNotFound;
        }

        if (!File.Exists(datPath))
        {
            WriteError($"DAT file not found: {datPath}");
            return ExitCodes.FileNotFound;
        }

        // Create backup
        Result backupResult = CreateBackup(datPath);
        if (backupResult.IsFailure)
        {
            WriteError(backupResult.Error.Message);
            return ExitCodes.OperationFailed;
        }

        WriteInfo($"Loading translations from: {translationsPath}");

        using var scope = serviceProvider.CreateScope();
        var patcher = scope.ServiceProvider.GetRequiredService<IPatcher>();

        Result<PatchSummary> result = patcher.ApplyTranslations(
            translationsPath,
            datPath,
            (applied, total) => WriteProgress($"Patching... {applied}/{total}"));

        if (result.IsFailure)
        {
            WriteError(result.Error.Message);
            RestoreBackup(datPath);
            return ExitCodes.OperationFailed;
        }

        PatchSummary summary = result.Value;

        // Print warnings
        foreach (string warning in summary.Warnings.Take(10))
        {
            WriteWarning(warning);
        }

        if (summary.Warnings.Count > 10)
        {
            WriteWarning($"... and {summary.Warnings.Count - 10} more warnings");
        }

        Console.WriteLine();
        WriteSuccess("=== PATCH COMPLETE ===");
        WriteInfo($"Applied {summary.AppliedTranslations:N0} of {summary.TotalTranslations:N0} translations");

        if (summary.SkippedTranslations > 0)
        {
            WriteWarning($"Skipped: {summary.SkippedTranslations:N0}");
        }

        return ExitCodes.Success;
    }

    private static Result CreateBackup(string datPath)
    {
        string backupPath = datPath + ".backup";

        try
        {
            if (File.Exists(backupPath))
            {
                WriteInfo($"Backup already exists: {backupPath}");
            }
            else
            {
                WriteInfo($"Creating backup: {backupPath}");
                File.Copy(datPath, backupPath);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(
                DomainErrors.Backup.CannotCreate(backupPath, ex.Message));
        }
    }

    private static void RestoreBackup(string datPath)
    {
        string backupPath = datPath + ".backup";

        Console.WriteLine();
        WriteWarning("Restoring from backup...");

        try
        {
            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, datPath, overwrite: true);
                WriteInfo("Restored from backup.");
            }
        }
        catch (Exception ex)
        {
            WriteError($"Failed to restore backup: {ex.Message}");
        }
    }

    private static string ResolveTranslationsPath(string input)
    {
        if (input.Contains(Path.DirectorySeparatorChar) ||
            input.Contains(Path.AltDirectorySeparatorChar) ||
            input.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return input;
        }

        return Path.Combine(TranslationsDir, input + ".txt");
    }

    private static int HandleUnknownCommand()
    {
        PrintUsage();
        return ExitCodes.InvalidArguments;
    }

    private static void PrintBanner()
    {
        Console.WriteLine("=== LOTRO Polish Patcher ===");
        Console.WriteLine($"Data directory: {DataDir}");
        Console.WriteLine();
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine();
        Console.WriteLine("  EXPORT texts from game (defaults to data/ folder):");
        Console.WriteLine("    LotroKoniecDev export [dat_file] [output.txt]");
        Console.WriteLine("    LotroKoniecDev export   <- uses data/client_local_English.dat");
        Console.WriteLine();
        Console.WriteLine("  PATCH (inject translations):");
        Console.WriteLine("    LotroKoniecDev patch <name> [dat_file]");
        Console.WriteLine("    Name resolves to translations/<name>.txt");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  LotroKoniecDev export");
        Console.WriteLine("  LotroKoniecDev patch example_polish");
    }

    private static void WriteInfo(string message) =>
        Console.WriteLine(message);

    private static void WriteSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static void WriteWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"WARN: {message}");
        Console.ResetColor();
    }

    private static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: {message}");
        Console.ResetColor();
    }

    private static void WriteProgress(string message)
    {
        Console.Write($"\r{message}".PadRight(60));
    }
}

/// <summary>
/// Standard exit codes for the application.
/// </summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 1;
    public const int FileNotFound = 2;
    public const int OperationFailed = 3;
}
