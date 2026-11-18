namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Reads this repository's GitHub releases and downloads their files for the desktop app's update
/// (spec 0016). Every answer must come from a host that
/// <see cref="Features.AppUpdating.DesktopReleasePolicy.IsAllowedHost"/> accepts, checked on the final
/// address after redirects and before any byte of the body is read.
/// </summary>
public interface IGitHubReleaseClient
{
    /// <summary>The first page of releases, within the check's time limit.</summary>
    Task<Result<IReadOnlyList<GitHubRelease>>> ListReleasesAsync(CancellationToken cancellationToken);

    Task<Result<string>> DownloadTextAsync(Uri uri, long maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Streams the file to <paramref name="destinationPath"/>, which must not exist yet. A failed
    /// download deletes the file it created, and never a file it did not create.
    /// </summary>
    Task<Result> DownloadFileAsync(
        Uri uri,
        string destinationPath,
        long maxBytes,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken);
}

public sealed record GitHubRelease(
    string TagName,
    bool IsDraft,
    bool IsPrerelease,
    IReadOnlyList<GitHubReleaseAsset> Assets);

/// <param name="DownloadUri"><c>null</c> when GitHub sent no absolute address.</param>
public sealed record GitHubReleaseAsset(string Name, Uri? DownloadUri);
