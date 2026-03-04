namespace LotroKoniecDev.Application.Abstractions;

public interface IGameLauncher
{
    Task<Result<int>> LaunchAndWaitForExitAsync(string lotroPath, CancellationToken cancellationToken = default);
}
