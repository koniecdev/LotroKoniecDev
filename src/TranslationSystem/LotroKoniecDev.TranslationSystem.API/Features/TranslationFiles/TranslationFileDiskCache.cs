using System.Security.Cryptography;
using System.Text;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// One copy of the translation file per content hash on the API's disk (PERF-09, ADR-0064), so a crowd
/// of stale ETags on update day costs one database read, not one read and one in-memory copy per
/// player. One write at a time; a file gets its final name only after its SHA-256 matches the stored
/// hash, so it always holds the bytes the ETag promises (AUDIT-SEC-01). Like the projector's gate, this
/// assumes one API process per directory.
/// </summary>
internal sealed partial class TranslationFileDiskCache : ITranslationFileDiskCache, IDisposable
{
    private const string PrivateDirectoryPrefix = "lotro-translation-files-";
    private const string CopyExtension = ".txt";
    private const string TemporaryExtension = ".tmp";
    private const int WriteBufferSize = 64 * 1024;

    /// <summary>
    /// The bytes the projector hashes: <see cref="Encoding.UTF8"/> adds no BOM either.
    /// </summary>
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CancellationToken _applicationStopped;
    private readonly string? _configuredDirectory;
    private readonly ILogger<TranslationFileDiskCache> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// Set only inside the write gate. A read outside it sees no directory (no copy yet) or a complete
    /// one.
    /// </summary>
    private string? _directory;

    public TranslationFileDiskCache(
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime applicationLifetime,
        IOptions<TranslationFileDiskCacheSettings> settings,
        ILogger<TranslationFileDiskCache> logger)
    {
        _scopeFactory = scopeFactory;
        _applicationStopped = applicationLifetime.ApplicationStopped;
        _configuredDirectory = settings.Value.Directory;
        _directory = _configuredDirectory;
        _logger = logger;
    }

    public async Task<TranslationFileCopy?> OpenAsync(string language, string contentHash, CancellationToken cancellationToken)
    {
        if (TryOpenCopy(language, contentHash) is { } copy)
        {
            return copy;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            // Only the wait follows the caller's token. The CLI gives up after a few seconds, and a first
            // write cut short by its caller would make the next waiter load the file again. The write
            // stops only once the host has stopped, after running requests finished.
            return await OpenOrWriteCopyAsync(language, contentHash, _applicationStopped);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        if (_configuredDirectory is not null || _directory is null)
        {
            return;
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // It sits in the temp folder, which the OS or the next container clears.
        }
    }

    private async Task<TranslationFileCopy?> OpenOrWriteCopyAsync(
        string language,
        string contentHash,
        CancellationToken cancellationToken)
    {
        if (TryOpenCopy(language, contentHash) is { } writtenWhileWaiting)
        {
            return writtenWhileWaiting;
        }

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IApplicationReadDbContext readDbContext = scope.ServiceProvider.GetRequiredService<IApplicationReadDbContext>();

        // A rebuild may have replaced the file since the caller read its hash, and the previous waiter
        // may have written the new copy. The hash is cheap to read, the content is not.
        string? currentHash = await readDbContext.PrecomputedTranslationFiles
            .Where(file => file.Language == language)
            .Select(file => file.ContentHash)
            .FirstOrDefaultAsync(cancellationToken);

        if (currentHash is null)
        {
            return null;
        }

        if (!string.Equals(currentHash, contentHash, StringComparison.Ordinal)
            && TryOpenCopy(language, currentHash) is { } current)
        {
            return current;
        }

        StoredFile? stored = await readDbContext.PrecomputedTranslationFiles
            .Where(file => file.Language == language)
            .Select(file => new StoredFile(file.Content, file.ContentHash))
            .FirstOrDefaultAsync(cancellationToken);

        return stored is null
            ? null
            : await WriteCopyAsync(language, stored, cancellationToken);
    }

    private async Task<TranslationFileCopy> WriteCopyAsync(string language, StoredFile stored, CancellationToken cancellationToken)
    {
        string copyFileName = CopyFileName(language, stored.ContentHash);
        string directory = PrepareDirectory();
        string temporaryPath = Path.Combine(directory, $"{language}-{Guid.NewGuid():N}{TemporaryExtension}");
        long byteCount;

        try
        {
            await using (FileStream file = new(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            }))
            {
                await using (StreamWriter writer = new(file, Utf8WithoutBom, WriteBufferSize, leaveOpen: true))
                {
                    await writer.WriteAsync(stored.Content.AsMemory(), cancellationToken);
                }

                file.Flush(flushToDisk: true);
                file.Position = 0;
                string writtenHash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));

                // Case-insensitive, like the patcher's own check (TranslationFileContentIntegrity).
                if (!string.Equals(writtenHash, stored.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The stored translation file for '{language}' does not hash to its stored ETag {stored.ContentHash}; the copy on disk hashes to {writtenHash}.");
                }

                byteCount = file.Length;
            }

            File.Move(temporaryPath, Path.Combine(directory, copyFileName), overwrite: true);
        }
        catch
        {
            DeleteIfPresent(temporaryPath);
            throw;
        }

        LogCopyWritten(_logger, language, stored.ContentHash, byteCount);
        RemoveOtherCopies(directory, language, copyFileName);

        // A new read-only handle: on Windows the writer's handle would lock every other download out of
        // the file until the first one ends.
        return TryOpenCopy(language, stored.ContentHash)
            ?? throw new IOException($"The translation file copy '{copyFileName}' was gone right after it was written.");
    }

    /// <summary>
    /// Without a configured directory, <see cref="Directory.CreateTempSubdirectory"/> gives a random,
    /// owner-only folder, so no other local user can plant a file that would be served under a valid
    /// ETag. Called only inside the write gate.
    /// </summary>
    private string PrepareDirectory()
    {
        if (_configuredDirectory is not null)
        {
            Directory.CreateDirectory(_configuredDirectory);
            return _configuredDirectory;
        }

        if (_directory is { } existing && Directory.Exists(existing))
        {
            return existing;
        }

        string created = Directory.CreateTempSubdirectory(PrivateDirectoryPrefix).FullName;
        Volatile.Write(ref _directory, created);
        return created;
    }

    private TranslationFileCopy? TryOpenCopy(string language, string contentHash)
    {
        string copyFileName = CopyFileName(language, contentHash);
        if (Volatile.Read(ref _directory) is not { } directory)
        {
            return null;
        }

        try
        {
            // FileShare.Delete lets a later write remove this copy while a download still reads it.
            FileStream file = new(Path.Combine(directory, copyFileName), new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });

            return new TranslationFileCopy(file, contentHash);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Also removes half-written files a crash left behind. A download still reading an old copy keeps
    /// it, because its handle stays valid. A failure here never fails the download.
    /// </summary>
    private void RemoveOtherCopies(string directory, string language, string keptFileName)
    {
        string[] paths;
        try
        {
            paths = Directory.GetFiles(directory, $"{language}-*");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogCopyRemovalFailed(_logger, exception, directory);
            return;
        }

        foreach (string path in paths)
        {
            if (string.Equals(Path.GetFileName(path), keptFileName, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogCopyRemovalFailed(_logger, exception, path);
            }
        }
    }

    private static void DeleteIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next write removes it with the other leftovers.
        }
    }

    /// <summary>
    /// Both parts become a file name, so both are checked where every name is built. The language is a
    /// constant of the endpoint, so a bad one is a bug in the caller. The hash comes from our own stored
    /// file, so a bad one must end in a 500, never in the 400 an <see cref="ArgumentException"/> becomes.
    /// </summary>
    private static string CopyFileName(string language, string contentHash)
    {
        if (language.Length == 0 || !language.All(char.IsAsciiLetter))
        {
            throw new ArgumentException("A language code is ASCII letters only.", nameof(language));
        }

        if (contentHash.Length != PrecomputedTranslationFile.ContentHashLength || !contentHash.All(char.IsAsciiHexDigit))
        {
            throw new InvalidOperationException($"The stored content hash '{contentHash}' is not a hex SHA-256.");
        }

        // Hashes compare case-insensitively, so one hash must give one file name.
        return $"{language}-{contentHash.ToUpperInvariant()}{CopyExtension}";
    }

    private sealed record StoredFile(string Content, string ContentHash);

    [LoggerMessage(EventId = EventIds.TranslationFileCopyWritten, Level = LogLevel.Information, Message = "Wrote the disk copy of the translation file for '{Language}' ({ContentHash}, {ByteCount} bytes)")]
    private static partial void LogCopyWritten(ILogger logger, string language, string contentHash, long byteCount);

    [LoggerMessage(EventId = EventIds.TranslationFileCopyRemovalFailed, Level = LogLevel.Warning, Message = "Could not remove old translation file copies at '{Path}'; the next write tries again")]
    private static partial void LogCopyRemovalFailed(ILogger logger, Exception exception, string path);
}
