using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.Storage;

/// <summary>
/// Stores the downloaded translation file and two small files next to it: <c>.etag</c> for the
/// conditional request and <c>.endpoint</c> for the last download URL that worked (#611). The launch
/// sync can then check for changes cheaply, and still find the file when discovery is unreachable.
/// </summary>
public sealed class TranslationFileCache : ITranslationFileCache
{
    public string? ReadETag(string translationFilePath) => ReadSidecar(ETagPath(translationFilePath));

    public string? ReadEndpointHref(string translationFilePath) => ReadSidecar(EndpointPath(translationFilePath));

    public Result Save(string translationFilePath, string content, string eTag)
    {
        try
        {
            EnsureDirectoryExists(translationFilePath);

            File.WriteAllText(translationFilePath, content);
            File.WriteAllText(ETagPath(translationFilePath), eTag);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(DomainErrors.TranslationFileSync.CacheWriteError(translationFilePath, ex.Message));
        }
    }

    public Result SaveEndpointHref(string translationFilePath, string endpointHref)
    {
        try
        {
            EnsureDirectoryExists(translationFilePath);
            File.WriteAllText(EndpointPath(translationFilePath), endpointHref);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(DomainErrors.TranslationFileSync.CacheWriteError(translationFilePath, ex.Message));
        }
    }

    private static string? ReadSidecar(string sidecarPath)
    {
        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(sidecarPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing or unreadable file here just means "nothing cached". The sync then does a full
            // download, or resolves the endpoint from discovery only. We catch the same errors the
            // writers do: a file written by an elevated run raises UnauthorizedAccessException for a
            // normal one, and that must not stop the launch.
            return null;
        }
    }

    private static void EnsureDirectoryExists(string translationFilePath)
    {
        string? directory = Path.GetDirectoryName(translationFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string ETagPath(string translationFilePath) => translationFilePath + ".etag";

    private static string EndpointPath(string translationFilePath) => translationFilePath + ".endpoint";
}
