using System.Diagnostics;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.GameLaunching;

public sealed class GameLauncher : IGameLauncher
{
    private const string LauncherExecutable = "LotroLauncher.exe";

    public async Task<Result<int>> LaunchAndWaitForExitAsync(string datFilePath, CancellationToken cancellationToken = default)
    {
        Result<Process> startResult = StartLauncherProcess(datFilePath);
        if (startResult.IsFailure)
        {
            return Result.Failure<int>(startResult.Error);
        }

        using Process process = startResult.Value;
        await process.WaitForExitAsync(cancellationToken);

        return Result.Success(process.ExitCode);
    }

    private static Result<Process> StartLauncherProcess(string datFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datFilePath);

        string launcherPath = ResolveLauncherPath(datFilePath);

        if (!File.Exists(launcherPath))
        {
            return Result.Failure<Process>(DomainErrors.GameLaunch.LauncherNotFound(launcherPath));
        }

        try
        {
            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = launcherPath,
                WorkingDirectory = Path.GetDirectoryName(launcherPath) ?? string.Empty,
                // UseShellExecute = true allows the launcher to trigger UAC elevation
                // prompts when it needs admin rights for game updates.
                UseShellExecute = true
            });

            if (process is null)
            {
                return Result.Failure<Process>(DomainErrors.GameLaunch.LaunchFailed(
                    "Process.Start returned null — the launcher could not be started."));
            }

            return Result.Success(process);
        }
        catch (Exception ex)
        {
            return Result.Failure<Process>(DomainErrors.GameLaunch.LaunchFailed(ex.Message));
        }
    }

    private static string ResolveLauncherPath(string datFilePath)
    {
        if (Directory.Exists(datFilePath))
        {
            return Path.Combine(datFilePath, LauncherExecutable);
        }

        if (File.Exists(datFilePath))
        {
            string dirPath = Path.GetDirectoryName(datFilePath) ?? string.Empty;
            return Path.Combine(dirPath, LauncherExecutable);
        }

        return Path.Combine(datFilePath, LauncherExecutable);
    }
}
