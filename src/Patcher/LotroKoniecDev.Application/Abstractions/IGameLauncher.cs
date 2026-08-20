namespace LotroKoniecDev.Application.Abstractions;

public interface IGameLauncher
{
    /// <summary>
    /// Starts the LOTRO launcher and does not wait for it to exit. The launcher takes care of any
    /// pending game update on its own, including asking for admin rights.
    /// </summary>
    Result Launch(string datFilePath);
}
