using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.Storage;

/// <summary>
/// Stores the downloaded translation file and a sidecar <c>.etag</c> file next to it, so the launch
/// sync can issue a conditional request and reuse the cached file when the server is unreachable.
/// </summary>
public sealed class TranslationFileCache : ITranslationFileCache
{
    public string? ReadETag(string translationFilePath)
    {
        string eTagPath = ETagPath(translationFilePath);
        if (!File.Exists(eTagPath))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(eTagPath);
        }
        catch (IOException)
        {
            // A missing or unreadable sidecar just means "no cached ETag" — fetch the full file.
            return null;
        }
    }

    public Result Save(string translationFilePath, string content, string eTag)
    {
        try
        {
            string? directory = Path.GetDirectoryName(translationFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(translationFilePath, content);
            File.WriteAllText(ETagPath(translationFilePath), eTag);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(DomainErrors.TranslationFileSync.CacheWriteError(translationFilePath, ex.Message));
        }
    }

    private static string ETagPath(string translationFilePath) => translationFilePath + ".etag";
}
