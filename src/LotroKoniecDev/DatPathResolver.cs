using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFileServices;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using Microsoft.Extensions.DependencyInjection;
using static LotroKoniecDev.ConsoleWriter;

namespace LotroKoniecDev;

internal sealed class DatPathResolver : IDatPathResolver
{
    public async Task<string?> Resolve(string? explicitPath, IServiceProvider serviceProvider)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        IDatFileLocator locator = serviceProvider.GetRequiredService<IDatFileLocator>();

        Result<IReadOnlyList<DatFileLocation>> result = locator.LocateAll(WriteInfo);

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
        return location.Path;
    }

    private async Task<string?> PromptUserChoice(IReadOnlyList<DatFileLocation> locations)
    {
        Console.WriteLine();
        WriteInfo("Multiple LOTRO installations found:");
        Console.WriteLine();

        for (int i = 0; i < locations.Count; i++)
        {
            Console.WriteLine($"  [{i + 1}] {locations[i].DisplayName}");
            Console.WriteLine($"      {locations[i].Path}");
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
