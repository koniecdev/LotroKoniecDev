using LotroKoniecDev.Domain.Core.BuildingBlocks;

namespace LotroKoniecDev.Application.Features.AppUpdating;

public enum AppUpdateCheckOutcome
{
    /// <summary>An unstamped build (0.0.0). Nothing was asked.</summary>
    DevelopmentBuild,

    /// <summary>No desktop release is newer than the app.</summary>
    UpToDate,

    /// <summary>A newer desktop release with both files exists.</summary>
    UpdateAvailable,

    /// <summary>
    /// The check did not finish, or found no desktop release at all. The app keeps running on its
    /// version and shows why.
    /// </summary>
    CheckFailed
}

/// <summary>A release the app may install: both download addresses already passed the policy.</summary>
public sealed record AvailableAppUpdate(DesktopVersion Version, Uri InstallerUri, Uri ChecksumUri);

public sealed record AppUpdateCheckResponse(
    AppUpdateCheckOutcome Outcome,
    AvailableAppUpdate? Update,
    Error? Failure)
{
    public static AppUpdateCheckResponse DevelopmentBuild() => new(AppUpdateCheckOutcome.DevelopmentBuild, null, null);

    public static AppUpdateCheckResponse UpToDate() => new(AppUpdateCheckOutcome.UpToDate, null, null);

    public static AppUpdateCheckResponse UpdateAvailable(AvailableAppUpdate update) =>
        new(AppUpdateCheckOutcome.UpdateAvailable, update, null);

    public static AppUpdateCheckResponse CheckFailed(Error failure) =>
        new(AppUpdateCheckOutcome.CheckFailed, null, failure);
}
