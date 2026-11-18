using FluentValidation;

namespace LotroKoniecDev.Application.Features.AppUpdating;

/// <summary>
/// Accepts only GitHub's own release addresses for this version's two files, so the command cannot be
/// pointed at anything the update check would not have offered.
/// </summary>
public sealed class InstallAppUpdateCommandValidator : AbstractValidator<InstallAppUpdateCommand>
{
    public InstallAppUpdateCommandValidator()
    {
        RuleFor(command => command.Version)
            .NotNull()
            .Must(version => version is { IsDevelopmentBuild: false })
            .WithMessage("A developer build (0.0.0) is never installed as an update.");

        RuleFor(command => command.InstallerUri)
            .NotNull()
            .Must((command, uri) => IsReleaseFile(uri, command.Version, DesktopReleasePolicy.InstallerAssetName))
            .WithMessage("The installer address must be this version's installer on GitHub's release download URL.")
            .When(command => command.Version is not null);

        RuleFor(command => command.ChecksumUri)
            .NotNull()
            .Must((command, uri) => IsReleaseFile(uri, command.Version, DesktopReleasePolicy.ChecksumAssetName))
            .WithMessage("The SHA-256 file address must be this version's SHA-256 file on GitHub's release download URL.")
            .When(command => command.Version is not null);
    }

    /// <summary>
    /// <see cref="Uri.Equals(object?)"/> ignores the user-info part, so the host rule is checked as well.
    /// </summary>
    private static bool IsReleaseFile(Uri? uri, DesktopVersion version, Func<DesktopVersion, string> assetName) =>
        uri is not null
        && DesktopReleasePolicy.IsAllowedHost(uri)
        && uri == DesktopReleasePolicy.DownloadUri(version, assetName(version));
}
