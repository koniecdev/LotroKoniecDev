using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Errors;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Application.Features.AppUpdating;

internal sealed partial class CheckForAppUpdateQueryHandler
    : IQueryHandler<CheckForAppUpdateQuery, Result<AppUpdateCheckResponse>>
{
    private readonly IGitHubReleaseClient _releaseClient;
    private readonly IAppUpdateWorkspace _workspace;
    private readonly ILogger<CheckForAppUpdateQueryHandler> _logger;

    public async ValueTask<Result<AppUpdateCheckResponse>> Handle(
        CheckForAppUpdateQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Runs before the version check, so a developer build cleans up too.
        _workspace.CleanUpOldDownloads();

        if (!DesktopVersion.TryParse(query.InstalledVersion, out DesktopVersion? installed) || installed.IsDevelopmentBuild)
        {
            return Result.Success(AppUpdateCheckResponse.DevelopmentBuild());
        }

        Result<IReadOnlyList<GitHubRelease>> releases = await _releaseClient.ListReleasesAsync(cancellationToken);
        if (releases.IsFailure)
        {
            return Result.Success(Failed(releases.Error));
        }

        AvailableAppUpdate? newest = FindNewestRelease(releases.Value);
        if (newest is null)
        {
            return Result.Success(Failed(DomainErrors.AppUpdate.NoDesktopRelease));
        }

        if (!newest.Version.IsNewerThan(installed))
        {
            return Result.Success(AppUpdateCheckResponse.UpToDate());
        }

        LogUpdateAvailable(_logger, installed.ToString(), newest.Version.ToString());
        return Result.Success(AppUpdateCheckResponse.UpdateAvailable(newest));
    }

    public CheckForAppUpdateQueryHandler(
        IGitHubReleaseClient releaseClient,
        IAppUpdateWorkspace workspace,
        ILogger<CheckForAppUpdateQueryHandler> logger)
    {
        _releaseClient = releaseClient;
        _workspace = workspace;
        _logger = logger;
    }

    /// <summary>
    /// The highest published desktop release that carries both files at GitHub's own release
    /// addresses. Drafts, prereleases, the server's <c>v</c> releases and anything off the naming
    /// contract are skipped, so a half-done release is never offered.
    /// </summary>
    private static AvailableAppUpdate? FindNewestRelease(IReadOnlyList<GitHubRelease> releases)
    {
        AvailableAppUpdate? newest = null;
        foreach (GitHubRelease release in releases)
        {
            if (release.IsDraft
                || release.IsPrerelease
                || !DesktopReleasePolicy.TryParseTag(release.TagName, out DesktopVersion? version))
            {
                continue;
            }

            Uri? installerUri = FindAssetUri(release, version, DesktopReleasePolicy.InstallerAssetName(version));
            Uri? checksumUri = FindAssetUri(release, version, DesktopReleasePolicy.ChecksumAssetName(version));
            if (installerUri is null || checksumUri is null)
            {
                continue;
            }

            if (newest is null || version.IsNewerThan(newest.Version))
            {
                newest = new AvailableAppUpdate(version, installerUri, checksumUri);
            }
        }

        return newest;
    }

    private static Uri? FindAssetUri(GitHubRelease release, DesktopVersion version, string assetName)
    {
        Uri expected = DesktopReleasePolicy.DownloadUri(version, assetName);
        return release.Assets.Any(asset =>
            string.Equals(asset.Name, assetName, StringComparison.Ordinal) && asset.DownloadUri == expected)
            ? expected
            : null;
    }

    /// <summary>
    /// A refused host may be an attack, so it is a Warning; being offline or rate limited is the normal
    /// result of the player's network (#977).
    /// </summary>
    private AppUpdateCheckResponse Failed(Error error)
    {
        if (error.Code == DomainErrors.AppUpdate.HostRefusedCode)
        {
            LogCheckRefusedHost(_logger, error.Message);
        }
        else
        {
            LogCheckFailed(_logger, error.Code, error.Message);
        }

        return AppUpdateCheckResponse.CheckFailed(error);
    }

    [LoggerMessage(
        EventId = EventIds.AppUpdateAvailable,
        Level = LogLevel.Information,
        Message = "Desktop app {Installed} found a newer release {Available}")]
    private static partial void LogUpdateAvailable(ILogger logger, string installed, string available);

    [LoggerMessage(
        EventId = EventIds.AppUpdateCheckFailed,
        Level = LogLevel.Information,
        Message = "The desktop app's update check did not finish ({Code}): {Error}")]
    private static partial void LogCheckFailed(ILogger logger, string code, string error);

    [LoggerMessage(
        EventId = EventIds.AppUpdateHostRefused,
        Level = LogLevel.Warning,
        Message = "The desktop app's update check refused an answer from outside GitHub: {Error}")]
    private static partial void LogCheckRefusedHost(ILogger logger, string error);
}
