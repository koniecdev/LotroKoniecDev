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

        // A folder found by scanning the drive, or data/ under the current folder, is not a known
        // install: anyone who can write there could have put a DAT and a launcher in it. So we never
        // use it without asking (AUDIT-SEC-02). Nothing checks the launcher itself, so this question
        // is the guard before launch (ADR-0069).
        if (location.Source is DatFileSource.DiskScan or DatFileSource.LocalFallback
            && !ConfirmUnknownLocation(location.Source))
        {
            WriteError("Location rejected. Provide the DAT file path explicitly with -d.");
            return null;
        }

        return location.Path;
    }

    private static bool ConfirmUnknownLocation(DatFileSource source)
    {
        Console.WriteLine();
        WriteWarning(source is DatFileSource.DiskScan
            ? "This installation was found by a drive scan, not a known install source."
            : "This installation is the data/ folder under the current folder, not a known install source.");
        WriteWarning("On launch, LotroLauncher.exe from this folder is started with this tool's rights, and nothing checks it.");
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
                Console.WriteLine("      [!] found by drive scan — make sure this is your real install; on launch, its LotroLauncher.exe starts unchecked");
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
