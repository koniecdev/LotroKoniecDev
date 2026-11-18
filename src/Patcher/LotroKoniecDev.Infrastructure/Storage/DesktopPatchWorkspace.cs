using System.Text;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.Storage;

/// <summary>
/// The desktop app's translation files and patch state in one folder (#42). The game version file
/// does not say which game file it describes, so <see cref="PatchedGameFileName"/> records that next
/// to it. A folder chosen in the settings and a hand edit of <c>config.json</c> are both caught, because
/// the check runs before every patch.
/// </summary>
public sealed class DesktopPatchWorkspace : IDesktopPatchWorkspace
{
    public const string PatchedGameFileName = "patched_game_file.txt";

    private const string TranslationFileName = "polish.txt";

    private const string DownloadFolderName = "download";

    private const string GameVersionFileName = "last_known_game_version.txt";

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _dataFolder;
    private readonly string _patchedGameFilePath;

    public string DownloadedTranslationFilePath { get; }

    public string WorkingTranslationFilePath { get; }

    public string GameVersionFilePath { get; }

    /// <remarks>
    /// The bytes are copied through streams, so the copy never takes the source's attributes: a
    /// read-only source made through <c>File.Copy</c> would leave a read-only working copy that every
    /// later run fails to replace. The source is shared for reading and writing, as <c>File.Copy</c>
    /// shares it, so a file another program holds open is still read.
    /// </remarks>
    public Result UseTranslationFile(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        FileStream source;
        try
        {
            source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unplugged drive, a missing folder and a network share that does not answer each fail
            // with their own error, and each means the same to the player: the file is gone.
            return Result.Failure(SourceFailure(sourcePath, ex));
        }

        string tempPath = TempPath();
        Result copied;
        using (source)
        {
            try
            {
                Directory.CreateDirectory(_dataFolder);
                copied = CopyToNewFile(source, sourcePath, tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                copied = Result.Failure(DomainErrors.DesktopPatchWorkspace.FileSystemError(tempPath, ex.Message));
            }
        }

        if (copied.IsFailure)
        {
            DeleteQuietly(tempPath);
            return copied;
        }

        try
        {
            ClearReadOnly(WorkingTranslationFilePath);
            File.Move(tempPath, WorkingTranslationFilePath, overwrite: true);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(tempPath);
            return Result.Failure(DomainErrors.DesktopPatchWorkspace.FileSystemError(WorkingTranslationFilePath, ex.Message));
        }
    }

    public Result ResetPatchStateIfGameFileChanged(string datFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datFilePath);

        string gameFile = Path.GetFullPath(datFilePath);
        if (string.Equals(ReadPatchedGameFile(), gameFile, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        // The version file goes first: a failed write of the marker after it then costs one more full
        // patch, never a skipped one. File.Delete throws on a missing folder, so the folder comes first.
        try
        {
            Directory.CreateDirectory(_dataFolder);
            File.Delete(GameVersionFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(DomainErrors.DesktopPatchWorkspace.FileSystemError(GameVersionFilePath, ex.Message));
        }

        string tempPath = TempPath();
        try
        {
            File.WriteAllText(tempPath, gameFile, Utf8WithoutBom);
            File.Move(tempPath, _patchedGameFilePath, overwrite: true);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(tempPath);
            return Result.Failure(DomainErrors.DesktopPatchWorkspace.FileSystemError(_patchedGameFilePath, ex.Message));
        }
    }

    /// <param name="dataFolder">The app's own folder, which only administrators can change.</param>
    public DesktopPatchWorkspace(string dataFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);

        _dataFolder = Path.GetFullPath(dataFolder);
        _patchedGameFilePath = Path.Combine(_dataFolder, PatchedGameFileName);
        DownloadedTranslationFilePath = Path.Combine(_dataFolder, DownloadFolderName, TranslationFileName);
        WorkingTranslationFilePath = Path.Combine(_dataFolder, TranslationFileName);
        GameVersionFilePath = Path.Combine(_dataFolder, GameVersionFileName);
    }

    /// <summary>A missing or unreadable marker counts as another game file: one extra patch is safe.</summary>
    private string? ReadPatchedGameFile()
    {
        try
        {
            return File.Exists(_patchedGameFilePath) ? File.ReadAllText(_patchedGameFilePath, Utf8WithoutBom).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string TempPath() => Path.Combine(_dataFolder, $"{Guid.NewGuid():N}.tmp");

    /// <summary>
    /// A read that fails after the open (a range another program locked, a drive lost during the copy)
    /// is the player's file again, not the data folder, so it gets the same answer as a failed open.
    /// A failed write throws and is the data folder's error.
    /// </summary>
    private static Result CopyToNewFile(FileStream source, string sourcePath, string targetPath)
    {
        using FileStream target = new(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read;
            try
            {
                read = source.Read(buffer, 0, buffer.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result.Failure(SourceFailure(sourcePath, ex));
            }

            if (read == 0)
            {
                return Result.Success();
            }

            target.Write(buffer, 0, read);
        }
    }

    private static Error SourceFailure(string sourcePath, Exception exception) =>
        File.Exists(sourcePath)
            ? DomainErrors.DesktopPatchWorkspace.SourceUnreadable(sourcePath, exception.Message)
            : DomainErrors.DesktopPatchWorkspace.SourceNotFound(sourcePath);

    /// <summary>An administrator may have set the flag by hand; it would stop every later replace.</summary>
    private static void ClearReadOnly(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp file with a random name harms nothing; it stays.
        }
    }
}
