using System.Security.Cryptography;
using System.Text;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Keeps one copy of the translation file per content hash on the API's own disk (PERF-09, #715,
/// ADR-0064). A full download streams that copy in small pieces. So a crowd of players whose ETags all
/// went stale at once, for example on the day of a game update, costs one database read of the
/// multi-MB content, not one read and one in-memory copy per player.
/// Only one copy is written at a time. Everyone else waits for it and then reads the same file. A file
/// gets its final name only after it is fully on disk and its SHA-256 matches the stored hash, so a
/// file with that name always holds exactly the bytes the ETag promises (AUDIT-SEC-01, #391).
/// Like the projector's gate, this assumes one API process per directory.
/// </summary>
internal sealed partial class TranslationFileDiskCache : ITranslationFileDiskCache
{
    private const string CopyExtension = ".txt";
    private const string TemporaryExtension = ".tmp";
    private const int WriteBufferSize = 64 * 1024;

    /// <summary>
    /// The same bytes the projector hashes with <see cref="Encoding.UTF8"/>, which adds no BOM.
    /// </summary>
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CancellationToken _applicationStopping;
    private readonly string _directory;
    private readonly ILogger<TranslationFileDiskCache> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public TranslationFileDiskCache(
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime applicationLifetime,
        IOptions<TranslationFileDiskCacheSettings> settings,
        ILogger<TranslationFileDiskCache> logger)
    {
        _scopeFactory = scopeFactory;
        _applicationStopping = applicationLifetime.ApplicationStopping;
        _directory = settings.Value.Directory;
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
            // Only the wait follows the caller's token. The write itself runs on the host's token: the
            // CLI gives up after a few seconds, so a slow first write cut short by its own caller would
            // make the next waiter load the whole file from the database again.
            return await OpenOrWriteCopyAsync(language, contentHash, _applicationStopping);
        }
        finally
        {
            _writeGate.Release();
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

        // A rebuild may have replaced the file after the caller read its hash, and the caller before
        // this one may already have written the new copy. The hash is cheap to read, the content is not.
        string? currentHash = await readDbContext.PrecomputedTranslationFiles
            .Where(file => file.Language == language)
            .Select(file => file.ContentHash)
            .FirstOrDefaultAsync(cancellationToken);

        if (currentHash is null)
        {
            return null;
        }

        if (TryOpenCopy(language, currentHash) is { } current)
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
        string copyPath = CopyPath(language, stored.ContentHash);
        Directory.CreateDirectory(_directory);
        string temporaryPath = Path.Combine(_directory, $"{language}-{Guid.NewGuid():N}{TemporaryExtension}");

        // FileShare.Delete lets the file be renamed below while this handle stays open. The handle is
        // what the response reads from, so a later write that removes this copy cannot pull it away
        // from a download that is still running.
        FileStream file = new(temporaryPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous,
        });

        try
        {
            await using (StreamWriter writer = new(file, Utf8WithoutBom, WriteBufferSize, leaveOpen: true))
            {
                await writer.WriteAsync(stored.Content.AsMemory(), cancellationToken);
            }

            file.Flush(flushToDisk: true);
            file.Position = 0;
            string writtenHash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
            if (!string.Equals(writtenHash, stored.ContentHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The stored translation file for '{language}' does not hash to its stored ETag {stored.ContentHash}; the copy on disk hashes to {writtenHash}.");
            }

            file.Position = 0;
            File.Move(temporaryPath, copyPath, overwrite: true);
        }
        catch
        {
            await file.DisposeAsync();
            DeleteIfPresent(temporaryPath);
            throw;
        }

        LogCopyWritten(_logger, language, stored.ContentHash, file.Length);
        RemoveOtherCopies(language, Path.GetFileName(copyPath));

        return new TranslationFileCopy(file, stored.ContentHash);
    }

    private TranslationFileCopy? TryOpenCopy(string language, string contentHash)
    {
        try
        {
            FileStream file = new(CopyPath(language, contentHash), new FileStreamOptions
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
    /// Removes every older copy of the language, and any half-written file a crash left behind, so the
    /// directory never holds more than one copy. A download still reading an old copy keeps it until it
    /// ends, because the handle stays valid after the name is gone.
    /// </summary>
    private void RemoveOtherCopies(string language, string keptFileName)
    {
        foreach (string path in Directory.EnumerateFiles(_directory, $"{language}-*"))
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
                LogCopyRemovalFailed(_logger, exception, Path.GetFileName(path));
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
    /// Both parts become a file name, so both are checked here, where every path is built.
    /// </summary>
    private string CopyPath(string language, string contentHash)
    {
        if (language.Length == 0 || !language.All(char.IsAsciiLetter))
        {
            throw new ArgumentException("A language code is ASCII letters only.", nameof(language));
        }

        if (contentHash.Length != PrecomputedTranslationFile.ContentHashLength || !contentHash.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("A content hash is a hex SHA-256.", nameof(contentHash));
        }

        return Path.Combine(_directory, $"{language}-{contentHash}{CopyExtension}");
    }

    private sealed record StoredFile(string Content, string ContentHash);

    [LoggerMessage(EventId = EventIds.TranslationFileCopyWritten, Level = LogLevel.Information, Message = "Wrote the disk copy of the translation file for '{Language}' ({ContentHash}, {ByteCount} bytes)")]
    private static partial void LogCopyWritten(ILogger logger, string language, string contentHash, long byteCount);

    [LoggerMessage(EventId = EventIds.TranslationFileCopyRemovalFailed, Level = LogLevel.Warning, Message = "Could not remove the old translation file copy '{FileName}'; the next write tries again")]
    private static partial void LogCopyRemovalFailed(ILogger logger, Exception exception, string fileName);
}
