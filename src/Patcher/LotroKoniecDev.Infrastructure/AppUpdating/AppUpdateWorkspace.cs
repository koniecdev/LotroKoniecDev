using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.AppUpdating;

/// <summary>
/// The update's download folders and the installer's log (spec 0016). The desktop app keeps both in
/// <see cref="FolderName"/> next to its exe: the log in the folder itself, so the clean-up of old
/// download folders never removes it.
/// </summary>
public sealed class AppUpdateWorkspace : IAppUpdateWorkspace
{
    public const string FolderName = "updates";

    private const string InstallerLogFileName = "update-install.log";

    private readonly string _downloadsRoot;
    private readonly string _logFolder;

    public void CleanUpOldDownloads()
    {
        try
        {
            if (!Directory.Exists(_downloadsRoot))
            {
                return;
            }

            foreach (string folder in Directory.EnumerateDirectories(_downloadsRoot))
            {
                TryDelete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next start tries again.
        }
    }

    public Result<string> CreateDownloadFolder()
    {
        string folder = Path.Combine(_downloadsRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            return Result.Success(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<string>(DomainErrors.AppUpdate.FileSystemError(folder, ex.Message));
        }
    }

    public void DeleteDownloadFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        TryDelete(folder);
    }

    public Result<string> PrepareInstallerLogPath()
    {
        try
        {
            Directory.CreateDirectory(_logFolder);
            return Result.Success(Path.Combine(_logFolder, InstallerLogFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<string>(DomainErrors.AppUpdate.FileSystemError(_logFolder, ex.Message));
        }
    }

    /// <param name="downloadsRoot">The folder that holds one sub-folder per update.</param>
    /// <param name="logFolder">The folder the installer writes its log to.</param>
    public AppUpdateWorkspace(string downloadsRoot, string logFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(logFolder);

        _downloadsRoot = Path.GetFullPath(downloadsRoot);
        _logFolder = Path.GetFullPath(logFolder);
    }

    /// <summary>
    /// A folder whose installer is still running (the new version starts before the old installer has
    /// ended) is locked and stays for the next start.
    /// </summary>
    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next start.
        }
    }
}
