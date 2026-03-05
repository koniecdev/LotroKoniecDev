namespace LotroKoniecDev.Application.Abstractions;

public interface IGameProcessDetector
{
    bool IsLotroRunning();
    bool IsGameClientRunning();
    bool IsLotroLauncherRunning();
    Result KillLotroProcesses();
}
