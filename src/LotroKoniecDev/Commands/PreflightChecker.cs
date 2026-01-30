using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Monads;
using Microsoft.Extensions.DependencyInjection;
using static LotroKoniecDev.ConsoleWriter;

namespace LotroKoniecDev.Commands;

internal static class PreflightChecker
{
    public static async Task<bool> RunAllAsync(
        string datPath,
        IServiceProvider serviceProvider,
        string versionFilePath)
    {
        return await CheckForGameUpdateAsync(serviceProvider, versionFilePath)
               && CheckPrerequisites(datPath, serviceProvider);
    }

    private static async Task<bool> CheckForGameUpdateAsync(
        IServiceProvider serviceProvider,
        string versionFilePath)
    {
        IGameUpdateChecker checker = serviceProvider.GetRequiredService<IGameUpdateChecker>();

        Result<GameUpdateCheckResult> result = await checker.CheckForUpdateAsync(versionFilePath);

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

    private static bool CheckPrerequisites(string datPath, IServiceProvider serviceProvider)
    {
        IGameProcessDetector detector = serviceProvider.GetRequiredService<IGameProcessDetector>();
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

        IWriteAccessChecker accessChecker = serviceProvider.GetRequiredService<IWriteAccessChecker>();
        if (accessChecker.CanWriteTo(directory))
        {
            return true;
        }

        WriteError($"No write access to: {directory}");
        WriteError("Run this application as Administrator.");
        return false;
    }
}
