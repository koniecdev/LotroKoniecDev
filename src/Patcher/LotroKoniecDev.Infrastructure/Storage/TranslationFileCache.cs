using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.Storage;

/// <summary>
/// Stores the downloaded translation file and two sidecars next to it — <c>.etag</c> for the
/// conditional request, <c>.endpoint</c> for the last-known-good download URL (#611) — so the launch
/// sync can revalidate cheaply and still find the file when discovery itself is unreachable.
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
            // A missing or unreadable sidecar just means "nothing cached" — the sync degrades to a
            // full download, or to discovery-only endpoint resolution. The catch matches the writers':
            // a sidecar written by an elevated run is readable only as UnauthorizedAccessException to
            // a plain one, and that must not take the launch down.
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
