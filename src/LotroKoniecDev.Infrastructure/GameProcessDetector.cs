using System.Diagnostics;
using LotroKoniecDev.Application.Abstractions;

namespace LotroKoniecDev.Infrastructure;

public sealed class GameProcessDetector : IGameProcessDetector
{
    private static readonly string[] LotroProcessNames =
    [
        "lotroclient",
        "lotroclient64",
        "LotroLauncher",
        "TurbineLauncher"
    ];

    public bool IsLotroRunning()
    {
        try
        {
            foreach (string processName in LotroProcessNames)
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
        catch
        {
            return false;
        }
    }
}
