using LotroKoniecDev.Application.Abstractions.Messaging;

namespace LotroKoniecDev.Application.Features.AppUpdating;

/// <summary>
/// Downloads a desktop release, checks the installer against its SHA-256 file and starts it (spec
/// 0016). On success the installer is running and the caller closes the app; the installer starts the
/// new version at its end.
/// </summary>
public sealed record InstallAppUpdateCommand(
    DesktopVersion Version,
    Uri InstallerUri,
    Uri ChecksumUri) : ICommand<Result>;
