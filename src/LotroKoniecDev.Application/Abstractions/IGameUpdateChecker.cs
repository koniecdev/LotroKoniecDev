using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Checks if LOTRO has released a game update by comparing the forum version with a locally stored version.
/// </summary>
public interface IGameUpdateChecker
{
    Task<Result<GameUpdateCheckSummary>> CheckForUpdateAsync(string versionFilePath);
}

/// <summary>
/// Contains the result of a game update check.
/// </summary>
/// <param name="UpdateDetected">True if a new game version was found compared to the stored version.</param>
/// <param name="CurrentVersion">The latest version string found on the LOTRO forums.</param>
/// <param name="PreviousVersion">The previously stored version string, or null if no version was stored.</param>
public sealed record GameUpdateCheckSummary(
    bool UpdateDetected,
    string CurrentVersion,
    string? PreviousVersion);
