namespace LotroKoniecDev.Application.Abstractions;

public interface IGameLauncher
{
    Task<Result<int>> LaunchAndWaitForExitAsync(string datFilePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Launches the LOTRO launcher without waiting for it to exit (fire-and-forget).
    /// Used by the simplified flow where waiting is pointless due to UAC restart.
    /// </summary>
    Result Launch(string datFilePath);
}
