using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev;

internal static class Program
{
    private static readonly string DataDir = Path.GetFullPath("data");
    private static readonly string VersionFilePath = Path.Combine(DataDir, "last_known_game_version.txt");
    private const string TranslationsDir = "translations";

    private static async Task<int> Main(string[] args)
    {
        PrintBanner();

        if (args.Length == 0)
        {
            PrintUsage();
            return ExitCodes.InvalidArguments;
        }

        string command = args[0].ToLowerInvariant();

        var services = new ServiceCollection();
        services.AddApplicationServices();
        services.AddInfrastructureServices();

        using var serviceProvider = services.BuildServiceProvider();

        return command switch
        {
            "export" => RunExport(args, serviceProvider),
            "patch" => await RunPatchAsync(args, serviceProvider),
            _ => HandleUnknownCommand()
        };
    }

    private static int RunExport(string[] args, IServiceProvider serviceProvider)
    {
        string? datPath = ResolveDatPath(
            args.Length > 1 ? args[1] : null,
            serviceProvider);

        if (datPath is null)
        {
            return ExitCodes.FileNotFound;
        }

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

        return ExitCodes.Success;
    }

    private static async Task<int> RunPatchAsync(string[] args, IServiceProvider serviceProvider)
    {
        if (args.Length < 2)
        {
            PrintUsage();
            return ExitCodes.InvalidArguments;
        }

        string translationsPath = ResolveTranslationsPath(args[1]);

        string? datPath = ResolveDatPath(
            args.Length > 2 ? args[2] : null,
            serviceProvider);

        if (datPath is null)
        {
            return ExitCodes.FileNotFound;
        }

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

        if (!await CheckForGameUpdateAsync(serviceProvider)
            || !RunPreflightChecks(datPath, serviceProvider))
        {
            return ExitCodes.OperationFailed;
        }

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

    private static async Task<bool> CheckForGameUpdateAsync(IServiceProvider serviceProvider)
    {
        var checker = serviceProvider.GetRequiredService<IGameUpdateChecker>();

        Result<GameUpdateCheckResult> result = await checker.CheckForUpdateAsync(VersionFilePath);

        if (result.IsFailure)
        {
            WriteWarning($"Could not check for game updates: {result.Error.Message}");
            return true;
        }

        GameUpdateCheckResult check = result.Value;

        if (!check.UpdateDetected)
        {
            return true;
        }

        Console.WriteLine();

        if (check.PreviousVersion is null)
        {
            WriteInfo($"Game version detected: {check.CurrentVersion}");
            WriteInfo("Version saved. Future runs will detect game updates.");
            return true;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("WARNING: LOTRO game update detected!");
        Console.ResetColor();
        Console.WriteLine($"  Previous version: {check.PreviousVersion}");
        Console.WriteLine($"  Current version:  {check.CurrentVersion}");
        Console.WriteLine();
        Console.WriteLine("  The game files have been updated. You should:");
        Console.WriteLine("  1. Run the LOTRO launcher to update game files");
        Console.WriteLine("  2. Then re-run this patcher to re-apply translations");
        Console.WriteLine();
        Console.Write("Continue with patching anyway? (y/N): ");

        string? answer = Console.ReadLine();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveDatPath(string? explicitPath, IServiceProvider serviceProvider)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var locator = serviceProvider.GetRequiredService<IDatFileLocator>();

        Result<IReadOnlyList<DatFileLocation>> result = locator.LocateAll(WriteInfo);

        if (result.IsFailure)
        {
            WriteError(result.Error.Message);
            return null;
        }

        IReadOnlyList<DatFileLocation> locations = result.Value;

        if (locations.Count != 1)
        {
            return PromptUserChoice(locations);
        }

        DatFileLocation location = locations[0];
        WriteInfo($"Found LOTRO: {location.DisplayName}");
        WriteInfo($"  {location.Path}");
        return location.Path;
    }

    private static string? PromptUserChoice(IReadOnlyList<DatFileLocation> locations)
    {
        Console.WriteLine();
        WriteInfo("Multiple LOTRO installations found:");
        Console.WriteLine();

        for (int i = 0; i < locations.Count; i++)
        {
            Console.WriteLine($"  [{i + 1}] {locations[i].DisplayName}");
            Console.WriteLine($"      {locations[i].Path}");
        }

        Console.WriteLine();
        Console.Write($"Choose installation (1-{locations.Count}): ");

        string? input = Console.ReadLine();

        if (int.TryParse(input, out int choice) &&
            choice >= 1 && choice <= locations.Count)
        {
            return locations[choice - 1].Path;
        }

        WriteError("Invalid choice.");
        return null;
    }

    private static bool RunPreflightChecks(string datPath, IServiceProvider serviceProvider)
    {
        var detector = serviceProvider.GetRequiredService<IGameProcessDetector>();
        if (detector.IsLotroRunning())
        {
            WriteWarning("LOTRO client is running. Close the game before patching.");
            Console.Write("Continue anyway? (y/N): ");
            string? answer = Console.ReadLine();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        string? directory = Path.GetDirectoryName(datPath);
        if (directory is null)
        {
            return true;
        }

        var accessChecker = serviceProvider.GetRequiredService<IWriteAccessChecker>();
        if (accessChecker.CanWriteTo(directory))
        {
            return true;
        }

        WriteError($"No write access to: {directory}");
        WriteError("Run this application as Administrator.");
        return false;

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
                Domain.Core.Errors.DomainErrors.Backup.CannotCreate(backupPath, ex.Message));
        }
    }

    private static void RestoreBackup(string datPath)
    {
        string backupPath = datPath + ".backup";

        Console.WriteLine();
        WriteWarning("Restoring from backup...");

        try
        {
            if (!File.Exists(backupPath))
            {
                return;
            }

            File.Copy(backupPath, datPath, overwrite: true);
            WriteInfo("Restored from backup.");
        }
        catch (Exception ex)
        {
            WriteError($"Failed to restore backup: {ex.Message}");
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

    private static int HandleUnknownCommand()
    {
        PrintUsage();
        return ExitCodes.InvalidArguments;
    }

    private static void PrintBanner()
    {
        Console.WriteLine("=== LOTRO Polish Patcher ===");
        Console.WriteLine();
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
        Console.WriteLine("  LotroKoniecDev patch example_polish C:\\path\\to\\client_local_English.dat");
        Console.WriteLine("  LotroKoniecDev export");
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
        => Console.Write($"\r{message}".PadRight(60));
}

internal static class ExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 1;
    public const int FileNotFound = 2;
    public const int OperationFailed = 3;
}
