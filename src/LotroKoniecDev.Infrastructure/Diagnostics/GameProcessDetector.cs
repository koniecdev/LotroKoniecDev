using System.Diagnostics;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Infrastructure.Diagnostics;

public sealed class GameProcessDetector : IGameProcessDetector
{
    private static readonly string[] GameClientProcessNames =
    [
        "lotroclient",
        "lotroclient64"
    ];

    private static readonly string[] LauncherProcessNames =
    [
        "LotroLauncher",
        "TurbineLauncher"
    ];

    private static readonly string[] AllLotroProcessNames =
    [
        ..GameClientProcessNames,
        ..LauncherProcessNames
    ];

    private readonly ILogger<GameProcessDetector> _logger;

    public GameProcessDetector(ILogger<GameProcessDetector> logger)
    {
        _logger = logger;
    }

    public bool IsLotroRunning() => AnyProcessRunning(AllLotroProcessNames);

    public bool IsGameClientRunning() => AnyProcessRunning(GameClientProcessNames);

    public bool IsLotroLauncherRunning() => AnyProcessRunning(LauncherProcessNames);

    public Result KillLotroProcesses()
    {
        try
        {
            foreach (string processName in AllLotroProcessNames)
            {
                Process[] processes = Process.GetProcessesByName(processName);
                foreach (Process process in processes)
                {
                    using (process)
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                            process.WaitForExit(5000);
                        }
                    }
                }
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(DomainErrors.GameLaunch.KillFailed(ex.Message));
        }
    }

    private bool AnyProcessRunning(string[] processNames)
    {
        try
        {
            foreach (string processName in processNames)
            {
                Process[] processes = Process.GetProcessesByName(processName);
                bool found = processes.Length > 0;

                foreach (Process process in processes)
                {
                    process.Dispose();
                }

                if (found)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check for running LOTRO processes");
            return false;
        }
    }
}
