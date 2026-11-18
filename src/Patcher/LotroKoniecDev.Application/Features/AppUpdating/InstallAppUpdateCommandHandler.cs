using FluentValidation;
using FluentValidation.Results;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Errors;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Application.Features.AppUpdating;

internal sealed partial class InstallAppUpdateCommandHandler : ICommandHandler<InstallAppUpdateCommand, Result>
{
    private readonly IGitHubReleaseClient _releaseClient;
    private readonly IAppUpdateWorkspace _workspace;
    private readonly IInstallerFileOpener _installerFileOpener;
    private readonly IProgress<OperationProgress> _progress;
    private readonly IValidator<InstallAppUpdateCommand> _validator;
    private readonly ILogger<InstallAppUpdateCommandHandler> _logger;

    public async ValueTask<Result> Handle(InstallAppUpdateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ValidationResult validationResult = _validator.Validate(command);
        if (!validationResult.IsValid)
        {
            return Result.Failure(validationResult.ToValidationError(nameof(InstallAppUpdateCommand)));
        }

        Result<string> downloadFolder = _workspace.CreateDownloadFolder();
        if (downloadFolder.IsFailure)
        {
            return NotInstalled(downloadFolder.Error);
        }

        Result installed = await InstallFromFolderAsync(command, downloadFolder.Value, cancellationToken);
        if (installed.IsFailure)
        {
            // After the held installer is released, so a refused file does not stay under the
            // installer's own name. A started installer still runs from its folder, so that one stays.
            _workspace.DeleteDownloadFolder(downloadFolder.Value);
        }

        return installed;
    }

    public InstallAppUpdateCommandHandler(
        IGitHubReleaseClient releaseClient,
        IAppUpdateWorkspace workspace,
        IInstallerFileOpener installerFileOpener,
        IProgress<OperationProgress> progress,
        IValidator<InstallAppUpdateCommand> validator,
        ILogger<InstallAppUpdateCommandHandler> logger)
    {
        _releaseClient = releaseClient;
        _workspace = workspace;
        _installerFileOpener = installerFileOpener;
        _progress = progress;
        _validator = validator;
        _logger = logger;
    }

    private async Task<Result> InstallFromFolderAsync(
        InstallAppUpdateCommand command, string downloadFolder, CancellationToken cancellationToken)
    {
        string installerName = DesktopReleasePolicy.InstallerAssetName(command.Version);

        Result<string> checksumFile = await _releaseClient.DownloadTextAsync(
            command.ChecksumUri, DesktopReleasePolicy.MaxChecksumFileBytes, cancellationToken);
        if (checksumFile.IsFailure)
        {
            return NotInstalled(checksumFile.Error);
        }

        if (!DesktopReleasePolicy.TryParseChecksumFile(checksumFile.Value, installerName, out string? expectedHash))
        {
            // The release job always writes a valid file, so a broken one should never happen or means
            // the release was changed: a Warning, like a hash mismatch (#977).
            LogChecksumFileInvalid(_logger, installerName, command.Version.ToString());
            return Result.Failure(DomainErrors.AppUpdate.ChecksumFileInvalid(installerName));
        }

        string installerPath = Path.Combine(downloadFolder, installerName);
        Result download = await _releaseClient.DownloadFileAsync(
            command.InstallerUri, installerPath, DesktopReleasePolicy.MaxInstallerBytes, _progress, cancellationToken);
        if (download.IsFailure)
        {
            return NotInstalled(download.Error);
        }

        Result<string> logPath = _workspace.PrepareInstallerLogPath();
        if (logPath.IsFailure)
        {
            return NotInstalled(logPath.Error);
        }

        Result<IOpenedInstaller> opened = _installerFileOpener.Open(installerPath);
        if (opened.IsFailure)
        {
            return NotInstalled(opened.Error);
        }

        // The handle stays open until the installer has started, so Windows starts the bytes that were
        // hashed. Inno Setup's later reads of the file by path are not covered (spec 0016).
        using IOpenedInstaller installer = opened.Value;
        if (!string.Equals(installer.Sha256Hex, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            LogHashMismatch(_logger, installerName, command.Version.ToString());
            return Result.Failure(DomainErrors.AppUpdate.HashMismatch(installerName));
        }

        Result start = installer.Start(DesktopReleasePolicy.InstallerArguments(logPath.Value));
        if (start.IsFailure)
        {
            return NotInstalled(start.Error);
        }

        LogInstallerStarted(_logger, command.Version.ToString());
        return Result.Success();
    }

    /// <summary>
    /// A refused host may be an attack, so it is a Warning; a failed download or a cancelled admin
    /// prompt is the normal result of the player's network or choice (#977).
    /// </summary>
    private Result NotInstalled(Error error)
    {
        if (error.Code == DomainErrors.AppUpdate.HostRefusedCode)
        {
            LogDownloadRefusedHost(_logger, error.Message);
        }
        else
        {
            LogNotInstalled(_logger, error.Code, error.Message);
        }

        return Result.Failure(error);
    }

    [LoggerMessage(
        EventId = EventIds.AppUpdateHashMismatch,
        Level = LogLevel.Warning,
        Message = "The downloaded {FileName} for desktop release {Version} does not match its SHA-256 file; it was not started")]
    private static partial void LogHashMismatch(ILogger logger, string fileName, string version);

    [LoggerMessage(
        EventId = EventIds.AppUpdateChecksumFileInvalid,
        Level = LogLevel.Warning,
        Message = "The SHA-256 file of desktop release {Version} does not hold one hash for {FileName}; the installer was not downloaded")]
    private static partial void LogChecksumFileInvalid(ILogger logger, string fileName, string version);

    [LoggerMessage(
        EventId = EventIds.AppUpdateInstallerStarted,
        Level = LogLevel.Information,
        Message = "Started the installer of desktop release {Version}")]
    private static partial void LogInstallerStarted(ILogger logger, string version);

    [LoggerMessage(
        EventId = EventIds.AppUpdateInstallerNotStarted,
        Level = LogLevel.Information,
        Message = "The desktop app's update did not finish ({Code}): {Error}")]
    private static partial void LogNotInstalled(ILogger logger, string code, string error);

    [LoggerMessage(
        EventId = EventIds.AppUpdateDownloadHostRefused,
        Level = LogLevel.Warning,
        Message = "The desktop app's update refused a download from outside GitHub: {Error}")]
    private static partial void LogDownloadRefusedHost(ILogger logger, string error);
}
