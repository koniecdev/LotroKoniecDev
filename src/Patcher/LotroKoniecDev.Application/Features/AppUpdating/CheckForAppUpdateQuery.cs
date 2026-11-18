using LotroKoniecDev.Application.Abstractions.Messaging;

namespace LotroKoniecDev.Application.Features.AppUpdating;

/// <summary>
/// Looks for a newer desktop release on GitHub (spec 0016). It never fails the caller: a check that
/// could not finish comes back as <see cref="AppUpdateCheckOutcome.CheckFailed"/>, so the app keeps
/// running on its version.
/// </summary>
/// <param name="InstalledVersion">
/// The version stamped into the exe. A value that is not a valid <see cref="DesktopVersion"/>, or
/// 0.0.0, is a developer build and is never checked.
/// </param>
public sealed record CheckForAppUpdateQuery(string? InstalledVersion) : IQuery<Result<AppUpdateCheckResponse>>;
