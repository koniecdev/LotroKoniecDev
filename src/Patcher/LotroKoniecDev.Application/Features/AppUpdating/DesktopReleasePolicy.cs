using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace LotroKoniecDev.Application.Features.AppUpdating;

/// <summary>
/// The contract between the release workflow and every app it ever shipped (spec 0016): where the
/// releases are, how a desktop release and its two files are named, which hosts may serve them, and
/// how the installer is started. A shipped app keeps these rules for good, so change them only with
/// the spec and the workflow.
/// </summary>
public static partial class DesktopReleasePolicy
{
    public const string RepositoryOwner = "koniecdev";

    public const string RepositoryName = "LotroKoniecDev";

    public const string TagPrefix = "desktop-v";

    public const long MaxReleaseListBytes = 32 * 1024 * 1024;

    public const long MaxChecksumFileBytes = 1024;

    public const long MaxInstallerBytes = 256 * 1024 * 1024;

    /// <summary>
    /// Windows' "run as administrator" verb, so the admin prompt comes from the app's own start of the
    /// held file and a "No" comes back to the app (spec 0016, "Why runas").
    /// </summary>
    public const string InstallerStartVerb = "runas";

    private const string GitHubHost = "github.com";

    private const string GitHubApiHost = "api.github.com";

    /// <summary>
    /// GitHub serves release files from hosts under this domain and has moved between them before
    /// (objects, then release-assets). A fixed list would lock every installed app out of its only
    /// repair path on the next move.
    /// </summary>
    private const string GitHubContentHostSuffix = ".githubusercontent.com";

    /// <summary>
    /// The first page of the list, never <c>/releases/latest</c>: GitHub's "Latest" is shared with the
    /// server's releases (owner decision on #45).
    /// </summary>
    public static readonly Uri ReleasesListUri =
        new($"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases?per_page=100");

    public static readonly TimeSpan CheckTimeLimit = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan DownloadTimeLimit = TimeSpan.FromMinutes(10);

    public static string InstallerAssetName(DesktopVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return $"LotroPoPolsku-Setup-{version}.exe";
    }

    public static string ChecksumAssetName(DesktopVersion version) => InstallerAssetName(version) + ".sha256";

    /// <summary>The only download address accepted for a release file: GitHub's own release URL.</summary>
    public static Uri DownloadUri(DesktopVersion version, string assetName)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);

        return new Uri(
            $"https://{GitHubHost}/{RepositoryOwner}/{RepositoryName}/releases/download/{TagPrefix}{version}/{assetName}");
    }

    public static bool TryParseTag(string? tagName, [NotNullWhen(true)] out DesktopVersion? version)
    {
        version = null;
        return tagName is not null
               && tagName.StartsWith(TagPrefix, StringComparison.Ordinal)
               && DesktopVersion.TryParse(tagName[TagPrefix.Length..], out version);
    }

    /// <summary>
    /// GitHub over https only (ADR-0067 §7: GitHub is outside our trust boundary). Checked on the
    /// address we ask for and again on the final address after redirects.
    /// </summary>
    public static bool IsAllowedHost(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true }
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Port != 443
            || uri.UserInfo.Length > 0)
        {
            return false;
        }

        string host = uri.IdnHost;
        return string.Equals(host, GitHubHost, StringComparison.OrdinalIgnoreCase)
               || string.Equals(host, GitHubApiHost, StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(GitHubContentHostSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads a <c>sha256sum</c> line: 64 hex characters, a space, a space or <c>*</c>, the file name.
    /// A file that names another file, or holds more than one line, is refused.
    /// </summary>
    public static bool TryParseChecksumFile(
        string? content, string expectedFileName, [NotNullWhen(true)] out string? sha256Hex)
    {
        sha256Hex = null;
        if (content is null)
        {
            return false;
        }

        Match match = ChecksumLinePattern().Match(content.TrimEnd());
        if (!match.Success || !string.Equals(match.Groups["name"].Value, expectedFileName, StringComparison.Ordinal))
        {
            return false;
        }

        sha256Hex = match.Groups["hash"].Value;
        return true;
    }

    /// <summary>
    /// One pinned string, never <c>ProcessStartInfo.ArgumentList</c>: .NET escapes an inner quote as
    /// <c>\"</c>, which Inno Setup does not read, so the log path would arrive broken and Setup would
    /// stop after the app has closed. <c>/NOCANCEL</c>: the app has closed by then, and a cancelled Setup
    /// would leave the player with no app running. <c>/FORCECLOSEAPPLICATIONS</c> relies on the busy gate,
    /// which keeps a patch in this copy of the app from running during an update; a second copy cannot
    /// run, because a second start ends (#1464).
    /// </summary>
    public static string InstallerArguments(string logFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);
        if (logFilePath.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("A Windows path cannot hold a quote.", nameof(logFilePath));
        }

        return $"/SILENT /NOCANCEL /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS \"/LOG={logFilePath}\"";
    }

    [GeneratedRegex(@"\A(?<hash>[0-9a-fA-F]{64}) [ *](?<name>[^\r\n]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLinePattern();
}
