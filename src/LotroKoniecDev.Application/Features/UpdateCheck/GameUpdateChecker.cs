using System.Text.RegularExpressions;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Application.Features.UpdateCheck;

/// <summary>
/// Checks for LOTRO game updates by scraping the release notes forum.
/// </summary>
public sealed partial class GameUpdateChecker : IGameUpdateChecker
{
    private readonly IForumPageFetcher _forumPageFetcher;
    private readonly IVersionFileStore _versionFileStore;

    public GameUpdateChecker(IForumPageFetcher forumPageFetcher, IVersionFileStore versionFileStore)
    {
        _forumPageFetcher = forumPageFetcher ?? throw new ArgumentNullException(nameof(forumPageFetcher));
        _versionFileStore = versionFileStore ?? throw new ArgumentNullException(nameof(versionFileStore));
    }

    public async Task<Result<GameUpdateCheckResult>> CheckForUpdateAsync(string versionFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionFilePath);

        // 1. Fetch the forum page
        Result<string> fetchResult = await _forumPageFetcher.FetchReleaseNotesPageAsync();

        if (fetchResult.IsFailure)
        {
            return Result.Failure<GameUpdateCheckResult>(fetchResult.Error);
        }

        // 2. Parse the latest version from the HTML
        string? currentVersion = ParseLatestVersion(fetchResult.Value);

        if (currentVersion is null)
        {
            return Result.Failure<GameUpdateCheckResult>(DomainErrors.GameUpdateCheck.VersionNotFoundInPage);
        }

        // 3. Read the previously stored version
        Result<string?> readResult = _versionFileStore.ReadLastKnownVersion(versionFilePath);

        if (readResult.IsFailure)
        {
            return Result.Failure<GameUpdateCheckResult>(readResult.Error);
        }

        string? previousVersion = readResult.Value;

        // 4. Compare
        bool updateDetected = !string.Equals(currentVersion, previousVersion, StringComparison.OrdinalIgnoreCase);

        // 5. Save if changed
        if (updateDetected)
        {
            Result saveResult = _versionFileStore.SaveVersion(versionFilePath, currentVersion);

            if (saveResult.IsFailure)
            {
                return Result.Failure<GameUpdateCheckResult>(saveResult.Error);
            }
        }

        return Result.Success(new GameUpdateCheckResult(updateDetected, currentVersion, previousVersion));
    }

    /// <summary>
    /// Extracts the latest game version number from forum page HTML.
    /// The first match is the latest because the forum lists threads in reverse chronological order.
    /// </summary>
    internal static string? ParseLatestVersion(string htmlContent)
    {
        Match match = VersionRegex().Match(htmlContent);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"Update\s+(\d+(?:\.\d+)*)\s+Release\s+Notes", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();
}
