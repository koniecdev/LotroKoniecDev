namespace LotroKoniecDev.Application.Abstractions;

public interface IGameLauncher
{
    /// <summary>
    /// Launches the LOTRO launcher without waiting for it to exit (fire-and-forget).
    /// The launcher handles any pending game updates independently, including UAC elevation.
    /// </summary>
    Result Launch(string datFilePath);
}
