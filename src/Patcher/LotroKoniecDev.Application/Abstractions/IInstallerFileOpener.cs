namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Opens a downloaded installer so that Windows starts, with admin rights, the bytes that were hashed
/// (spec 0016).
/// </summary>
public interface IInstallerFileOpener
{
    Result<IOpenedInstaller> Open(string installerPath);
}

/// <summary>
/// An installer held open with read sharing only, so no other program can change, delete or rename it
/// until this is disposed.
/// </summary>
public interface IOpenedInstaller : IDisposable
{
    /// <summary>The lower-case hex SHA-256 of the file, read through the held handle.</summary>
    string Sha256Hex { get; }

    /// <summary>
    /// Starts the installer with admin rights and does not wait for it. A player without them sees the
    /// admin prompt during this call, and a "No" is <c>InstallerStartCancelled</c>.
    /// </summary>
    Result Start(string arguments);
}
