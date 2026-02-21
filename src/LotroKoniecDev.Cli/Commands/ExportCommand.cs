using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Features.Export;
using LotroKoniecDev.Domain.Core.Monads;
using Microsoft.Extensions.DependencyInjection;
using static LotroKoniecDev.Cli.ConsoleWriter;

namespace LotroKoniecDev.Cli.Commands;

internal static class ExportCommand
{
    public static int Run(string[] args, IServiceProvider serviceProvider, string dataDir)
    {
        IDatPathResolver datPathResolver = serviceProvider.GetRequiredService<IDatPathResolver>();
        string? datPath = datPathResolver.Resolve(args.Length > 1 ? args[1] : null);

        if (datPath is null)
        {
            return ExitCodes.FileNotFound;
        }
        
        if (!File.Exists(datPath))
        {
            WriteError($"DAT file not found: {datPath}");
            return ExitCodes.FileNotFound;
        }

        string outputPath = args.Length > 2
            ? args[2]
            : Path.Combine(dataDir, "exported.txt");

        WriteInfo($"Opening: {datPath}");

        using IServiceScope scope = serviceProvider.CreateScope();
        IExporter exporter = scope.ServiceProvider.GetRequiredService<IExporter>();

        Result<ExportSummaryResponse> result = exporter.ExportAllTexts(
            datPath,
            outputPath,
            (processed, total) => WriteProgress($"Processing {processed}/{total} files..."));

        if (result.IsFailure)
        {
            WriteError(result.Error.Message);
            return ExitCodes.OperationFailed;
        }

        ExportSummaryResponse summaryResponse = result.Value;
        Console.WriteLine();
        WriteSuccess("=== EXPORT COMPLETE ===");
        WriteInfo($"Exported {summaryResponse.TotalFragments:N0} texts from {summaryResponse.TotalTextFiles:N0} files");
        WriteInfo($"Output: {summaryResponse.OutputPath}");

        return ExitCodes.Success;
    }
}
