using System.Diagnostics;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.GameLaunching;

/// <summary>
/// Starts LotroLauncher.exe from the folder of the DAT, with no check of the file. SSG no longer
/// signs it, and a signature never covered the libraries it loads from that folder (ADR-0069). The
/// guard is the player's confirmation of a folder that is not a known install.
/// </summary>
public sealed class GameLauncher : IGameLauncher
{
    private const string LauncherExecutable = "LotroLauncher.exe";

    public Result Launch(string datFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datFilePath);

        string launcherPath = ResolveLauncherPath(datFilePath);

        if (!File.Exists(launcherPath))
        {
            return Result.Failure(DomainErrors.GameLaunch.LauncherNotFound(launcherPath));
        }

        try
        {
            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = launcherPath,
                WorkingDirectory = Path.GetDirectoryName(launcherPath) ?? string.Empty,
                // Like a double click: a launcher that asks for admin rights at start gets the UAC
                // prompt instead of failing with error 740.
                UseShellExecute = true
            });

            if (process is null)
            {
                return Result.Failure(DomainErrors.GameLaunch.LaunchFailed(
                    "Process.Start returned null — the launcher could not be started."));
            }

            // No waiting: the launcher restarts itself after it updates itself, so this process can
            // end long before the game starts.
            process.Dispose();
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(DomainErrors.GameLaunch.LaunchFailed(ex.Message));
        }
    }

    private static string ResolveLauncherPath(string datFilePath)
    {
        // ShellExecute reads a relative file name against the working directory we pass, so a
        // relative path would start a different file than the one checked here.
        string fullDatFilePath = Path.GetFullPath(datFilePath);

        if (Directory.Exists(fullDatFilePath))
        {
            return Path.Combine(fullDatFilePath, LauncherExecutable);
        }

        if (File.Exists(fullDatFilePath))
        {
            string dirPath = Path.GetDirectoryName(fullDatFilePath) ?? string.Empty;
            return Path.Combine(dirPath, LauncherExecutable);
        }

        return Path.Combine(fullDatFilePath, LauncherExecutable);
    }
}
