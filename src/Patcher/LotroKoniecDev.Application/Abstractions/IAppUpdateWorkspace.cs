namespace LotroKoniecDev.Application.Abstractions;

/// <summary>The folders the desktop app's update writes to (spec 0016).</summary>
public interface IAppUpdateWorkspace
{
    /// <summary>
    /// Deletes the folders earlier updates left behind. Best effort: a folder in use stays for the
    /// next start, and nothing here ever fails the check.
    /// </summary>
    void CleanUpOldDownloads();

    /// <summary>A new, empty folder for one update's downloads.</summary>
    Result<string> CreateDownloadFolder();

    /// <summary>
    /// Deletes one update's folder after the update failed. Best effort, like
    /// <see cref="CleanUpOldDownloads"/>: a folder that cannot go now goes at the next start.
    /// </summary>
    void DeleteDownloadFolder(string folder);

    /// <summary>
    /// The absolute path of the installer's log file, with its folder created: Inno Setup stops when it
    /// cannot create the log.
    /// </summary>
    Result<string> PrepareInstallerLogPath();
}
