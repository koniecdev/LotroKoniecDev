using System.Diagnostics;
using LotroKoniecDev.Application.Features.GameLaunching;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.GameLaunching;

public sealed class GameLauncher : IGameLauncher
{
    private const string LauncherExecutable = "TurbineLauncher.exe";

    public Result<int> Launch(string lotroPath, bool waitForExit = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lotroPath);

        string launcherPath = ResolveLauncherPath(lotroPath);

        if (!File.Exists(launcherPath))
        {
            return Result.Failure<int>(DomainErrors.GameLaunch.LauncherNotFound(launcherPath));
        }

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = launcherPath,
                WorkingDirectory = Path.GetDirectoryName(launcherPath),
                UseShellExecute = false
            });

            if (process is null)
            {
                return Result.Failure<int>(DomainErrors.GameLaunch.LaunchFailed(
                    "Process.Start returned null — the launcher could not be started."));
            }

            if (!waitForExit)
            {
                return Result.Success(0);
            }

            process.WaitForExit();
            return Result.Success(process.ExitCode);
        }
        catch (Exception ex)
        {
            return Result.Failure<int>(DomainErrors.GameLaunch.LaunchFailed(ex.Message));
        }
    }

    private static string ResolveLauncherPath(string lotroPath)
    {
        if (File.Exists(lotroPath) 
            && Path.GetFileName(lotroPath).Equals("client_local_English.dat", StringComparison.OrdinalIgnoreCase))
        {
            string? datDirectory = Path.GetDirectoryName(lotroPath);
            return Path.Combine(datDirectory ?? string.Empty, LauncherExecutable);
        }

        return Path.Combine(lotroPath, LauncherExecutable);
    }
}
