namespace LotroKoniecDev.Application.Abstractions;

public interface IGameLauncher
{
    Task<Result<int>> LaunchAndWaitForExitAsync(string datFilePath, CancellationToken cancellationToken = default);
}
