namespace LotroKoniecDev.Application.Features.GameLaunching;

public interface IGameLauncher
{
    Result<int> Launch(string datFilePath);
}
