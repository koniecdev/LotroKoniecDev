using System.Diagnostics;
using LotroKoniecDev.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Infrastructure.Diagnostics;

public sealed partial class GameProcessDetector : IGameProcessDetector
{
    private static readonly string[] AllLotroProcessNames =
    [
        "lotroclient",
        "lotroclient64",
        "LotroLauncher",
        "TurbineLauncher"
    ];

    private readonly ILogger<GameProcessDetector> _logger;

    public GameProcessDetector(ILogger<GameProcessDetector> logger)
    {
        _logger = logger;
    }

    public bool IsLotroRunning()
    {
        try
        {
            foreach (string processName in AllLotroProcessNames)
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
            LogGameProcessCheckFailed(_logger, ex);
            return false;
        }
    }

    [LoggerMessage(EventId = EventIds.GameProcessCheckFailed, Level = LogLevel.Debug, Message = "Failed to check for running LOTRO processes")]
    private static partial void LogGameProcessCheckFailed(ILogger logger, Exception exception);
}
