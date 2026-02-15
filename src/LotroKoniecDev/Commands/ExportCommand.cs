using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.Export;
using LotroKoniecDev.Domain.Core.Monads;
using Microsoft.Extensions.DependencyInjection;
using static LotroKoniecDev.ConsoleWriter;

namespace LotroKoniecDev.Commands;

internal static class ExportCommand
{
    public static int Run(string[] args, IServiceProvider serviceProvider, string dataDir)
    {
        string? datPath = DatPathResolver.Resolve(
            args.Length > 1 ? args[1] : null,
            serviceProvider);

        if (datPath is null)
        {
            return ExitCodes.FileNotFound;
        }

        string outputPath = args.Length > 2
            ? args[2]
            : Path.Combine(dataDir, "exported.txt");

        if (!File.Exists(datPath))
        {
            WriteError($"DAT file not found: {datPath}");
            return ExitCodes.FileNotFound;
        }

        WriteInfo($"Opening: {datPath}");

        using IServiceScope scope = serviceProvider.CreateScope();
        IExporter exporter = scope.ServiceProvider.GetRequiredService<IExporter>();

        Result<ExportSummary> result = exporter.ExportAllTexts(
            datPath,
            outputPath,
            (processed, total) => WriteProgress($"Processing {processed}/{total} files..."));

        if (result.IsFailure)
        {
            WriteError(result.Error.Message);
            return ExitCodes.OperationFailed;
        }

        ExportSummary summary = result.Value;
        Console.WriteLine();
        WriteSuccess("=== EXPORT COMPLETE ===");
        WriteInfo($"Exported {summary.TotalFragments:N0} texts from {summary.TotalTextFiles:N0} files");
        WriteInfo($"Output: {summary.OutputPath}");

        return ExitCodes.Success;
    }
}
