using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.AppUpdating;

/// <summary>
/// The update's download folders under <c>%TEMP%\LotroPoPolsku\updates</c> and the installer's log
/// beside the app's own log in <c>%AppData%\LotroPoPolsku</c> (spec 0016).
/// </summary>
public sealed class AppUpdateWorkspace : IAppUpdateWorkspace
{
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
    /// <param name="logFolder">The app's own data folder, where the installer writes its log.</param>
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
