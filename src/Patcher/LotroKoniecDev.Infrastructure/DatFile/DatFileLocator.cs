using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Enums;
using Microsoft.Win32;

namespace LotroKoniecDev.Infrastructure.DatFile;

public sealed class DatFileLocator : IDatFileLocator
{
    private const string DatFileName = "client_local_English.dat";

    private static readonly string SsgPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "StandingStoneGames",
        "The Lord of the Rings Online",
        DatFileName);

    private static readonly string SteamPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "Steam", "steamapps", "common",
        "The Lord of the Rings Online",
        DatFileName);

    private static readonly string LocalFallbackPath =
        Path.GetFullPath(Path.Combine("data", DatFileName));

    private static readonly string[] RegistryKeys =
    [
        @"SOFTWARE\StandingStoneGames\The Lord of the Rings Online",
        @"SOFTWARE\WOW6432Node\StandingStoneGames\The Lord of the Rings Online",
        @"SOFTWARE\WOW6432Node\Turbine\The Lord of the Rings Online"
    ];

    private static readonly string[] RegistryValueNames =
    [
        "InstallLocation",
        "GameFolder",
        "Install Location"
    ];

    public Result<IReadOnlyList<DatFileLocation>> LocateAll(Action<string>? progress = null)
    {
        List<DatFileLocation> locations = [];

        TryAddIfExists(locations, SsgPath, DatFileSource.StandingStoneGames,
            "Standing Stone Games (default)");

        TryAddIfExists(locations, SteamPath, DatFileSource.Steam, "Steam");

        TryAddFromRegistry(locations);

        if (locations.Count == 0)
        {
            progress?.Invoke("No installation found in standard locations. Scanning drives...");
            ScanFixedDrives(locations, progress);
        }

        TryAddIfExists(locations, LocalFallbackPath, DatFileSource.LocalFallback,
            "Local (data/)");

        if (locations.Count == 0)
        {
            return Result.Failure<IReadOnlyList<DatFileLocation>>(
                DomainErrors.DatFileLocation.NoneFound);
        }

        return Result.Success<IReadOnlyList<DatFileLocation>>(locations);
    }

    private static void TryAddIfExists(
        List<DatFileLocation> locations,
        string path,
        DatFileSource source,
        string displayName)
    {
        if (File.Exists(path) && !locations.Any(l => PathsEqual(l.Path, path)))
        {
            locations.Add(new DatFileLocation(path, source, displayName));
        }
    }

    private static void TryAddFromRegistry(List<DatFileLocation> locations)
    {
        foreach (string keyPath in RegistryKeys)
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath);
                if (key is null)
                {
                    continue;
                }

                foreach (string valueName in RegistryValueNames)
                {
                    if (key.GetValue(valueName) is not string installPath)
                    {
                        continue;
                    }

                    string datPath = Path.Combine(installPath, DatFileName);
                    TryAddIfExists(locations, datPath, DatFileSource.Registry,
                        $"Registry ({Path.GetFileName(installPath)})");
                }
            }
            catch
            {
                // Registry access may fail - continue silently
            }
        }
    }

    private static void ScanFixedDrives(
        List<DatFileLocation> locations,
        Action<string>? progress)
    {
        IEnumerable<DriveInfo> drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady);

        foreach (DriveInfo drive in drives)
        {
            progress?.Invoke($"Scanning {drive.Name} for LOTRO installation...");

            try
            {
                IEnumerable<string> files = Directory.EnumerateFiles(
                    drive.RootDirectory.FullName,
                    DatFileName,
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.System
                    });

                foreach (string filePath in files)
                {
                    string dirName = Path.GetFileName(Path.GetDirectoryName(filePath)!) ?? "Unknown";
                    TryAddIfExists(locations, filePath, DatFileSource.DiskScan,
                        $"Disk scan ({dirName})");
                }
            }
            catch
            {
                // Drive scan failed - continue to next
            }
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);
}
