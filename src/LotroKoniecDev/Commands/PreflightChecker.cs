using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Monads;
using Microsoft.Extensions.DependencyInjection;
using static LotroKoniecDev.ConsoleWriter;

namespace LotroKoniecDev.Commands;

internal sealed class PreflightChecker : IPreflightChecker
{
    private readonly IGameUpdateChecker _gameUpdateChecker;
    private readonly IGameProcessDetector _gameProcessDetector;
    private readonly IWriteAccessChecker _writeAccessChecker;

    public PreflightChecker(
        IGameUpdateChecker gameUpdateChecker,
        IGameProcessDetector gameProcessDetector,
        IWriteAccessChecker writeAccessChecker)
    {
        _gameUpdateChecker = gameUpdateChecker;
        _gameProcessDetector = gameProcessDetector;
        _writeAccessChecker = writeAccessChecker;
    }
    
    public async Task<bool> RunAllAsync(
        string datPath,
        string versionFilePath)
    {
        return await CheckForGameUpdateAsync(versionFilePath)
               && CheckPrerequisites(datPath);
    }

    private async Task<bool> CheckForGameUpdateAsync(string versionFilePath)
    {
        Result<GameUpdateCheckResult> result = await _gameUpdateChecker.CheckForUpdateAsync(versionFilePath);

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

    private bool CheckPrerequisites(string datPath)
    {
        if (_gameProcessDetector.IsLotroRunning())
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

        if (_writeAccessChecker.CanWriteTo(directory))
        {
            return true;
        }

        WriteError($"No write access to: {directory}");
        WriteError("Run this application as Administrator.");
        return false;
    }
}
