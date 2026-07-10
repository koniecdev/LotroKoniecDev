using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Enums;
using static LotroKoniecDev.Cli.ConsoleWriter;

namespace LotroKoniecDev.Cli;

internal sealed class DatPathResolver : IDatPathResolver
{
    private readonly IDatFileLocator _datFileLocator;

    public DatPathResolver(IDatFileLocator datFileLocator)
    {
        _datFileLocator = datFileLocator;
    }

    public string? Resolve(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        Result<IReadOnlyList<DatFileLocation>> result = _datFileLocator.LocateAll(WriteInfo);

        if (result.IsFailure)
        {
            WriteError(result.Error.Message);
            return null;
        }

        IReadOnlyList<DatFileLocation> locations = result.Value;

        if (locations.Count != 1)
        {
            return PromptUserChoice(locations);
        }

        DatFileLocation location = locations[0];
        WriteInfo($"Found LOTRO: {location.DisplayName}");
        WriteInfo($"  {location.Path}");

        // A drive-scanned folder is an unauthenticated source — anyone with write access could
        // have planted a DAT + launcher pair there, so it is never used silently (AUDIT-SEC-02).
        if (location.Source is DatFileSource.DiskScan && !ConfirmScannedLocation())
        {
            WriteError("Scanned location rejected. Provide the DAT file path explicitly with -d.");
            return null;
        }

        return location.Path;
    }

    private static bool ConfirmScannedLocation()
    {
        Console.WriteLine();
        WriteWarning("This installation was found by a drive scan, not a known install source.");
        WriteWarning("On launch, LotroLauncher.exe from this folder will be started.");
        Console.Write("Use this installation? [y/N]: ");

        string? input = Console.ReadLine();
        return string.Equals(input?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
    }

    private static string? PromptUserChoice(IReadOnlyList<DatFileLocation> locations)
    {
        Console.WriteLine();
        WriteInfo("Multiple LOTRO installations found:");
        Console.WriteLine();

        for (int i = 0; i < locations.Count; i++)
        {
            Console.WriteLine($"  [{i + 1}] {locations[i].DisplayName}");
            Console.WriteLine($"      {locations[i].Path}");

            if (locations[i].Source is DatFileSource.DiskScan)
            {
                Console.WriteLine("      [!] found by drive scan — make sure this is your real install");
            }
        }

        Console.WriteLine();
        Console.Write($"Choose installation (1-{locations.Count}): ");

        string? input = Console.ReadLine();

        if (int.TryParse(input, out int choice) &&
            choice >= 1 && choice <= locations.Count)
        {
            return locations[choice - 1].Path;
        }

        WriteError("Invalid choice.");
        return null;
    }
}
