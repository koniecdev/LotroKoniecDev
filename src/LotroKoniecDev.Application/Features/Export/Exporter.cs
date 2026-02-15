using System.Text;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.Application.Features.Export;

/// <summary>
/// Exports text fragments from LOTRO DAT files to a translation-ready format.
/// </summary>
public sealed class Exporter : IExporter
{
    private const int ProgressReportInterval = 500;

    private readonly IDatFileHandler _datFileHandler;

    public Exporter(IDatFileHandler datFileHandler)
    {
        _datFileHandler = datFileHandler ?? throw new ArgumentNullException(nameof(datFileHandler));
    }

    public Result<ExportSummary> ExportAllTexts(
        string datFilePath,
        string outputPath,
        Action<int, int>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        // Open DAT file
        Result<int> openResult = _datFileHandler.Open(datFilePath);
        if (openResult.IsFailure)
        {
            return Result.Failure<ExportSummary>(openResult.Error);
        }

        int handle = openResult.Value;

        try
        {
            return ProcessExport(handle, outputPath, progress);
        }
        finally
        {
            _datFileHandler.Close(handle);
        }
    }

    private Result<ExportSummary> ProcessExport(
        int handle,
        string outputPath,
        Action<int, int>? progress)
    {
        Dictionary<int, (int Size, int Iteration)> fileSizes = _datFileHandler.GetAllSubfileSizes(handle);

        int totalTextFiles = fileSizes.Count(kv => SubFile.IsTextFile(kv.Key));

        try
        {
            using StreamWriter writer = new(outputPath, false, Encoding.UTF8);

            WriteHeader(writer);

            int processedFiles = 0;
            int totalFragments = 0;

            foreach ((int fileId, (int size, int _)) in fileSizes)
            {
                if (!SubFile.IsTextFile(fileId))
                {
                    continue;
                }

                Result<int> exportResult = ExportSubfile(handle, fileId, size, writer);

                if (exportResult.IsSuccess)
                {
                    totalFragments += exportResult.Value;
                }

                processedFiles++;

                if (processedFiles % ProgressReportInterval == 0)
                {
                    progress?.Invoke(processedFiles, totalTextFiles);
                }
            }

            return Result.Success(new ExportSummary(
                processedFiles,
                totalFragments,
                outputPath));
        }
        catch (Exception ex)
        {
            return Result.Failure<ExportSummary>(
                DomainErrors.Export.CannotCreateOutputFile(outputPath, ex.Message));
        }
    }

    private Result<int> ExportSubfile(int handle, int fileId, int size, StreamWriter writer)
    {
        Result<SubFile> loadResult = _datFileHandler.LoadSubFile(handle, fileId, size);

        if (loadResult.IsFailure)
        {
            return Result.Failure<int>(loadResult.Error);
        }

        SubFile subFile = loadResult.Value;
        int fragmentCount = 0;

        foreach ((ulong fragmentId, Fragment fragment) in subFile.Fragments)
        {
            WriteFragment(writer, fileId, fragmentId, fragment);
            fragmentCount++;
        }

        return Result.Success(fragmentCount);
    }

    private static void WriteHeader(StreamWriter writer)
    {
        writer.WriteLine("# LOTRO Text Export - Ready for Translation");
        writer.WriteLine("# Format: file_id||gossip_id||text||args_order||args_id||approved");
        writer.WriteLine("#");
        writer.WriteLine("# Translation instructions:");
        writer.WriteLine("#   1. Replace English text with Polish translation");
        writer.WriteLine("#   2. DO NOT modify <--DO_NOT_TOUCH!--> markers - they are variable placeholders");
        writer.WriteLine("#   3. args_order/args_id - leave as NULL unless changing argument order");
        writer.WriteLine("#   4. Remove lines you don't translate (or leave them - identical lines are ignored)");
        writer.WriteLine("#");
    }

    private static void WriteFragment(
        StreamWriter writer,
        int fileId,
        ulong fragmentId,
        Fragment fragment)
    {
        string text = string.Join(DatFileConstants.PieceSeparator, fragment.Pieces);

        // Escape newlines for single-line storage
        text = text.Replace("\r", "\\r").Replace("\n", "\\n");

        // Generate default args_order and args_id if fragment has arguments
        string argsOrder = "NULL";
        string argsId = "NULL";

        if (fragment.HasArguments)
        {
            IEnumerable<string> order = Enumerable
                .Range(1, fragment.ArgRefs.Count)
                .Select(i => i.ToString());

            argsOrder = string.Join("-", order);
            argsId = argsOrder; // Default: same order
        }

        // Format: file_id||gossip_id||text||args_order||args_id||approved
        writer.WriteLine($"{fileId}||{fragmentId}||{text}||{argsOrder}||{argsId}||1");
    }
}
