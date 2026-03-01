namespace LotroKoniecDev.Application.Abstractions;

public interface IGameLauncher
{
    Result Launch(string lotroPath);
    Result<int> LaunchAndWaitForExit(string lotroPath);
}
